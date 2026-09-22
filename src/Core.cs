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
                return Prefix ? Strip + word.Substring(Add.Length) : word.Substring(0, word.Length - Add.Length) + Strip;
            }
        }
        // Walk only affixes that match the edge of this word. Spelling checks
        // perform many membership lookups; scanning every Hunspell rule for
        // every candidate would block keyboard input on the UI thread.
        private sealed class RuleIndex
        {
            private readonly Dictionary<char, RuleIndex> next = new Dictionary<char, RuleIndex>();
            private readonly List<Rule> entries = new List<Rule>();
            public void Add(Rule rule)
            {
                RuleIndex node = this;
                for (int i = 0; i < rule.Add.Length; i++)
                {
                    char c = rule.Add[rule.Prefix ? i : rule.Add.Length - i - 1];
                    RuleIndex child;
                    if (!node.next.TryGetValue(c, out child)) node.next[c] = child = new RuleIndex();
                    node = child;
                }
                node.entries.Add(rule);
            }
            public IEnumerable<Rule> Matching(string word, bool prefix)
            {
                RuleIndex node = this;
                foreach (Rule rule in node.entries) yield return rule;
                for (int i = 0; i < word.Length; i++)
                {
                    if (!node.next.TryGetValue(word[prefix ? i : word.Length - i - 1], out node)) yield break;
                    foreach (Rule rule in node.entries) yield return rule;
                }
            }
        }
        private readonly Dictionary<string, string> words = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly RuleIndex prefixes = new RuleIndex(), suffixes = new RuleIndex();
        private readonly Dictionary<string, bool> cache = new Dictionary<string, bool>();
        private readonly HashSet<string> blocked = new HashSet<string>(StringComparer.Ordinal);
        private string needAffix, forbidden, onlyCompound;
        public int Count { get { return words.Count; } }

        public WordDictionary(string path, string supplementPath = null)
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
                var rule = new Rule { Flag = p[1], Prefix = p[0] == "PFX", Cross = isCross,
                    Strip = p[2] == "0" ? "" : p[2], Add = add == "0" ? "" : add,
                    Condition = new Regex(p[0] == "PFX" ? "^(?:" + p[4] + ")" : "(?:" + p[4] + ")$", RegexOptions.CultureInvariant) };
                (rule.Prefix ? prefixes : suffixes).Add(rule);
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
            if (supplementPath != null && File.Exists(supplementPath))
            {
                foreach (string raw in File.ReadLines(supplementPath, Encoding.UTF8))
                {
                    string word = raw.Trim().ToLowerInvariant().Replace('’', '\'').Replace('ʼ', '\'');
                    if (word.Length == 0 || word.StartsWith("#", StringComparison.Ordinal) || blocked.Contains(word) || words.ContainsKey(word)) continue;
                    words.Add(word, "");
                }
            }
        }
        private static bool Has(string flags, string flag) { return flag != null && flags.Contains(flag); }
        public bool Contains(string value)
        {
            string word = value.ToLowerInvariant().Replace('’', '\'').Replace('ʼ', '\'');
            return ContainsNormalized(word, true);
        }
        internal bool ContainsNormalized(string word, bool remember)
        {
            if (word.Length < 2 || blocked.Contains(word)) return false;
            bool found;
            if (cache.TryGetValue(word, out found)) return found;
            string flags;
            found = words.TryGetValue(word, out flags) && !Has(flags, needAffix);
            if (!found) found = Matches(word, prefixes.Matching(word, true)) || Matches(word, suffixes.Matching(word, false));
            if (remember)
            {
                if (cache.Count > 4096) cache.Clear();
                cache[word] = found;
            }
            return found;
        }
        private bool Matches(string word, IEnumerable<Rule> matching)
        {
            string flags;
            foreach (Rule rule in matching)
            {
                string root = rule.Reverse(word);
                if (blocked.Contains(root)) continue;
                if (words.TryGetValue(root, out flags) && flags.Contains(rule.Flag) && rule.Condition.IsMatch(root)) return true;
                if (!rule.Prefix || !rule.Cross) continue;
                foreach (Rule suffix in suffixes.Matching(root, false))
                {
                    if (!suffix.Cross) continue;
                    string stem = suffix.Reverse(root);
                    if (!blocked.Contains(stem) && words.TryGetValue(stem, out flags) && flags.Contains(rule.Flag) && flags.Contains(suffix.Flag)
                        && rule.Condition.IsMatch(root) && suffix.Condition.IsMatch(stem)) return true;
                }
            }
            return false;
        }
    }

    public sealed class Suggestion
    {
        public Language Language;
        public string Text;
        public bool Custom, PreserveLayout, Spelling;
    }

    public sealed class Detector
    {
        // Two-letter dictionary entries include many initials and abbreviations.
        // Only common function words are eligible for automatic correction at
        // this length; longer words still use the complete dictionaries.
        private static readonly HashSet<string>[] ShortWords = {
            new HashSet<string>("am an as at be by do go he if in is it me my no of on or so to up us we".Split(' '), StringComparer.OrdinalIgnoreCase),
            new HashSet<string>("во до за из ко на об от по со да не ни но ну ты мы вы он ей её ее им их уж бы же ли то".Split(' '), StringComparer.OrdinalIgnoreCase),
            new HashSet<string>("до за зі із на об од по ув не ні та чи що як бо би же то це ця ці ми ти ви їй її їм їх".Split(' '), StringComparer.OrdinalIgnoreCase) };
        private readonly WordDictionary[] dictionaries;
        private readonly SpellChecker[] spellCheckers;
        public readonly HashSet<string> Ignored = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public readonly HashSet<string> SpellingIgnored = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public Detector(string directory)
        {
            dictionaries = new[] {
                new WordDictionary(Path.Combine(directory, "en", "en_US.dic"), Path.Combine(directory, "supplemental", "en.txt")),
                new WordDictionary(Path.Combine(directory, "ru_RU", "ru_RU.dic"), Path.Combine(directory, "supplemental", "ru.txt")),
                new WordDictionary(Path.Combine(directory, "uk_UA", "uk_UA.dic"), Path.Combine(directory, "supplemental", "uk.txt")) };
            spellCheckers = dictionaries.Select((dictionary, index) => new SpellChecker(dictionary, (Language)index)).ToArray();
        }
        public int Count { get { return dictionaries.Sum(d => d.Count); } }
        public bool Known(string word, Language language) { return dictionaries[(int)language].Contains(word); }
        public Suggestion SuggestSpelling(string word, Language language)
        {
            if (String.IsNullOrEmpty(word) || !Enum.IsDefined(typeof(Language), language)) return null;
            return spellCheckers[(int)language].Suggest(word, Ignored, SpellingIgnored);
        }
        private bool CanCorrect(string word, Language language)
        {
            return (word.Length >= 3 || (word.Length == 2 && ShortWords[(int)language].Contains(word))) && Known(word, language);
        }
        private static string TrimEnd(string word) { return word.TrimEnd('.', ',', '!', '?', ':', ';', ')', '"', '»'); }
        public Suggestion Suggest(string word, Language source, Func<string, Language, Language, string> convert, Language preferredCyrillic = Language.Russian)
        {
            string bare = TrimEnd(word);
            if (word.Length < 2 || word.Length > 64 || word.Any(char.IsDigit) || word.Contains("@") || word.Contains("_") || word.Contains("://") || Ignored.Contains(word) || Ignored.Contains(bare)) return null;
            // A valid word in any supported language must not be overwritten.
            if (dictionaries.Any(d => d.Contains(bare))) return null;
            var candidates = new List<Suggestion>();
            foreach (Language target in Enum.GetValues(typeof(Language)))
            {
                if (target == source) continue;
                string converted = convert(word, source, target);
                if (converted != word && CanCorrect(TrimEnd(converted), target)) candidates.Add(new Suggestion { Language = target, Text = converted });
                if (bare != word)
                {
                    converted = convert(bare, source, target);
                    string text = converted + word.Substring(bare.Length);
                    if (converted != bare && CanCorrect(converted, target))
                    {
                        // If both interpretations identify the same word, keep
                        // the punctuation as typed (e.g. ? must not become ,).
                        Suggestion sameWord = candidates.FirstOrDefault(c => c.Language == target && TrimEnd(c.Text) == converted);
                        if (sameWord != null) sameWord.Text = text;
                        else candidates.Add(new Suggestion { Language = target, Text = text });
                    }
                }
            }
            if (candidates.Count == 1) return candidates[0];
            // Use the selected priority (Russian by default) when both
            // Cyrillic layouts produce a word.
            // More than one interpretation within the same language (such as
            // a punctuation key) remains ambiguous and is not auto-corrected.
            if (candidates.Count == 2 && candidates.Any(c => c.Language == Language.Russian)
                && candidates.Any(c => c.Language == Language.Ukrainian))
                return candidates.FirstOrDefault(c => c.Language == preferredCyrillic) ?? candidates[0];
            return null;
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
