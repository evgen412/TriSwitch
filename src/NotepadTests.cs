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
        public static int Run(string directory, bool hotkey = false)
        {
            var log = new List<string>();
            string marker = "TriSwitch-test-" + Guid.NewGuid().ToString("N");
            string path = Path.Combine(directory, marker + ".txt");
            string prefix = marker + " " + new string('x', 70000) + " ";
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
                if (hotkey)
                {
                    Tests.Check(Native.ReplaceChecked(focus, current, "", () => focus.Same(Native.Focus())), "Clear fixture suffix");
                    RunHotkey(focus, value, prefix, log);
                    File.WriteAllLines(Path.Combine(directory, "notepad-hotkey-test-results.txt"), log, Encoding.UTF8);
                    return 0;
                }
                string[] texts = { "Добрый ", "добрій ", "добрый ", "ДОБРІЙ ", "ДОБРЫЙ ", "Hello ", "Скоро вернусь ", "Добрій " };
                for (int n = 0; n < 40; n++)
                {
                    string identity;
                    Tests.Check(guard.TryCheck(focus, out identity) && guard.TailMatches(current), "Caret/text guard failed");
                    string next = texts[n % texts.Length];
                    Tests.Check(Native.ReplaceChecked(focus, current, next, () => focus.Same(Native.Focus())), "Replacement failed");
                    catalog.Switch(focus, (Language)(n % 3)); Thread.Sleep(40);
                    Tests.Equal(prefix + next, value.Current.Value);
                    current = next;
                }
                // Native undo must restore the preceding replacement as one operation.
                Tests.Check(Message(focus.Control, 0xC7, IntPtr.Zero, IntPtr.Zero, 2, 500, out result) != IntPtr.Zero, "Undo failed");
                Tests.Equal(prefix + texts[6], value.Current.Value);
                log.Add("PASS 40 modern Notepad replacements, casing, phrases, layout switches, caret beyond 64K, native undo; exact Unicode text verified");
                log.Add("Owned test document: " + path);
            }
            catch (Exception e) { exit = 1; log.Add("FAIL " + e.Message); }
            File.WriteAllLines(Path.Combine(directory, hotkey ? "notepad-hotkey-test-results.txt" : "notepad-test-results.txt"), log, Encoding.UTF8);
            return exit;
        }

        private static void RunHotkey(FocusStamp focus, ValuePattern value, string prefix, List<string> log)
        {
            var form = new MainWindow(true, true);
            var latency = new Stopwatch();
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
                steps.Enqueue(delegate { log.Add("Before undo: " + form.TestState); Tests.Equal(before + "Добрый ", value.Current.Value); });
                steps.Enqueue(() => key(17, false));
                steps.Enqueue(delegate { key(46, false); key(46, true); });
                steps.Enqueue(delegate { latency.Restart(); key(17, true); });
                steps.Enqueue(delegate { log.Add("After undo: " + form.TestState); Tests.Equal(before + "Добрій ", value.Current.Value); log.Add("PASS Notepad Ctrl+Delete undo and repeated replacement"); });
                preceding += "Добрій ";
            }
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
