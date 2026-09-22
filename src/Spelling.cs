using System;
using System.Collections.Generic;

namespace TriSwitch
{
    // Local, deliberately conservative spelling correction. The general search
    // accepts exactly one dictionary word at edit distance one; it never ranks
    // competing words. A small explicit list covers familiar Russian typos.
    internal sealed class SpellChecker
    {
        private static readonly string[] Alphabets = {
            "abcdefghijklmnopqrstuvwxyz",
            "абвгдеёжзийклмнопрстуфхцчшщъыьэюя",
            "абвгґдеєжзиіїйклмнопрстуфхцчшщьюя" };
        private static readonly Dictionary<string, string> CommonRussianTypos = new Dictionary<string, string>(StringComparer.Ordinal) {
            { "привт", "привет" }, { "програма", "программа" },
            { "пожалуста", "пожалуйста" }, { "здраствуйте", "здравствуйте" } };
        private static readonly char[] TrailingPunctuation = { '.', ',', '!', '?', ':', ';', ')', ']', '}', '"', '»', '”', '…' };
        private readonly WordDictionary dictionary;
        private readonly Language language;
        private readonly string alphabet;
        private readonly Dictionary<string, string> cache = new Dictionary<string, string>(StringComparer.Ordinal);

        public SpellChecker(WordDictionary dictionary, Language language)
        {
            this.dictionary = dictionary; this.language = language; alphabet = Alphabets[(int)language];
        }

        public Suggestion Suggest(string word, HashSet<string> ignored)
        {
            // Bounds are checked before allocating or enumerating candidates.
            if (word.Length > 64) return null;
            string bare = word.TrimEnd(TrailingPunctuation);
            if (bare.Length < 4 || bare.Length > 32 || ignored.Contains(word) || ignored.Contains(bare)) return null;
            string lower = bare.ToLowerInvariant();
            bool allLower = true, allUpper = true, title = true;
            for (int i = 0; i < bare.Length; i++)
            {
                // Restricts the token to its language's letters: identifiers,
                // mixed scripts, email addresses, URLs and punctuation inside
                // words are excluded, as are apostrophes and hyphens for now.
                if (alphabet.IndexOf(lower[i]) < 0) return null;
                bool upper = char.IsUpper(bare[i]);
                allLower &= !upper; allUpper &= upper; title &= i == 0 ? upper : !upper;
            }
            if (!allLower && !allUpper && !title) return null;
            if (dictionary.ContainsNormalized(lower, true)) return null;
            string corrected;
            if (!cache.TryGetValue(lower, out corrected))
            {
                if (language != Language.Russian || !CommonRussianTypos.TryGetValue(lower, out corrected)
                    || !dictionary.ContainsNormalized(corrected, false)) corrected = FindUnique(lower);
                if (cache.Count >= 1024) cache.Clear();
                cache[lower] = corrected;
            }
            if (corrected == null || ignored.Contains(corrected)) return null;
            if (allUpper) corrected = corrected.ToUpperInvariant();
            else if (title) corrected = char.ToUpperInvariant(corrected[0]) + corrected.Substring(1);
            return new Suggestion { Text = corrected + word.Substring(bare.Length), Language = language, PreserveLayout = true, Spelling = true };
        }

        private string FindUnique(string word)
        {
            string match = null;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (string candidate in Candidates(word))
            {
                if (candidate.Length < 4 || candidate == word || !seen.Add(candidate)
                    || !dictionary.ContainsNormalized(candidate, false)) continue;
                if (match != null) return null;
                match = candidate;
            }
            return match;
        }

        private IEnumerable<string> Candidates(string word)
        {
            // Delete an extra letter, or swap two neighbouring letters.
            for (int i = 0; i < word.Length; i++)
            {
                yield return word.Remove(i, 1);
                if (i + 1 < word.Length && word[i] != word[i + 1])
                    yield return word.Substring(0, i) + word[i + 1] + word[i] + word.Substring(i + 2);
            }
            // A repeated 2- or 3-letter fragment is a common typing accident,
            // e.g. пропущенныные -> пропущенные. It obeys the same uniqueness
            // requirement as the single-letter edits above and below.
            for (int length = 2; length <= 3; length++)
                for (int i = 0; i + length * 2 <= word.Length; i++)
                    if (String.CompareOrdinal(word, i, word, i + length, length) == 0)
                        yield return word.Remove(i, length);
            for (int i = 0; i <= word.Length; i++)
            {
                string before = word.Substring(0, i), after = word.Substring(i);
                foreach (char letter in alphabet)
                {
                    yield return before + letter + after;
                    if (i < word.Length && letter != word[i]) yield return before + letter + after.Substring(1);
                }
            }
        }
    }
}
