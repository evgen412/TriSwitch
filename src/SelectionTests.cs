using System;
using System.IO;
using System.Linq;
using System.Text;

namespace TriSwitch
{
    internal static class SelectionTests
    {
        internal static void Run(Action<string, Action> test, string directory)
        {
            test("Selection cycle follows UK RU EN and skips missing layouts", delegate
            {
                Language current = Language.Ukrainian;
                foreach (Language expected in new[] { Language.Russian, Language.English, Language.Ukrainian })
                { current = SelectionCycle.Next(current, l => true); Tests.Check(current == expected, "wrong cycle order"); }
                Tests.Check(SelectionCycle.Next(Language.English, l => l != Language.Ukrainian) == Language.Russian, "missing UK not skipped");
                Tests.Check(SelectionCycle.Next(Language.Ukrainian, l => l == Language.Ukrainian) == Language.Ukrainian, "single layout changed");
                Tests.Check(SelectionCycle.Next(Language.Russian, l => false) == Language.Russian, "no-layout behavior");
            });
            test("Selection conversion always uses the original text", delegate
            {
                var cycle = new SelectionCycle("ІЇЄґ ПрИвІт", Language.Ukrainian);
                Tests.Equal("ЫЪЭґ ПрИвЫт", cycle.TextFor(Language.Russian, Layouts.Convert));
                Tests.Equal("S}\"ґ GhBdSn", cycle.TextFor(Language.English, Layouts.Convert));
                // Even if an intermediate conversion was lossy, returning to the source is exact.
                cycle.Current = Language.English;
                Tests.Equal(cycle.Original, cycle.TextFor(Language.Ukrainian, (text, from, to) => "lossy"));
            });
            test("Selection source follows the text before the active layout", delegate
            {
                Tests.Check(SelectionCycle.DetectSource("Hello", Language.Russian, Language.Russian) == Language.English, "Latin source");
                Tests.Check(SelectionCycle.DetectSource("СЫР", Language.Ukrainian, Language.Ukrainian) == Language.Russian, "Russian source");
                Tests.Check(SelectionCycle.DetectSource("Їжак", Language.English, Language.Russian) == Language.Ukrainian, "Ukrainian source");
                Tests.Check(SelectionCycle.DetectSource("мама", Language.Ukrainian, Language.Russian) == Language.Ukrainian, "ambiguous active layout");
                Tests.Check(SelectionCycle.DetectSource("мама", Language.English, Language.Ukrainian) == Language.Ukrainian, "ambiguous preference");
                Tests.Check(!SelectionCycle.DetectSource("wordслово", Language.English, Language.Russian).HasValue, "mixed alphabet");
                Tests.Check(!SelectionCycle.DetectSource("ыі", Language.Russian, Language.Russian).HasValue, "mixed Cyrillic variants");
            });
            test("Selection rejects unsupported ranges without truncation", delegate
            {
                foreach (string text in new[] { "", "one\ntwo", "one\ttwo", "word🙂", "и\u0306", "a\u200Db", "a\u2028b", "עברית", new string('a', 513) })
                    Tests.Check(!SelectionCycle.Supported(text), "accepted unsupported selection: " + text.Length);
                Tests.Check(SelectionCycle.Supported(new string('a', 512)), "length boundary");
                Tests.Check(SelectionCycle.Supported("ІЇЄґ ПрИвІт!?"), "ordinary selection rejected");
                Tests.Check(SelectionCycle.Supported("]"), "punctuation-only intermediate variant rejected");
            });
            test("Six-action settings migrate without losing custom shortcuts", delegate
            {
                string path = Path.Combine(directory, "test-selection-migration.json");
                try
                {
                    foreach (bool conflict in new[] { false, true })
                    {
                        HotkeyBinding[] old = HotkeyBinding.Defaults().Take(6).ToArray();
                        old[0] = new HotkeyBinding { Key = conflict ? 0x76u : 0x77u, Modifiers = 3 };
                        old[2] = new HotkeyBinding();
                        string json = "{\"Automatic\":false,\"Exclusions\":\"myapp\",\"Hotkeys\":["
                            + string.Join(",", old.Select(b => "{\"Key\":" + b.Key + ",\"Modifiers\":" + b.Modifiers + "}")) + "]}";
                        File.WriteAllText(path, json, Encoding.UTF8);
                        Settings loaded = Settings.Load(path);
                        for (int i = 0; i < old.Length; i++) Tests.Check(loaded.Hotkeys[i].Same(old[i]), "old shortcut changed");
                        Tests.Check(!loaded.Automatic && loaded.Exclusions == "myapp", "settings lost");
                        Tests.Check(loaded.Hotkeys[6].Key == (conflict ? 0u : 0x76u), "new shortcut conflict");
                        loaded.Save(path); Tests.Check(Settings.Load(path).Hotkeys[6].Same(loaded.Hotkeys[6]), "migration not persisted");
                    }
                }
                finally { if (File.Exists(path)) File.Delete(path); }
            });
        }
    }
}
