using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace TriSwitch
{
    internal static class PreferenceTests
    {
        private static void Reject(Action action)
        { try { action(); } catch (ArgumentException) { return; } throw new Exception("Invalid input was accepted"); }
        private static ReplacementRule Rule(string from, string to, int target = -1)
        { return new ReplacementRule { From = from, To = to, Target = target }; }
        private static string Signature(HotkeyBinding b) { return b.Key + ":" + b.Modifiers; }
        internal static void Run(Action<string, Action> test, string directory)
        {
            test("Default hotkeys validate", delegate { HotkeyBinding.Validate(HotkeyBinding.Defaults()); });
            test("Cyrillic priority defaults to Russian", delegate { Tests.Check(new Settings().CyrillicPriority == Language.Russian, "wrong default priority"); });
            test("Spelling correction is enabled by default", delegate { Tests.Check(new Settings().SpellCheck, "spelling correction disabled by default"); });
            test("Startup command quotes spaced Unicode path and requests tray", delegate { Tests.Equal("\"D:\\Мои программы\\TriSwitch.exe\" --tray", StartupRegistration.CommandFor(@"D:\Мои программы\TriSwitch.exe")); });
            test("Startup command rejects invalid executable paths", delegate { Reject(() => StartupRegistration.CommandFor("TriSwitch.exe")); Reject(() => StartupRegistration.CommandFor("D:\\app\" --other.exe")); });
            test("Duplicate hotkeys rejected", delegate { var b = HotkeyBinding.Defaults(); b[1] = b[0].Copy(); Reject(() => HotkeyBinding.Validate(b)); });
            test("Bare typing key and reserved F12 rejected", delegate
            {
                var b = HotkeyBinding.Defaults(); b[0] = new HotkeyBinding { Key = 65 }; Reject(() => HotkeyBinding.Validate(b));
                b[0] = new HotkeyBinding { Key = 123, Modifiers = 3 }; Reject(() => HotkeyBinding.Validate(b));
                b[0] = new HotkeyBinding { Key = 46, Modifiers = 3 }; Reject(() => HotkeyBinding.Validate(b));
            });
            test("Pause, F-key and disabled hotkeys allowed", delegate { var b = HotkeyBinding.Defaults(); b[0] = new HotkeyBinding { Key = 19 }; b[1] = new HotkeyBinding { Key = 120 }; b[2] = new HotkeyBinding(); HotkeyBinding.Validate(b); });
            test("Hotkey conflict leaves old bindings active", delegate
            {
                var registrations = new Dictionary<int, string>(); var defaults = HotkeyBinding.Defaults();
                using (var registry = new HotkeyRegistry((id, b) => { if (b.Key == 120 || registrations.ContainsValue(Signature(b))) return false; registrations.Add(id, Signature(b)); return true; }, id => registrations.Remove(id)))
                {
                    registry.Start(defaults); string before = string.Join("|", registrations.OrderBy(p => p.Key));
                    var changed = HotkeyBinding.Defaults(); changed[0] = new HotkeyBinding { Key = 119, Modifiers = 6 }; changed[1] = new HotkeyBinding { Key = 120, Modifiers = 6 };
                    string error; bool saved = false;
                    Tests.Check(!registry.Apply(changed, () => saved = true, out error), "conflict accepted");
                    Tests.Check(!saved, "saved conflict"); Tests.Equal(before, string.Join("|", registrations.OrderBy(p => p.Key)));
                    Tests.Check(registry.Matches(new KeyEvent { Vk = 49, Ctrl = true, Alt = true }), "old key no longer active");
                }
                Tests.Check(registrations.Count == 0, "registrations leaked");
            });
            test("Save failure rolls back registrations", delegate
            {
                var registrations = new Dictionary<int, string>();
                using (var registry = new HotkeyRegistry((id, b) => { registrations.Add(id, Signature(b)); return true; }, id => registrations.Remove(id)))
                {
                    registry.Start(HotkeyBinding.Defaults()); string before = string.Join("|", registrations.OrderBy(p => p.Key));
                    var changed = HotkeyBinding.Defaults(); changed[0] = new HotkeyBinding { Key = 119, Modifiers = 6 }; string error;
                    Tests.Check(!registry.Apply(changed, () => { throw new IOException("read only"); }, out error), "save error ignored");
                    Tests.Equal(before, string.Join("|", registrations.OrderBy(p => p.Key)));
                }
            });
            test("Hotkeys can swap actions without self-conflict", delegate
            {
                var registrations = new Dictionary<int, string>();
                using (var registry = new HotkeyRegistry((id, b) => { if (registrations.ContainsValue(Signature(b))) return false; registrations.Add(id, Signature(b)); return true; }, id => registrations.Remove(id)))
                {
                    registry.Start(HotkeyBinding.Defaults()); int id49 = registrations.Single(p => p.Value == "49:3").Key;
                    var changed = HotkeyBinding.Defaults(); var first = changed[0]; changed[0] = changed[1]; changed[1] = first; string error;
                    Tests.Check(registry.Apply(changed, () => { }, out error), error); Tests.Check(registrations.Count == 6, "extra registration");
                    Tests.Check(registry.Resolve(id49, 49, 3) == 2, "wrong action after swap");
                    Tests.Check(registry.Resolve(id49, 50, 3) == 0, "stale message accepted");
                }
            });
            test("Disabling a hotkey unregisters it", delegate
            {
                var registrations = new HashSet<int>();
                using (var registry = new HotkeyRegistry((id, b) => registrations.Add(id), id => registrations.Remove(id)))
                { registry.Start(HotkeyBinding.Defaults()); var changed = HotkeyBinding.Defaults(); changed[0] = new HotkeyBinding(); string error; Tests.Check(registry.Apply(changed, () => { }, out error), error); Tests.Check(registrations.Count == 5, "not unregistered"); Tests.Check(!registry.Matches(new KeyEvent { Vk = 49, Ctrl = true, Alt = true }), "disabled key matches"); }
            });
            test("Old settings migrate preserving user values", delegate
            {
                string path = Path.Combine(directory, "test-legacy-settings.json");
                try
                {
                    File.WriteAllText(path, "{\"Automatic\":false,\"Exclusions\":\"myapp\",\"IgnoreWords\":\"myword\"}", Encoding.UTF8);
                    Settings s = Settings.Load(path); Tests.Check(!s.Automatic, "automatic changed"); Tests.Equal("myapp", s.Exclusions); Tests.Equal("myword", s.IgnoreWords);
                    Tests.Check(s.Hotkeys.Length == 6 && s.Hotkeys[1].Key == 50 && s.Replacements.Count == 0, "missing defaults");
                    Tests.Check(s.CyrillicPriority == Language.Russian, "legacy settings did not receive Russian priority");
                    Tests.Check(s.SpellCheck, "legacy settings did not enable spelling correction");
                }
                finally { if (File.Exists(path)) File.Delete(path); }
            });
            test("Custom preferences survive save and reload", delegate
            {
                string path = Path.Combine(directory, "test-new-settings.json");
                try
                {
                    foreach (bool spellCheck in new[] { false, true })
                    {
                        var s = new Settings { Automatic = false, SpellCheck = spellCheck, CyrillicPriority = Language.Ukrainian, Exclusions = "myapp", IgnoreWords = "myword" }; s.Hotkeys[1] = new HotkeyBinding { Key = 119, Modifiers = 6 }; s.Hotkeys[2] = new HotkeyBinding(); s.Replacements.Add(Rule("адр", "Моя улица", 1)); s.Save(path);
                        Settings loaded = Settings.Load(path); Tests.Check(loaded.Hotkeys[1].Same(s.Hotkeys[1]) && loaded.Hotkeys[2].Key == 0, "lost hotkeys");
                        Tests.Check(loaded.CyrillicPriority == Language.Ukrainian && !loaded.Automatic, "lost priority or automatic setting"); Tests.Equal("myapp", loaded.Exclusions); Tests.Equal("myword", loaded.IgnoreWords);
                        Tests.Check(loaded.SpellCheck == spellCheck, "lost spelling setting");
                        Tests.Equal("Моя улица", loaded.Replacements[0].To); Tests.Check(loaded.Replacements[0].Target == 1, "lost target");
                        Settings copy = loaded.Copy(); copy.Replacements[0].To = "another"; copy.Hotkeys[1].Key = 120;
                        Tests.Check(copy.CyrillicPriority == Language.Ukrainian && !copy.Automatic, "copy lost priority or automatic setting"); Tests.Equal("myapp", copy.Exclusions); Tests.Equal("myword", copy.IgnoreWords);
                        Tests.Check(copy.SpellCheck == spellCheck, "copy lost spelling setting");
                        copy.SpellCheck = !spellCheck; Tests.Check(loaded.SpellCheck == spellCheck, "copy changed original spelling setting");
                        copy.CyrillicPriority = Language.Russian; Tests.Check(loaded.CyrillicPriority == Language.Ukrainian, "copy changed original priority");
                        Tests.Equal("Моя улица", loaded.Replacements[0].To); Tests.Check(loaded.Hotkeys[1].Key == 119, "shared mutable copy");
                    }
                }
                finally { if (File.Exists(path)) File.Delete(path); }
            });
            test("Invalid Cyrillic priority rejected on save and load", delegate
            {
                string path = Path.Combine(directory, "test-invalid-priority-settings.json");
                try
                {
                    foreach (Language invalid in new[] { Language.English, (Language)(-1), (Language)3, (Language)99 })
                    {
                        new Settings { CyrillicPriority = Language.Ukrainian }.Save(path);
                        string saved = File.ReadAllText(path, Encoding.UTF8);
                        Reject(() => new Settings { CyrillicPriority = invalid }.Save(path));
                        Tests.Equal(saved, File.ReadAllText(path, Encoding.UTF8));
                        Tests.Check(!File.Exists(path + ".tmp"), "invalid save left a temporary file");
                        File.WriteAllText(path, "{\"CyrillicPriority\":" + (int)invalid + "}", Encoding.UTF8);
                        Reject(() => Settings.Load(path));
                    }
                }
                finally { if (File.Exists(path)) File.Delete(path); }
            });
            var ignored = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            test("Custom rule preserves lower, title and upper case", delegate
            {
                var r = Rule("Добрій", "Добрый", 1); r.PreserveCase = true;
                var book = new ReplacementBook(new[] { r });
                foreach (var pair in new[] { new[] { "добрій", "добрый" }, new[] { "Добрій", "Добрый" }, new[] { "ДОБРІЙ!", "ДОБРЫЙ!" }, new[] { "ДоБрІй", "Добрый" } })
                {
                    var result = book.Find(pair[0], Language.Ukrainian, ignored);
                    Tests.Equal(pair[1], result.Text); Tests.Check(result.Language == Language.Russian, "lost target");
                }
            });
            test("Case option survives copy and persistence; old rules stay literal", delegate
            {
                string path = Path.Combine(directory, "test-case-settings.json");
                try
                {
                    File.WriteAllText(path, "{\"Replacements\":[{\"Enabled\":true,\"From\":\"brb\",\"To\":\"Be Right Back\",\"Target\":-1}]}");
                    var s = Settings.Load(path); Tests.Check(!s.Replacements[0].PreserveCase, "legacy behavior changed");
                    s.Replacements[0].PreserveCase = true; s.Copy().Save(path);
                    var rule = Settings.Load(path).Replacements[0]; Tests.Check(rule.PreserveCase, "option lost");
                    var book = new ReplacementBook(new[] { rule });
                    Tests.Equal("be right back", book.Find("brb", Language.English, ignored).Text);
                    Tests.Equal("Be right back", book.Find("Brb", Language.English, ignored).Text);
                    Tests.Equal("BE RIGHT BACK", book.Find("BRB", Language.English, ignored).Text);
                }
                finally { if (File.Exists(path)) File.Delete(path); }
            });
            test("Short custom trigger expands a phrase", delegate { var book = new ReplacementBook(new[] { Rule("brb", "Скоро вернусь") }); Suggestion s = book.Find("brb", Language.English, ignored); Tests.Equal("Скоро вернусь", s.Text); Tests.Check(s.PreserveLayout && s.Language == Language.English && s.Custom, "layout was changed"); });
            test("Replacement is case insensitive and literal", delegate { var book = new ReplacementBook(new[] { Rule("адр", "Улица Мира", 1) }); Suggestion s = book.Find("АДР", Language.Ukrainian, ignored); Tests.Equal("Улица Мира", s.Text); Tests.Check(s.Language == Language.Russian && !s.PreserveLayout, "wrong layout"); });
            test("Punctuation preserved with exact rule priority", delegate
            {
                var book = new ReplacementBook(new[] { Rule("brb", "Скоро вернусь"), Rule("brb!", "Очень скоро") });
                Tests.Equal("Скоро вернусь,", book.Find("brb,", Language.English, ignored).Text); Tests.Equal("Очень скоро", book.Find("brb!", Language.English, ignored).Text);
            });
            test("Only whole custom triggers match", delegate { var book = new ReplacementBook(new[] { Rule("brb", "Скоро вернусь") }); Tests.Check(book.Find("xbrb", Language.English, ignored) == null, "substring replaced"); });
            test("Ignored words override custom rules", delegate { var book = new ReplacementBook(new[] { Rule("brb", "Скоро вернусь") }); var skip = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "brb" }; Tests.Check(book.Find("BRB!", Language.English, skip) == null, "ignore bypassed"); });
            test("Disabled custom rule does not apply", delegate { var r = Rule("brb", "Скоро вернусь"); r.Enabled = false; var book = new ReplacementBook(new[] { r }); Tests.Check(book.Find("brb", Language.English, ignored) == null, "disabled rule applied"); });
            test("Duplicate enabled rules rejected", delegate { Reject(() => new ReplacementBook(new[] { Rule("brb", "first"), Rule("BRB", "second") })); });
            test("Invalid custom rules rejected", delegate
            {
                foreach (ReplacementRule r in new[] { Rule("a b", "phrase"), Rule("a", ""), Rule("a", "line\nline"), Rule("a", "text", 9), Rule("a", "😀"), Rule("a", "a"), Rule(new string('a', 65), "b"), Rule("a", new string('b', 513)) }) Reject(() => new ReplacementBook(new[] { r }));
            });
            test("Editing a draft does not alter active rules", delegate { var rule = Rule("brb", "original"); var book = new ReplacementBook(new[] { rule }); rule.To = "draft"; Tests.Equal("original", book.Find("brb", Language.English, ignored).Text); });
            test("Custom rules can explicitly replace dictionary words", delegate { var book = new ReplacementBook(new[] { Rule("hello", "Здравствуйте", 1) }); Tests.Equal("Здравствуйте", book.Find("hello", Language.English, ignored).Text); });
        }
    }
}
