using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace TriSwitch
{
    public enum Language { English, Russian, Ukrainian }

    public static class Layouts
    {
        public static readonly string[] Names = { "EN", "RU", "UK" };
        private static readonly string[] Lower = {
            "`qwertyuiop[]asdfghjkl;'zxcvbnm,./",
            "ёйцукенгшщзхъфывапролджэячсмитьбю.",
            "'йцукенгшщзхїфівапролджєячсмитьбю." };
        private static readonly string[] Upper = {
            "~QWERTYUIOP{}ASDFGHJKL:\"ZXCVBNM<>?",
            "ЁЙЦУКЕНГШЩЗХЪФЫВАПРОЛДЖЭЯЧСМИТЬБЮ,",
            "₴ЙЦУКЕНГШЩЗХЇФІВАПРОЛДЖЄЯЧСМИТЬБЮ," };

        public static string Convert(string text, Language from, Language to)
        {
            if (from == to) return text;
            var result = new StringBuilder(text.Length);
            foreach (char c in text)
            {
                int index = Lower[(int)from].IndexOf(c);
                if (index >= 0) result.Append(Lower[(int)to][index]);
                else
                {
                    index = Upper[(int)from].IndexOf(c);
                    result.Append(index >= 0 ? Upper[(int)to][index] : c);
                }
            }
            return result.ToString();
        }

        public static Language? FromHandle(IntPtr layout)
        {
            switch ((int)(layout.ToInt64() & 0xffff))
            {
                case 0x0409: return Language.English;
                case 0x0419: return Language.Russian;
                case 0x0422: return Language.Ukrainian;
                default: return null;
            }
        }
    }

    // Conservative Hunspell membership reader: dictionary entries and one affix,
    // including cross-product prefix+suffix. No speculative language scoring.
    public sealed class WordDictionary
    {
        private sealed class Rule
        {
            public string Flag, Strip, Add;
            public bool Prefix, Cross;
            public Regex Condition;
            public string Reverse(string word)
            {
                if (Prefix ? !word.StartsWith(Add, StringComparison.Ordinal) : !word.EndsWith(Add, StringComparison.Ordinal)) return null;
                string root = Prefix ? Strip + word.Substring(Add.Length) : word.Substring(0, word.Length - Add.Length) + Strip;
                return Condition.IsMatch(root) ? root : null;
            }
        }
        private readonly Dictionary<string, string> words = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly List<Rule> rules = new List<Rule>();
        private readonly Dictionary<string, bool> cache = new Dictionary<string, bool>();
        private readonly HashSet<string> blocked = new HashSet<string>(StringComparer.Ordinal);
        private string needAffix, forbidden, onlyCompound;
        public int Count { get { return words.Count; } }

        public WordDictionary(string path)
        {
            var cross = new Dictionary<string, bool>();
            foreach (string line in File.ReadLines(Path.ChangeExtension(path, ".aff"), Encoding.UTF8))
            {
                string[] p = Regex.Split(line.Trim(), @"\s+");
                if (p.Length < 2) continue;
                if (p[0] == "NEEDAFFIX") needAffix = p[1];
                if (p[0] == "FORBIDDENWORD") forbidden = p[1];
                if (p[0] == "ONLYINCOMPOUND") onlyCompound = p[1];
                if (p[0] != "PFX" && p[0] != "SFX") continue;
                if (p.Length == 4) { cross[p[0] + p[1]] = p[2] == "Y"; continue; }
                if (p.Length < 5) continue;
                string add = p[3].Split('/')[0];
                bool isCross; cross.TryGetValue(p[0] + p[1], out isCross);
                rules.Add(new Rule { Flag = p[1], Prefix = p[0] == "PFX", Cross = isCross,
                    Strip = p[2] == "0" ? "" : p[2], Add = add == "0" ? "" : add,
                    Condition = new Regex(p[0] == "PFX" ? "^(?:" + p[4] + ")" : "(?:" + p[4] + ")$", RegexOptions.CultureInvariant) });
            }
            foreach (string raw in File.ReadLines(path, Encoding.UTF8).Skip(1))
            {
                string entry = raw.Split('\t', ' ')[0];
                int slash = entry.IndexOf('/');
                string word = (slash < 0 ? entry : entry.Substring(0, slash)).ToLowerInvariant().Replace('’', '\'').Replace('ʼ', '\'');
                string flags = slash < 0 ? "" : entry.Substring(slash + 1);
                if (Has(flags, forbidden) || Has(flags, onlyCompound)) { blocked.Add(word); continue; }
                string old;
                words[word] = words.TryGetValue(word, out old) ? old + flags : flags;
            }
        }
        private static bool Has(string flags, string flag) { return flag != null && flags.Contains(flag); }
        public bool Contains(string value)
        {
            string word = value.ToLowerInvariant().Replace('’', '\'').Replace('ʼ', '\'');
            if (word.Length < 2 || blocked.Contains(word)) return false;
            bool found;
            if (cache.TryGetValue(word, out found)) return found;
            string flags;
            found = words.TryGetValue(word, out flags) && !Has(flags, needAffix);
            if (!found)
            {
                foreach (Rule rule in rules)
                {
                    string root = rule.Reverse(word);
                    if (root == null || blocked.Contains(root)) continue;
                    if (words.TryGetValue(root, out flags) && flags.Contains(rule.Flag)) { found = true; break; }
                    if (!rule.Prefix || !rule.Cross) continue;
                    foreach (Rule suffix in rules.Where(r => !r.Prefix && r.Cross))
                    {
                        string stem = suffix.Reverse(root);
                        if (stem != null && !blocked.Contains(stem) && words.TryGetValue(stem, out flags) && flags.Contains(rule.Flag) && flags.Contains(suffix.Flag))
                        { found = true; break; }
                    }
                    if (found) break;
                }
            }
            if (cache.Count > 4096) cache.Clear();
            cache[word] = found;
            return found;
        }
    }

    public sealed class Suggestion
    {
        public Language Language;
        public string Text;
        public bool Custom, PreserveLayout;
    }

    public sealed class Detector
    {
        private readonly WordDictionary[] dictionaries;
        public readonly HashSet<string> Ignored = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public Detector(string directory)
        {
            dictionaries = new[] {
                new WordDictionary(Path.Combine(directory, "en", "en_US.dic")),
                new WordDictionary(Path.Combine(directory, "ru_RU", "ru_RU.dic")),
                new WordDictionary(Path.Combine(directory, "uk_UA", "uk_UA.dic")) };
        }
        public int Count { get { return dictionaries.Sum(d => d.Count); } }
        public bool Known(string word, Language language) { return dictionaries[(int)language].Contains(word); }
        private static string TrimEnd(string word) { return word.TrimEnd('.', ',', '!', '?', ':', ';', ')', '"', '»'); }
        public Suggestion Suggest(string word, Language source, Func<string, Language, Language, string> convert)
        {
            string bare = TrimEnd(word);
            if (bare.Length < 4 || word.Length > 64 || word.Any(char.IsDigit) || word.Contains("@") || word.Contains("_") || word.Contains("://") || Ignored.Contains(bare)) return null;
            // A valid word in any supported language must not be overwritten.
            if (dictionaries.Any(d => d.Contains(bare))) return null;
            var candidates = new List<Suggestion>();
            foreach (Language target in Enum.GetValues(typeof(Language)))
            {
                if (target == source) continue;
                string converted = convert(word, source, target);
                if (converted != word && Known(TrimEnd(converted), target)) candidates.Add(new Suggestion { Language = target, Text = converted });
                else if (bare != word)
                {
                    converted = convert(bare, source, target);
                    if (converted != bare && Known(converted, target)) candidates.Add(new Suggestion { Language = target, Text = converted + word.Substring(bare.Length) });
                }
            }
            return candidates.Count == 1 ? candidates[0] : null;
        }
    }

    public sealed class WordBuffer
    {
        public string Word = "", Suffix = "";
        public Language Language;
        public bool Valid;
        private bool suppressed;
        public void Clear() { Word = ""; Suffix = ""; Valid = false; suppressed = false; }
        public void Append(char c, Language language)
        {
            if (suppressed) return;
            if (Suffix.Length > 0 || (Valid && Language != language)) Clear();
            if (Word.Length >= 64) { Clear(); suppressed = true; return; }
            Word += c; Language = language; Valid = true;
        }
        public void Space() { if (suppressed || Suffix.Length > 0) Clear(); else if (Valid) Suffix = " "; }
        public void Backspace()
        {
            if (Suffix.Length > 0) Suffix = "";
            else if (Word.Length > 0) Word = Word.Substring(0, Word.Length - 1);
            Valid = Word.Length > 0;
        }
    }
}
