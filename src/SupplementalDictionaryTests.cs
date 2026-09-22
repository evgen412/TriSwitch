using System;

namespace TriSwitch
{
    internal static class SupplementalDictionaryTests
    {
        public static void Run(Action<string, Action> test, Detector detector)
        {
            test("Supplemental Russian words and inflections", delegate
            {
                foreach (string word in new[] { "из-за", "из-под", "по-русски", "что-нибудь", "какими-то",
                    "всё-таки", "мессенджерами", "скриншотов", "видеозвонками", "геолокацию",
                    "репозиториев", "коммитами", "нейросетях", "чат-ботом", "переподключиться" })
                    Check(detector.Known(word, Language.Russian), word);
            });
            test("Detect supplemental words in the wrong layout", delegate
            {
                foreach (string word in new[] { "из-за", "из-под", "по-русски", "что-нибудь", "какой-то",
                    "всё-таки", "мессенджерами", "скриншотов", "видеозвонками", "геолокацию",
                    "репозиториев", "коммитами", "нейросетях", "переподключиться" })
                {
                    string input = Layouts.Convert(word, Language.Russian, Language.English);
                    Suggestion suggestion = detector.Suggest(input, Language.English, Layouts.Convert);
                    Check(suggestion != null && suggestion.Language == Language.Russian && suggestion.Text == word,
                        input + " -> " + word);
                }
            });
            test("Preserve correctly typed supplemental words", delegate
            {
                foreach (string word in new[] { "скриншот", "нейросеть", "из-за", "кое-какими", "переподключение" })
                    Check(detector.Suggest(word, Language.Russian, Layouts.Convert) == null, word);
            });
            test("Supplemental dictionary accepts exact forms only", delegate
            {
                foreach (string word in new[] { "скриншотовами", "мессенджерамиами", "из-нейросети", "кот-мессенджер" })
                    Check(!detector.Known(word, Language.Russian), word);
            });
        }

        private static void Check(bool condition, string message)
        {
            if (!condition) throw new Exception(message);
        }
    }
}
