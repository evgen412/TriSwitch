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
        public static int Run(string directory)
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
                Tests.Equal(prefix + current, value.Current.Value);
                IntPtr result;
                Tests.Check(Message(focus.Control, 0xB1, new IntPtr(-1), new IntPtr(-1), 2, 500, out result) != IntPtr.Zero, "Caret positioning failed");
                var guard = new FocusGuard(); var catalog = new LayoutCatalog();
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
            File.WriteAllLines(Path.Combine(directory, "notepad-test-results.txt"), log, Encoding.UTF8);
            return exit;
        }
    }
}
