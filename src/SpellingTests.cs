using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace TriSwitch
{
    internal static class SpellingTests
    {
        public static void Run(Action<string, Action> test, Detector detector)
        {
            test("Spelling: explicit common Russian typos", delegate
            {
                Correct(detector, "привт", Language.Russian, "привет");
                Correct(detector, "програма", Language.Russian, "программа");
                Correct(detector, "пожалуста", Language.Russian, "пожалуйста");
                Correct(detector, "здраствуйте", Language.Russian, "здравствуйте");
            });
            test("Spelling: missing letters in RU UK EN", delegate
            {
                Correct(detector, "орфографя", Language.Russian, "орфография");
                Correct(detector, "перезавнтаження", Language.Ukrainian, "перезавантаження");
                Correct(detector, "dictinary", Language.English, "dictionary");
            });
            test("Spelling: extra letters in RU UK EN", delegate
            {
                Correct(detector, "ошибкка", Language.Russian, "ошибка");
                Correct(detector, "дякуую", Language.Ukrainian, "дякую");
                Correct(detector, "compputer", Language.English, "computer");
            });
            test("Spelling: adjacent transposition", delegate
            {
                Correct(detector, "перезапсук", Language.Russian, "перезапуск");
                Correct(detector, "пргорамма", Language.Russian, "программа");
                Correct(detector, "compuetr", Language.English, "computer");
            });
            test("Spelling: wrong letters in RU UK EN", delegate
            {
                Correct(detector, "орфаграфия", Language.Russian, "орфография");
                Correct(detector, "орфаграфія", Language.Ukrainian, "орфографія");
                Correct(detector, "dictionarx", Language.English, "dictionary");
            });
            test("Spelling: duplicated fragment", delegate
            { Correct(detector, "пропущенныные", Language.Russian, "пропущенные"); });
            test("Spelling: preserves case and trailing punctuation", delegate
            {
                Correct(detector, "Привт!", Language.Russian, "Привет!");
                Correct(detector, "ПРОГРАМА?!»,", Language.Russian, "ПРОГРАММА?!»,");
                Correct(detector, "Дякуую…", Language.Ukrainian, "Дякую…");
                Correct(detector, "COMPPUTER", Language.English, "COMPUTER");
                Correct(detector, "Dictinary.)", Language.English, "Dictionary.)");
            });
            test("Spelling: valid current-language words unchanged", delegate
            {
                Unchanged(detector, Language.Russian, "привет", "программа", "программами", "словами");
                Unchanged(detector, Language.Ukrainian, "програма", "привіт", "словами");
                Unchanged(detector, Language.English, "hello", "computers", "wold");
            });
            test("Spelling: other-language membership does not block Russian correction", delegate
            {
                Tests.Check(detector.Known("програма", Language.Ukrainian), "Ukrainian fixture missing");
                Correct(detector, "програма", Language.Russian, "программа");
                Unchanged(detector, Language.Ukrainian, "програма");
            });
            test("Spelling: competing dictionary words unchanged", delegate
            {
                Unchanged(detector, Language.Russian, "слонн", "праграмма", "словми", "программма");
                Unchanged(detector, Language.Ukrainian, "привт", "прогрма");
                Unchanged(detector, Language.English, "helo", "computr", "recieve", "speling");
            });
            test("Spelling: identifiers, URLs, numbers and mixed scripts unchanged", delegate
            {
                Unchanged(detector, Language.Russian, "привт1", "привт_", "привт@example.com", "https://привт", "привт.рф", "привт/мир", "привт-мир", "прИвт", "пРивт", "пpивт", "'привт", "ПривтПривт");
                Unchanged(detector, Language.English, "compputer.net", "comppuTer", "compputer42", "compputеr", "don't");
                Unchanged(detector, Language.Ukrainian, "п'ять", "п’ять", "дякуую_test");
            });
            test("Spelling: no cross-script or unsupported-language correction", delegate
            {
                Unchanged(detector, Language.Russian, "dictinary", "ghbdtn");
                Unchanged(detector, Language.English, "привт", "дякуую");
                Tests.Check(detector.SuggestSpelling("привт", (Language)99) == null, "invalid language");
            });
            test("Spelling: short, long and empty tokens unchanged", delegate
            {
                Unchanged(detector, Language.Russian, null, "", " ", "врм", "!?", new string('а', 33), new string('а', 65));
                Unchanged(detector, Language.English, "teh", new string('a', 33));
            });
            test("Spelling: ignored word applies after cached correction", delegate
            {
                Correct(detector, "привт", Language.Russian, "привет");
                try
                {
                    detector.Ignored.Add("ПрИвТ");
                    Unchanged(detector, Language.Russian, "ПРИВТ!", "Привт", "привт");
                }
                finally { detector.Ignored.Remove("ПрИвТ"); }
                Correct(detector, "Привт!", Language.Russian, "Привет!");
            });
            test("Spelling-only exclusions: parsing, cached corrections and removal in RU UK EN", delegate
            {
                Correct(detector, "привт", Language.Russian, "привет");
                Correct(detector, "дякуую", Language.Ukrainian, "дякую");
                Correct(detector, "dictinary", Language.English, "dictionary");
                var words = SpellChecker.ParseIgnoredWords("  ПрИвТ!\r\nпривт\tДЯКУУЮ…; DICTINARY, ?! ");
                Tests.Check(words.Count == 3, "duplicates or punctuation-only entries retained");
                try
                {
                    detector.SpellingIgnored.UnionWith(words);
                    Unchanged(detector, Language.Russian, "ПРИВТ!", "Привт", "привт?!»,");
                    Unchanged(detector, Language.Ukrainian, "ДЯКУУЮ…", "Дякуую");
                    Unchanged(detector, Language.English, "Dictinary.)", "DICTINARY");
                    Correct(detector, "пожалуста", Language.Russian, "пожалуйста");
                }
                finally { detector.SpellingIgnored.Clear(); }
                Correct(detector, "Привт!", Language.Russian, "Привет!");
                Correct(detector, "Дякуую…", Language.Ukrainian, "Дякую…");
                Correct(detector, "Dictinary", Language.English, "Dictionary");
            });
            test("Spelling-only exclusions preserve layout correction and custom rules", delegate
            {
                try
                {
                    detector.SpellingIgnored.UnionWith(new[] { "ghbdtn", "привт", "привет" });
                    Suggestion layout = detector.Suggest("ghbdtn", Language.English, Layouts.Convert);
                    Tests.Check(layout != null, "spelling exclusion blocked layout correction");
                    Tests.Equal("привет", layout.Text);
                    var rules = new ReplacementBook(new[] { new ReplacementRule { From = "привт", To = "Моя замена" } });
                    Suggestion custom = rules.Find("Привт!", Language.Russian, detector.Ignored);
                    Tests.Check(custom != null, "spelling exclusion blocked explicit rule");
                    Tests.Equal("Моя замена!", custom.Text);
                    detector.SpellingIgnored.Remove("привт");
                    Correct(detector, "привт", Language.Russian, "привет");
                }
                finally { detector.SpellingIgnored.Clear(); }
            });
            test("Spelling: ignored token with punctuation", delegate
            {
                try
                {
                    detector.Ignored.Add("привт!");
                    Unchanged(detector, Language.Russian, "ПРИВТ!");
                }
                finally { detector.Ignored.Remove("привт!"); }
            });
            test("Spelling: explicit typo requires a known target and preserves known input", delegate
            {
                string directory = Path.Combine(Path.GetTempPath(), "TriSwitch-spelling-" + Guid.NewGuid().ToString("N"));
                string dictionaryPath = Path.Combine(directory, "fixture.dic"), affixPath = Path.Combine(directory, "fixture.aff");
                Directory.CreateDirectory(directory);
                try
                {
                    File.WriteAllText(affixPath, "SET UTF-8\n", Encoding.UTF8);
                    File.WriteAllText(dictionaryPath, "1\nпривет\n", Encoding.UTF8);
                    var ignored = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    var checker = new SpellChecker(new WordDictionary(dictionaryPath), Language.Russian);
                    Tests.Check(checker.Suggest("привт", ignored) != null, "known target was not corrected");
                    File.WriteAllText(dictionaryPath, "2\nпривт\nпривет\n", Encoding.UTF8);
                    checker = new SpellChecker(new WordDictionary(dictionaryPath), Language.Russian);
                    Tests.Check(checker.Suggest("привт", ignored) == null, "known input overwritten by explicit typo");
                    File.WriteAllText(dictionaryPath, "0\n", Encoding.UTF8);
                    checker = new SpellChecker(new WordDictionary(dictionaryPath), Language.Russian);
                    Tests.Check(checker.Suggest("привт", ignored) == null, "unknown explicit target accepted");
                }
                finally
                {
                    if (File.Exists(dictionaryPath)) File.Delete(dictionaryPath);
                    if (File.Exists(affixPath)) File.Delete(affixPath);
                    Directory.Delete(directory);
                }
            });
        }

        private static void Correct(Detector detector, string input, Language language, string expected)
        {
            Suggestion result = detector.SuggestSpelling(input, language);
            Tests.Check(result != null, "no spelling suggestion for " + input);
            Tests.Equal(expected, result.Text);
            Tests.Check(result.Language == language && result.PreserveLayout && result.Spelling && !result.Custom, "incorrect spelling flags");
        }
        private static void Unchanged(Detector detector, Language language, params string[] words)
        {
            foreach (string word in words) Tests.Check(detector.SuggestSpelling(word, language) == null, "unexpected spelling correction for " + word);
        }
    }
}
