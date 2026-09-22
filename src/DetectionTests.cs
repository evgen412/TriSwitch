using System;

namespace TriSwitch
{
    internal static class DetectionTests
    {
        private static void Suggest(Detector detector, string input, Language source, string expected, Language target, Language preferredCyrillic = Language.Russian)
        {
            Suggestion result = detector.Suggest(input, source, Layouts.Convert, preferredCyrillic);
            Tests.Check(result != null, "no suggestion for " + input);
            Tests.Equal(expected, result.Text);
            Tests.Check(result.Language == target, "wrong target for " + input);
        }

        private static void Unchanged(Detector detector, Language source, params string[] words)
        {
            foreach (string word in words)
                Tests.Check(detector.Suggest(word, source, Layouts.Convert) == null, "unexpected suggestion for " + word);
        }

        internal static void Run(Action<string, Action> test, Detector detector)
        {
            test("Short Russian words and prepositions", delegate
            {
                Suggest(detector, "gjl", Language.English, "под", Language.Russian);
                Suggest(detector, "j,j", Language.English, "обо", Language.Russian);
                Suggest(detector, "xnj", Language.English, "что", Language.Russian);
                Suggest(detector, "ytn", Language.English, "нет", Language.Russian);
                Suggest(detector, "ljv", Language.English, "дом", Language.Russian);
                Suggest(detector, "cj", Language.English, "со", Language.Russian);
            });
            test("Short Ukrainian words and prepositions", delegate
            {
                Suggest(detector, "gsl", Language.English, "під", Language.Ukrainian);
                Suggest(detector, "dsl", Language.English, "від", Language.Ukrainian);
                Suggest(detector, "dsy", Language.English, "він", Language.Ukrainian);
                Suggest(detector, "ys", Language.English, "ні", Language.Ukrainian);
            });
            test("Short English words and prepositions", delegate
            {
                Suggest(detector, "фтв", Language.Russian, "and", Language.English);
                Suggest(detector, "ащк", Language.Russian, "for", Language.English);
                Suggest(detector, "ещ", Language.Russian, "to", Language.English);
                Suggest(detector, "ща", Language.Russian, "of", Language.English);
                Suggest(detector, "фт", Language.Ukrainian, "an", Language.English);
            });
            test("Short corrections preserve case and punctuation", delegate
            {
                Suggest(detector, "GJL!", Language.English, "ПОД!", Language.Russian);
                Suggest(detector, "gjl,", Language.English, "под,", Language.Russian);
                Suggest(detector, "Cj!", Language.English, "Со!", Language.Russian);
                Suggest(detector, "Ys?", Language.English, "Ні?", Language.Ukrainian);
                Suggest(detector, "ЕЩ!", Language.Russian, "TO!", Language.English);
            });
            test("Shared Russian and Ukrainian words correct to the same text", delegate
            {
                Suggest(detector, "yf", Language.English, "на", Language.Russian);
                Suggest(detector, "gjkt", Language.English, "поле", Language.Russian);
                Suggest(detector, "gthtpfgecnb", Language.English, "перезапусти", Language.Russian);
                Suggest(detector, "rjn", Language.English, "кот", Language.Russian);
                Suggest(detector, "vbh", Language.English, "мир", Language.Russian);
            });
            test("Shared corrections preserve case and punctuation", delegate
            {
                Suggest(detector, "YF!", Language.English, "НА!", Language.Russian);
                Suggest(detector, "Gjkt,", Language.English, "Поле,", Language.Russian);
                Suggest(detector, "Gthtpfgecnb?", Language.English, "Перезапусти?", Language.Russian);
            });
            test("Shared corrections respect the selected Ukrainian priority", delegate
            {
                Suggest(detector, "yf", Language.English, "на", Language.Ukrainian, Language.Ukrainian);
                Suggest(detector, "gjkt", Language.English, "поле", Language.Ukrainian, Language.Ukrainian);
                Suggest(detector, "gthtpfgecnb", Language.English, "перезапусти", Language.Ukrainian, Language.Ukrainian);
                Suggest(detector, "Vfvf!", Language.English, "Мама!", Language.Ukrainian, Language.Ukrainian);
            });
            test("Missing preferred candidate falls back to Russian", delegate
            {
                Suggest(detector, "yf", Language.English, "на", Language.Russian, Language.English);
            });
            test("Different Russian and Ukrainian text prefers Russian", delegate
            {
                // The same physical keys spell Russian сыр and Ukrainian сір.
                Tests.Check(detector.Known("сыр", Language.Russian), "сыр missing from dictionary");
                Tests.Check(detector.Known("сір", Language.Ukrainian), "сір missing from dictionary");
                Suggest(detector, "csh", Language.English, "сыр", Language.Russian);
                Suggest(detector, "Csh!", Language.English, "Сыр!", Language.Russian);
                Suggest(detector, "csh", Language.English, "сыр", Language.Russian, Language.English);
                Suggest(detector, "csh", Language.English, "сір", Language.Ukrainian, Language.Ukrainian);
            });
            test("Russian priority also resolves a different Ukrainian punctuation reading", delegate
            {
                // The comma may be punctuation after Russian со, or the last
                // physical key of Ukrainian соб. Russian wins by preference.
                Suggest(detector, "cj,", Language.English, "со,", Language.Russian);
                Suggest(detector, "cj,", Language.English, "соб", Language.Ukrainian, Language.Ukrainian);
            });
            test("Multiple punctuation readings within Russian remain unchanged", delegate
            {
                // A period may end обеда. or represent the last key in обедаю.
                Tests.Check(detector.Known("обеда", Language.Russian), "обеда missing from dictionary");
                Tests.Check(detector.Known("обедаю", Language.Russian), "обедаю missing from dictionary");
                foreach (Language preferred in Enum.GetValues(typeof(Language)))
                    Tests.Check(detector.Suggest("j,tlf.", Language.English, Layouts.Convert, preferred) == null, "preference resolved multiple Russian readings");
            });
            test("Known short words survive a mismatched active layout", delegate
            {
                Unchanged(detector, Language.English, "дом", "під", "ні", "со");
                Unchanged(detector, Language.Russian, "cat", "and", "to", "of");
                Unchanged(detector, Language.Ukrainian, "дом", "мы", "an");
                // These source words are dictionary entries even though their
                // physical keys can also spell words in another language.
                Unchanged(detector, Language.English, "vs");
                Unchanged(detector, Language.Russian, "шт");
            });
            test("Rare two-letter dictionary words are not auto-corrected", delegate
            {
                Tests.Check(detector.Known("ox", Language.English), "ox missing from dictionary");
                Tests.Check(detector.Known("эф", Language.Russian), "эф missing from dictionary");
                Unchanged(detector, Language.Russian, "щч");
                Unchanged(detector, Language.English, "'a");
            });
            test("Single characters and empty tokens unchanged", delegate
            {
                Unchanged(detector, Language.English, "", "d", "f", "z", "s", "d!", ".", "!");
                Unchanged(detector, Language.Russian, "ф", "ш", "я");
            });
            test("Short ignored words preserve case and punctuation", delegate
            {
                bool added = detector.Ignored.Add("gjl");
                try { Unchanged(detector, Language.English, "gjl", "GJL!"); }
                finally { if (added) detector.Ignored.Remove("gjl"); }
            });
            test("Short tokens containing numbers addresses or identifiers unchanged", delegate
            {
                Unchanged(detector, Language.English, "gjl1", "1gsl", "user@gjl.com", "https://gjl", "gjl_name");
            });
            test("Correction respects the full token length limit", delegate
            {
                string punctuation = new string('!', 61);
                Suggest(detector, "gjl" + punctuation, Language.English, "под" + punctuation, Language.Russian);
                Unchanged(detector, Language.English, "gjl" + punctuation + "!");
            });
        }
    }
}
