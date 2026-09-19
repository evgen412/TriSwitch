using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace TriSwitch
{
    internal static class Tests
    {
        public static int Run(string directory)
        {
            var log = new List<string>(); int failures = 0;
            Action<string, Action> test = delegate(string name, Action action)
            { try { action(); log.Add("PASS " + name); } catch (Exception e) { failures++; log.Add("FAIL " + name + ": " + e.Message); } };
            var clock = Stopwatch.StartNew();
            try
            {
                var detector = new Detector(Path.Combine(directory, "dictionaries"));
                test("Dictionary size", delegate { Check(detector.Count > 400000, detector.Count.ToString()); });
                test("EN -> RU", delegate { Equal("привет", Layouts.Convert("ghbdtn", Language.English, Language.Russian)); });
                test("EN -> UK", delegate { Equal("привіт", Layouts.Convert("ghbdsn", Language.English, Language.Ukrainian)); });
                test("RU -> EN", delegate { Equal("hello", Layouts.Convert("руддщ", Language.Russian, Language.English)); });
                test("Case preservation", delegate { Equal("ПрИвЕт", Layouts.Convert("GhBdTn", Language.English, Language.Russian)); });
                test("Ukrainian special letters", delegate { Equal("ієї", Layouts.Convert("s']", Language.English, Language.Ukrainian)); });
                test("RU -> UK physical keys", delegate { Equal("ієї", Layouts.Convert("ыэъ", Language.Russian, Language.Ukrainian)); });
                test("Round trip alphabet", delegate
                {
                    foreach (Language l in new[] { Language.Russian, Language.Ukrainian })
                    { const string word = "qwertyuiopasdfghjklzxcvbnmQWERTYUIOPASDFGHJKLZXCVBNM"; Equal(word, Layouts.Convert(Layouts.Convert(word, Language.English, l), l, Language.English)); }
                });
                test("Russian word forms", delegate { Check(detector.Known("программами", Language.Russian), "программами"); });
                test("Ukrainian word forms", delegate { Check(detector.Known("словами", Language.Ukrainian), "словами"); });
                test("English word forms", delegate { Check(detector.Known("computers", Language.English), "computers"); });
                test("Detect Russian", delegate { Suggest(detector, "ghbdtn", Language.English, "привет", Language.Russian); });
                test("Detect Ukrainian", delegate { Suggest(detector, "ghbdsn", Language.English, "привіт", Language.Ukrainian); });
                test("Detect English", delegate { Suggest(detector, "руддщ", Language.Russian, "hello", Language.English); });
                test("Leading punctuation key", delegate { Suggest(detector, ";bpym", Language.English, "жизнь", Language.Russian); });
                test("Preserve punctuation", delegate { Suggest(detector, "ghbdtn!", Language.English, "привет!", Language.Russian); });
                test("Preserve comma", delegate { Suggest(detector, "ghbdtn,", Language.English, "привет,", Language.Russian); });
                test("Ambiguous RU/UK unchanged", delegate { Check(detector.Suggest("vfvf", Language.English, Layouts.Convert) == null, "мама is ambiguous"); });
                test("Valid English unchanged", delegate { Check(detector.Suggest("hello", Language.English, Layouts.Convert) == null, "hello"); });
                test("Valid Ukrainian unchanged", delegate { Check(detector.Suggest("привіт", Language.Ukrainian, Layouts.Convert) == null, "привіт"); });
                test("Short tokens unchanged", delegate { Check(detector.Suggest("rjn", Language.English, Layouts.Convert) == null, "rjn"); });
                test("Numbers and URLs unchanged", delegate
                { foreach (string s in new[] { "ghbdtn1", "user@example.com", "https://ghbdtn", "my_name" }) Check(detector.Suggest(s, Language.English, Layouts.Convert) == null, s); });
                test("Ignored word", delegate { detector.Ignored.Add("ghbdtn"); Check(detector.Suggest("GHBDTN!", Language.English, Layouts.Convert) == null, "ignore"); detector.Ignored.Clear(); });
                test("Buffer preserves trailing space", delegate { var b = new WordBuffer(); b.Append('a', Language.English); b.Space(); Equal("a", b.Word); Equal(" ", b.Suffix); b.Backspace(); Equal("", b.Suffix); b.Backspace(); Check(!b.Valid, "empty buffer"); });
                test("Second space invalidates", delegate { var b = new WordBuffer(); b.Append('a', Language.English); b.Space(); b.Space(); Check(!b.Valid, "stale word"); });
                test("New word after space", delegate { var b = new WordBuffer(); b.Append('a', Language.English); b.Space(); b.Append('b', Language.English); Equal("b", b.Word); Equal("", b.Suffix); });
                test("Long token cannot become a partial word", delegate { var b = new WordBuffer(); for (int i = 0; i < 100; i++) b.Append('a', Language.English); Check(!b.Valid, "overflow"); b.Space(); b.Append('b', Language.English); Equal("b", b.Word); });
                test("Layout changes clear partial word", delegate { var b = new WordBuffer(); b.Append('a', Language.English); b.Append('ф', Language.Russian); Equal("ф", b.Word); });
                test("Native INPUT ABI", delegate { Check(Marshal.SizeOf(typeof(Native.Input)) == (IntPtr.Size == 8 ? 40 : 28), "wrong INPUT alignment"); });
                test("Settings round trip", delegate
                {
                    string path = Path.Combine(directory, "test-settings.json");
                    try { var s = new Settings { Automatic = false, Exclusions = "Code\r\nexample", IgnoreWords = "слово" }; s.Save(path); var loaded = Settings.Load(path); Check(!loaded.Automatic, "automatic"); Equal(s.Exclusions, loaded.Exclusions); Equal(s.IgnoreWords, loaded.IgnoreWords); s.Automatic = true; s.Save(path); Check(Settings.Load(path).Automatic, "atomic update"); }
                    finally { if (File.Exists(path)) File.Delete(path); }
                });
                var catalog = new LayoutCatalog(); log.Add("Installed layouts: " + catalog.Status);
                test("Transient Windows input profiles", delegate
                {
                    var layouts = new IntPtr[Native.GetKeyboardLayoutList(0, null)];
                    Native.GetKeyboardLayoutList(layouts.Length, layouts);
                    foreach (IntPtr h in layouts)
                    {
                        int id = (int)(h.ToInt64() & 0xffff);
                        if (id < 0x2000 || id > 0x4c00 || (id & 0x3ff) != 0) continue;
                        var s = new StringBuilder(8);
                        Native.ToUnicodeEx(0x53, Native.MapVirtualKeyEx(0x53, 0, h), new byte[256], s, 8, 4, h);
                        if (s.ToString() == "ы") Check(Native.InputLanguage(h) == Language.Russian, "Transient RU profile");
                        if (s.ToString() == "і") Check(Native.InputLanguage(h) == Language.Ukrainian, "Transient UK profile");
                        log.Add("Transient profile " + h.ToString("X") + ": " + Native.InputLanguage(h));
                    }
                    Check(!Native.InputLanguage(IntPtr.Zero).HasValue, "Unknown layout must stay unsupported");
                });
                test("Native layout conversion", delegate
                { if (catalog.Available(Language.English) && catalog.Available(Language.Ukrainian)) Equal("привіт", catalog.Convert("ghbdsn", Language.English, Language.Ukrainian)); else throw new Exception("EN or UK layout missing"); });
                PreferenceTests.Run(test, directory);
            }
            catch (Exception e) { failures++; log.Add("FAIL initialization: " + e); }
            log.Add("Results: " + (log.Count(s => s.StartsWith("PASS"))) + " passed; " + failures + " failed; " + clock.ElapsedMilliseconds + " ms");
            File.WriteAllLines(Path.Combine(directory, "self-test-results.txt"), log, Encoding.UTF8);
            return failures == 0 ? 0 : 1;
        }
        private static void Suggest(Detector detector, string input, Language source, string expected, Language target)
        { Suggestion result = detector.Suggest(input, source, Layouts.Convert); Check(result != null, "no suggestion for " + input); Equal(expected, result.Text); Check(result.Language == target, "wrong target"); }
        internal static void Check(bool value, string message) { if (!value) throw new Exception(message); }
        internal static void Equal(string expected, string actual) { Check(expected == actual, "expected [" + expected + "]; actual [" + actual + "]"); }
    }

    internal static class IntegrationTests
    {
        private sealed class Step { public string Name; public Action Action; public int Delay; }
        public static int Startup(string directory)
        {
            var form = new MainWindow(true, true); var timer = new Timer { Interval = 500 };
            var log = new List<string>(); int step = 0, failures = 0, visibleEvents = 0;
            form.VisibleChanged += delegate { if (form.Visible) visibleEvents++; };
            timer.Tick += delegate
            {
                timer.Stop();
                try
                {
                    if (step == 0)
                    {
                        Tests.Check(!form.Visible && !form.ShowInTaskbar && visibleEvents == 0, "Startup displayed the main window");
                        Tests.Check(form.TrayVisible && form.IsWatching, "Tray or input watcher not active");
                        log.Add("PASS starts in tray without showing a window; hooks active");
                        form.OpenForTest(); step++; timer.Start();
                    }
                    else
                    {
                        Tests.Check(form.Visible && form.ShowInTaskbar && visibleEvents == 1, "Cannot open tray window");
                        log.Add("PASS opens settings from tray");
                        form.Close(); Tests.Check(!form.Visible && form.TrayVisible && form.IsWatching, "Closing settings stopped the app");
                        log.Add("PASS closing settings returns to tray with hooks active");
                        form.FinishTest();
                    }
                }
                catch (Exception error) { failures++; log.Add("FAIL " + error.Message); form.FinishTest(); }
            };
            timer.Start(); Application.Run(form); timer.Dispose();
            log.Add("Startup results: " + failures + " failed");
            File.WriteAllLines(Path.Combine(directory, "startup-test-results.txt"), log, Encoding.UTF8); return failures == 0 ? 0 : 1;
        }
        public static int Preview(string directory)
        {
            var form = new MainWindow(false, true, true); var timer = new Timer { Interval = 300 }; int tab = 0;
            timer.Tick += delegate
            {
                timer.Stop();
                var tabs = form.Controls[0].Controls.OfType<TabControl>().Single(); tabs.SelectedIndex = tab;
                form.Refresh();
                using (var picture = new System.Drawing.Bitmap(form.Width, form.Height))
                { form.DrawToBitmap(picture, new System.Drawing.Rectangle(0, 0, form.Width, form.Height)); picture.Save(Path.Combine(directory, "preview-" + tab + ".png")); }
                if (++tab == tabs.TabCount) form.FinishTest(); else timer.Start();
            };
            form.Shown += delegate { timer.Start(); }; Application.Run(form); timer.Dispose(); return 0;
        }
        public static int Run(string directory)
        {
            var log = new List<string>(); var steps = new Queue<Step>(); int failures = 0;
            var form = new MainWindow(false, true);
            Control inputTarget = form.TestEditor;
            var timer = new Timer { Interval = 500 };
            IntPtr original = Native.GetKeyboardLayout(0);
            var installed = new IntPtr[Native.GetKeyboardLayoutList(0, null)]; Native.GetKeyboardLayoutList(installed.Length, installed);
            Func<Language, IntPtr> layout = l => installed.OrderBy(h => Layouts.FromHandle(h).HasValue ? 1 : 0).FirstOrDefault(h => Native.InputLanguage(h) == l);
            Action<Language> activate = l => { Tests.Check(layout(l) != IntPtr.Zero, "Missing " + l); Native.ActivateKeyboardLayout(layout(l), 0); };
            Action focus = delegate { Tests.Check(Native.GetForegroundWindow() == form.Handle && inputTarget.Focused, "Test field lost focus; no input sent"); };
            Action<string, Action, int> add = (name, action, delay) => steps.Enqueue(new Step { Name = name, Action = action, Delay = delay });
            Action<ushort, bool> key = delegate(ushort vk, bool up)
            {
                focus(); Native.Input input = Native.Key(vk, up); input.Data.Keyboard.Extra = new UIntPtr(Native.TestTag);
                Tests.Check(Native.SendInput(1, new[] { input }, Marshal.SizeOf(typeof(Native.Input))) == 1, "SendInput failed");
            };
            Action<string> type = delegate(string value)
            {
                foreach (char character in value)
                {
                    ushort vk = character == ' ' ? (ushort)32 : (ushort)char.ToUpperInvariant(character);
                    add("key " + character, delegate { key(vk, false); key(vk, true); }, 80);
                }
            };
            Action<ushort> hotkey = delegate(ushort vk)
            {
                add("modifiers down", delegate { key(17, false); key(18, false); }, 70);
                add("hotkey", delegate { key(vk, false); key(vk, true); }, 70);
                add("modifiers up", delegate { key(18, true); key(17, true); }, 350);
            };
            Action<ushort, uint> combination = delegate(ushort vk, uint modifiers)
            {
                add("custom modifiers down", delegate { if ((modifiers & 2) != 0) key(17, false); if ((modifiers & 1) != 0) key(18, false); if ((modifiers & 4) != 0) key(16, false); }, 70);
                add("custom hotkey", delegate { key(vk, false); key(vk, true); }, 70);
                add("custom modifiers up", delegate { if ((modifiers & 4) != 0) key(16, true); if ((modifiers & 1) != 0) key(18, true); if ((modifiers & 2) != 0) key(17, true); }, 300);
            };
            Action reset = delegate { focus(); form.TestEditor.Clear(); key(27, false); key(27, true); activate(Language.English); };
            add("Activate test window", delegate { form.Show(); form.WindowState = FormWindowState.Normal; form.Activate(); Native.SetForegroundWindow(form.Handle); form.TestEditor.Focus(); }, 500);
            add("Start hooks", delegate
            {
                using (var picture = new System.Drawing.Bitmap(form.Width, form.Height))
                { form.DrawToBitmap(picture, new System.Drawing.Rectangle(0, 0, form.Width, form.Height)); picture.Save(Path.Combine(directory, "window-preview.png")); }
                Tests.Check(form.IsWatching, "watcher unavailable"); form.TestEditor.Focus(); focus(); activate(Language.English);
                string identity; bool safe = new FocusGuard().TryCheck(Native.Focus(), out identity);
                var element = System.Windows.Automation.AutomationElement.FocusedElement;
                log.Add("Guard=" + safe + "; identity=" + identity + "; control=" + element.Current.ControlType.ProgrammaticName + "; password=" + element.Current.IsPassword + "; focus=" + element.Current.HasKeyboardFocus);
            }, 400);
            type("ghbdtn "); add("AUTO RU", delegate { log.Add(form.TestState); Tests.Equal("привет ", form.TestEditor.Text); Tests.Check(Native.InputLanguage(Native.GetKeyboardLayout(0)) == Language.Russian, "RU layout switch"); }, 200);
            hotkey(8); add("UNDO preserves space", delegate { Tests.Equal("ghbdtn ", form.TestEditor.Text); }, 200);
            add("Use Russian input profile (prefer transient)", delegate { reset(); activate(Language.Russian); }, 150);
            type("hello "); add("RU profile auto correction", delegate { Tests.Equal("hello ", form.TestEditor.Text); Tests.Check(Native.InputLanguage(Native.GetKeyboardLayout(0)) == Language.English, "EN layout switch"); }, 200);
            add("Reset", reset, 150); type("ghbdsn "); add("AUTO UK", delegate { Tests.Equal("привіт ", form.TestEditor.Text); Tests.Check(Native.InputLanguage(Native.GetKeyboardLayout(0)) == Language.Ukrainian, "UK layout switch"); }, 200);
            add("Reset", reset, 150); type("vfvf "); add("AMBIGUOUS unchanged", delegate { Tests.Equal("vfvf ", form.TestEditor.Text); }, 200);
            hotkey(0x33); add("MANUAL UK", delegate { Tests.Equal("мама ", form.TestEditor.Text); }, 200);
            hotkey(0x31); add("MANUAL EN", delegate { Tests.Equal("vfvf ", form.TestEditor.Text); }, 200);
            add("Reset", reset, 150); type("ghbdtn"); hotkey(0x32); type(" "); hotkey(8);
            add("UNDO after manual correction then space", delegate { Tests.Equal("ghbdtn ", form.TestEditor.Text); }, 200);
            add("Reset", reset, 150); hotkey(0x20); type("ghbdsn "); add("PAUSE suppresses auto", delegate { Tests.Equal("ghbdsn ", form.TestEditor.Text); }, 200); hotkey(0x20);
            add("Password field", delegate { reset(); form.TestEditor.Multiline = false; form.TestEditor.UseSystemPasswordChar = true; form.TestEditor.Focus(); }, 200);
            type("ghbdsn "); hotkey(0x33); add("PASSWORD untouched", delegate { Tests.Equal("ghbdsn ", form.TestEditor.Text); form.TestEditor.UseSystemPasswordChar = false; form.TestEditor.Multiline = true; form.TestEditor.Focus(); }, 200);
            add("Reset", reset, 150); type("ghbdtn"); add("Navigation invalidates buffer", delegate { key(0x24, false); key(0x24, true); }, 200); hotkey(0x32);
            add("NO stale replacement after navigation", delegate { Tests.Equal("ghbdtn", form.TestEditor.Text); }, 200);
            add("Reset", reset, 150); type("ghbdsn");
            add("Application changes text", delegate { form.TestEditor.AppendText("!"); }, 200); hotkey(0x33);
            add("NO replacement when text changed externally", delegate { Tests.Equal("ghbdsn!", form.TestEditor.Text); }, 200);
            add("Exclude test process", delegate { reset(); form.SetTestExclusions(Process.GetCurrentProcess().ProcessName); }, 200); type("ghbdsn "); hotkey(0x33);
            add("EXCLUDED process untouched", delegate { Tests.Equal("ghbdsn ", form.TestEditor.Text); form.SetTestExclusions(""); }, 200);
            add("REMAP and persist hotkeys", delegate
            {
                reset(); var bindings = HotkeyBinding.Defaults(); bindings[1] = new HotkeyBinding { Key = 119, Modifiers = 6 }; bindings[4] = new HotkeyBinding { Key = 120, Modifiers = 6 }; bindings[5] = new HotkeyBinding { Key = 121 };
                string error; Tests.Check(form.ApplyHotkeys(bindings, out error), error);
                Tests.Check(Settings.Load(Path.Combine(directory, "test-preferences.json")).Hotkeys[1].Same(bindings[1]), "remap not persisted");
            }, 200);
            type("vfvf"); hotkey(0x32); add("OLD hotkey no longer acts", delegate { Tests.Equal("vfvf", form.TestEditor.Text); }, 200);
            // An unrelated Ctrl/Alt shortcut invalidates the tracked word: retype.
            add("Reset", reset, 150); type("vfvf"); combination(119, 6); type(" ");
            add("REMAPPED hotkey changes word", delegate { Tests.Equal("мама ", form.TestEditor.Text); }, 200);
            combination(120, 6); add("REMAPPED undo preserves text", delegate { Tests.Equal("vfvf ", form.TestEditor.Text); }, 200);
            add("Reset", reset, 150); combination(121, 0); type("ghbdsn "); add("BARE F-key pause works", delegate { Tests.Equal("ghbdsn ", form.TestEditor.Text); }, 200); combination(121, 0);
            add("Focus shortcut capture", delegate { reset(); inputTarget = form.FocusTestHotkey(0); }, 200); combination(119, 6);
            add("CAPTURE registered key without executing it", delegate
            {
                Tests.Check(((HotkeyBox)inputTarget).Binding.Same(new HotkeyBinding { Key = 119, Modifiers = 6 }), "registered combination not captured");
                Tests.Equal("", form.TestEditor.Text);
            }, 200);
            combination(118, 6); add("CAPTURE new combination", delegate { Tests.Check(((HotkeyBox)inputTarget).Binding.Same(new HotkeyBinding { Key = 118, Modifiers = 6 }), "new combination not captured"); }, 200);
            add("Return to test field", delegate { form.FocusTestEditor(); inputTarget = form.TestEditor; reset(); }, 200);
            add("SAVE custom replacements", delegate
            {
                string error; Tests.Check(form.ApplyReplacements(new List<ReplacementRule> {
                    new ReplacementRule { From = "brb", To = "Скоро вернусь" },
                    new ReplacementRule { From = "ukr", To = "Слава Україні", Target = 2 },
                    new ReplacementRule { From = "hello", To = "Здравствуйте", Target = 1 } }, out error), error);
                Tests.Check(Settings.Load(Path.Combine(directory, "test-preferences.json")).Replacements.Count == 3, "rules not persisted");
            }, 200);
            type("brb "); add("CUSTOM phrase keeps active layout", delegate { Tests.Equal("Скоро вернусь ", form.TestEditor.Text); Tests.Check(Native.InputLanguage(Native.GetKeyboardLayout(0)) == Language.English, "layout not preserved"); }, 200);
            combination(120, 6); add("CUSTOM undo removes entire phrase", delegate { Tests.Equal("brb ", form.TestEditor.Text); }, 200);
            add("Reset", reset, 150); type("brb "); add("UNDONE rule suppressed for session", delegate { Tests.Equal("brb ", form.TestEditor.Text); }, 200);
            add("Reset", reset, 150); type("ukr "); add("CUSTOM target layout selected", delegate { Tests.Equal("Слава Україні ", form.TestEditor.Text); Tests.Check(Native.InputLanguage(Native.GetKeyboardLayout(0)) == Language.Ukrainian, "UK not selected"); }, 200);
            add("Reset", reset, 150); type("hello "); add("CUSTOM overrides dictionary word", delegate { Tests.Equal("Здравствуйте ", form.TestEditor.Text); }, 200);
            add("DISABLE custom rule", delegate { reset(); string error; Tests.Check(form.ApplyReplacements(new List<ReplacementRule> { new ReplacementRule { From = "ukr", To = "Слава Україні", Target = 2, Enabled = false } }, out error), error); }, 200);
            type("ukr "); add("DISABLED custom rule does not expand", delegate { Tests.Equal("ukr ", form.TestEditor.Text); }, 200);
            timer.Tick += delegate
            {
                timer.Stop();
                if (steps.Count == 0)
                {
                    Native.ActivateKeyboardLayout(original, 0); form.FinishTest(); return;
                }
                Step step = steps.Dequeue();
                try { step.Action(); if (!step.Name.StartsWith("key ")) log.Add("PASS " + step.Name); timer.Interval = step.Delay; timer.Start(); }
                catch (Exception e)
                {
                    failures++; log.Add("FAIL " + step.Name + ": " + e.Message);
                    // Release only modifiers created by this test, then stop.
                    Native.SendInput(3, new[] { Native.Key(16, true), Native.Key(18, true), Native.Key(17, true) }, Marshal.SizeOf(typeof(Native.Input)));
                    Native.ActivateKeyboardLayout(original, 0); form.FinishTest();
                }
            };
            form.Shown += delegate { timer.Start(); };
            Application.Run(form); timer.Dispose();
            log.Add("Integration results: " + failures + " failed");
            File.WriteAllLines(Path.Combine(directory, "integration-test-results.txt"), log, Encoding.UTF8);
            return failures == 0 ? 0 : 1;
        }
    }
}
