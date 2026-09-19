using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Automation;

namespace TriSwitch
{
    internal static class NotepadTests
    {
        private delegate bool WindowVisitor(IntPtr window, IntPtr data);
        [DllImport("user32.dll")] private static extern bool EnumWindows(WindowVisitor visitor, IntPtr data);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr window, StringBuilder text, int count);
        [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW")]
        private static extern IntPtr Message(IntPtr window, uint message, IntPtr first, IntPtr last, uint flags, uint timeout, out IntPtr result);

        // Explicit opt-in. Only the unique document created here can be modified.
        public static int Run(string directory, bool hotkey = false, bool systemLayouts = false, bool protect = false)
        {
            var log = new List<string>();
            string marker = "TriSwitch-test-" + Guid.NewGuid().ToString("N");
            string path = Path.Combine(directory, marker + ".txt");
            string prefix = hotkey || systemLayouts ? "" : marker + " " + new string('x', 70000) + " ";
            string current = "Добрій ";
            File.WriteAllText(path, prefix + current, new UTF8Encoding(true));
            int exit = 0;
            try
            {
                Process.Start(new ProcessStartInfo("notepad.exe", "\"" + path + "\"") { UseShellExecute = true, WindowStyle = ProcessWindowStyle.Normal });
                IntPtr window = IntPtr.Zero;
                for (int attempt = 0; attempt < 50 && window == IntPtr.Zero; attempt++)
                {
                    Thread.Sleep(100);
                    EnumWindows(delegate(IntPtr w, IntPtr data) { var title = new StringBuilder(512); GetWindowText(w, title, title.Capacity); if (title.ToString().Contains(marker)) window = w; return true; }, IntPtr.Zero);
                }
                Tests.Check(window != IntPtr.Zero, "Owned test document did not open");
                Native.SetForegroundWindow(window); Thread.Sleep(250);
                var focus = Native.Focus(); Tests.Check(focus.Window == window, "Test document not focused");
                var element = AutomationElement.FocusedElement;
                Tests.Check(element.Current.ClassName == "RichEditD2DPT", "Expected modern Notepad editor");
                var value = (ValuePattern)element.GetCurrentPattern(ValuePattern.Pattern);
                for (int ready = 0; ready < 30 && value.Current.Value != prefix + current; ready++) Thread.Sleep(100);
                Tests.Check(value.Current.Value == prefix + current, "Owned test document content not ready");
                IntPtr result;
                Tests.Check(Message(focus.Control, 0xB1, new IntPtr(-1), new IntPtr(-1), 2, 500, out result) != IntPtr.Zero, "Caret positioning failed");
                var guard = new FocusGuard(); var catalog = new LayoutCatalog();
                if (systemLayouts)
                {
                    RunSystemLayouts(focus, element, log, protect);
                    File.WriteAllLines(Path.Combine(directory, "notepad-system-test-results.txt"), log, Encoding.UTF8);
                    return 0;
                }
                if (hotkey)
                {
                    Tests.Check(Native.ReplaceChecked(focus, current, "", () => focus.Same(Native.Focus())), "Clear fixture suffix");
                    RunHotkey(focus, value, prefix, log);
                    File.WriteAllLines(Path.Combine(directory, "notepad-hotkey-test-results.txt"), log, Encoding.UTF8);
                    return 0;
                }
                var pattern = (TextPattern)element.GetCurrentPattern(TextPattern.Pattern);
                Func<string> font = () => { var r = pattern.GetSelection()[0]; return r.GetAttributeValue(TextPattern.FontNameAttribute) + "/" + r.GetAttributeValue(TextPattern.FontSizeAttribute) + "/" + r.GetAttributeValue(TextPattern.FontWeightAttribute) + "/" + r.GetAttributeValue(TextPattern.IsItalicAttribute); };
                string initialFont = font(); log.Add("Initial font: " + initialFont);
                IntPtr languageOptions;
                Tests.Check(Message(focus.Control, 0x479, IntPtr.Zero, IntPtr.Zero, 2, 500, out languageOptions) != IntPtr.Zero, "Read language options");
                string[] texts = { "Добрый ", "добрій ", "добрый ", "ДОБРІЙ ", "ДОБРЫЙ ", "Hello ", "Скоро вернусь ", "Добрій " };
                for (int n = 0; n < 40; n++)
                {
                    string identity;
                    Tests.Check(guard.TryCheck(focus, out identity) && guard.TailMatches(current), "Caret/text guard failed");
                    string next = texts[n % texts.Length];
                    Tests.Check(Native.ReplaceChecked(focus, current, next, () => focus.Same(Native.Focus())), "Replacement failed");
                    bool switched = catalog.Switch(focus, (Language)(n % 3)); Tests.Check(switched, "Switch layout"); Thread.Sleep(40);
                    IntPtr restoredOptions;
                    Tests.Check(Message(focus.Control, 0x479, IntPtr.Zero, IntPtr.Zero, 2, 500, out restoredOptions) != IntPtr.Zero && restoredOptions == languageOptions, "Editor language options not restored");
                    Tests.Equal(prefix + next, value.Current.Value);
                    Tests.Check(focus.Same(Native.Focus()) && !Native.ModifiersDown, "Test editor focus changed");
                    Native.SendInput(2, new[] { Native.Key(65, false), Native.Key(65, true) }, Marshal.SizeOf(typeof(Native.Input))); Thread.Sleep(80);
                    string letter = n % 3 == 0 ? "a" : "ф";
                    Tests.Equal(prefix + next + letter, value.Current.Value);
                    Tests.Equal(initialFont, font());
                    Tests.Check(Native.ReplaceChecked(focus, letter, "", () => focus.Same(Native.Focus())), "Remove test letter");
                    current = next;
                }
                // Native undo must restore the preceding replacement as one operation.
                Tests.Check(Native.ReplaceChecked(focus, current, "Тест ", () => focus.Same(Native.Focus())), "Final undo fixture");
                Tests.Check(Message(focus.Control, 0xC7, IntPtr.Zero, IntPtr.Zero, 2, 500, out result) != IntPtr.Zero, "Undo failed");
                Tests.Equal(prefix + current, value.Current.Value);
                log.Add("PASS font family, size, weight and italic preserved for typed characters after 40 layout switches; original language options restored");
                log.Add("PASS 40 modern Notepad replacements, casing, phrases, layout switches, caret beyond 64K, native undo; exact Unicode text verified");
                log.Add("Owned test document: " + path);
            }
            catch (Exception e) { exit = 1; log.Add("FAIL " + e.Message); }
            File.WriteAllLines(Path.Combine(directory, systemLayouts ? "notepad-system-test-results.txt" : hotkey ? "notepad-hotkey-test-results.txt" : "notepad-test-results.txt"), log, Encoding.UTF8);
            return exit;
        }

        private static void RunSystemLayouts(FocusStamp focus, AutomationElement element, List<string> log, bool protect)
        {
            var pattern = (TextPattern)element.GetCurrentPattern(TextPattern.Pattern);
            Func<string> font = () => Convert.ToString(pattern.GetSelection()[0].GetAttributeValue(TextPattern.FontNameAttribute));
            string originalFont = font();
            IntPtr originalOptions, restored;
            Tests.Check(Message(focus.Control, 0x479, IntPtr.Zero, IntPtr.Zero, 2, 500, out originalOptions) != IntPtr.Zero, "Read options");
            log.Add("Options=" + originalOptions.ToString("X") + "; protect=" + protect);
            using (var protection = new NotepadFontGuard())
            {
                if (protect) Tests.Check(protection.Protect(focus), "Enable font protection");
                var layouts = new IntPtr[Native.GetKeyboardLayoutList(0, null)]; Native.GetKeyboardLayoutList(layouts.Length, layouts);
                foreach (IntPtr layout in layouts)
                {
                    Tests.Check(focus.Same(Native.Focus()) && !Native.ModifiersDown, "Test focus changed");
                    // Request each exact OS profile, including transient IDs. Do not call
                    // TriSwitch's layout selector, which may choose another RU profile.
                    Native.PostMessage(focus.Window, 0x50, IntPtr.Zero, layout); Thread.Sleep(250);
                    Tests.Check(Native.GetKeyboardLayout(focus.Thread) == layout, "Profile did not activate");
                    Native.SendInput(2, new[] { Native.Key(65, false), Native.Key(65, true) }, Marshal.SizeOf(typeof(Native.Input))); Thread.Sleep(150);
                    log.Add("Layout " + layout.ToString("X") + " font=" + font());
                    if (protect) Tests.Equal(originalFont, font());
                }
            }
            Tests.Check(Message(focus.Control, 0x479, IntPtr.Zero, IntPtr.Zero, 2, 500, out restored) != IntPtr.Zero && restored == originalOptions, "Original editor settings not restored");
            log.Add("PASS original editor options restored after disposal");
        }
        private static void RunHotkey(FocusStamp focus, ValuePattern value, string prefix, List<string> log)
        {
            var form = new MainWindow(true, true);
            var latency = new Stopwatch();
            var pattern = (TextPattern)AutomationElement.FocusedElement.GetCurrentPattern(TextPattern.Pattern);
            Func<string> font = () => Convert.ToString(pattern.GetSelection()[0].GetAttributeValue(TextPattern.FontNameAttribute));
            string initialFont = font(); log.Add("Initial font: " + initialFont);
            form.TestTrace = stage => log.Add(stage + ": " + latency.ElapsedMilliseconds + " ms since Ctrl release");
            var timer = new System.Windows.Forms.Timer { Interval = 400 };
            var steps = new Queue<Action>(); Exception failure = null;
            var catalog = new LayoutCatalog();
            Action<ushort, bool> key = (vk, up) =>
            {
                Tests.Check(focus.Same(Native.Focus()), "Test document lost focus; no input sent");
                var input = Native.Key(vk, up); input.Data.Keyboard.Extra = new UIntPtr(Native.TestTag);
                Tests.Check(Native.SendInput(1, new[] { input }, Marshal.SizeOf(typeof(Native.Input))) == 1, "Test input failed");
            };
            steps.Enqueue(delegate
            {
                string error; var bindings = HotkeyBinding.Defaults(); bindings[4] = new HotkeyBinding { Key = 46, Modifiers = 2 };
                Tests.Check(form.ApplyHotkeys(bindings, out error), error);
                Tests.Check(form.ApplyReplacements(new List<ReplacementRule> { new ReplacementRule { From = "добрій", To = "добрый", Target = 1, PreserveCase = true } }, out error), error);
            });
            string preceding = prefix;
            for (int cycle = 0; cycle < 3; cycle++)
            {
                string before = preceding;
                steps.Enqueue(() => Tests.Check(catalog.Switch(focus, Language.Ukrainian), "Select UK"));
                steps.Enqueue(() => key(16, false));
                foreach (char c in "Lj,hsq ")
                {
                    ushort vk = c == ',' ? (ushort)188 : c == ' ' ? (ushort)32 : (ushort)char.ToUpperInvariant(c);
                    steps.Enqueue(delegate { key(vk, false); key(vk, true); });
                    if (c == 'L') steps.Enqueue(() => key(16, true));
                }
                steps.Enqueue(delegate { log.Add("Before undo: " + form.TestState); Tests.Equal(before + "Добрый ", value.Current.Value); Tests.Equal(initialFont, font()); });
                steps.Enqueue(() => key(17, false));
                steps.Enqueue(delegate { key(46, false); key(46, true); });
                steps.Enqueue(delegate { latency.Restart(); key(17, true); });
                steps.Enqueue(delegate { log.Add("After undo: " + form.TestState); Tests.Equal(before + "Добрій ", value.Current.Value); Tests.Equal(initialFont, font()); log.Add("PASS Notepad Ctrl+Delete undo and repeated replacement"); });
                preceding += "Добрій ";
            }
            steps.Enqueue(() => Tests.Check(catalog.Switch(focus, Language.Ukrainian), "Select UK for manual conversion"));
            foreach (char c in "ghbdsn ")
            {
                ushort vk = c == ' ' ? (ushort)32 : (ushort)char.ToUpperInvariant(c);
                steps.Enqueue(delegate { key(vk, false); key(vk, true); });
            }
            steps.Enqueue(delegate { key(17, false); key(18, false); });
            steps.Enqueue(delegate { key(50, false); key(50, true); });
            steps.Enqueue(delegate { key(18, true); key(17, true); });
            steps.Enqueue(delegate { Tests.Equal(preceding + "привыт ", value.Current.Value); log.Add("Manual conversion font: " + font()); Tests.Equal(initialFont, font()); });
            steps.Enqueue(delegate { key(65, false); key(65, true); });
            steps.Enqueue(delegate { log.Add("Following letter font: " + font()); Tests.Equal(initialFont, font()); Tests.Equal(preceding + "привыт ф", value.Current.Value); });
            timer.Tick += delegate
            {
                timer.Stop();
                try
                {
                    if (steps.Count == 0) { form.FinishTest(); return; }
                    steps.Dequeue()(); timer.Interval = 120; timer.Start();
                }
                catch (Exception e) { failure = e; key(16, true); key(17, true); form.FinishTest(); }
            };
            timer.Start(); System.Windows.Forms.Application.Run(form); timer.Dispose();
            if (failure != null) throw failure;
        }
    }
}
