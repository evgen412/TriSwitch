using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace TriSwitch
{
    internal static class SpellingIntegrationTests
    {
        private sealed class Step { public string Name; public Action Action; public int Delay; }

        public static int Run(string directory)
        {
            var log = new List<string>();
            var steps = new Queue<Step>();
            var pressed = new HashSet<ushort>();
            int failures = 0;
            bool activated = false, complete = false;
            IntPtr original = Native.GetKeyboardLayout(0);
            string settingsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "test-preferences.json");
            var form = new MainWindow(false, true);
            var timer = new Timer { Interval = 500 };
            var installed = new IntPtr[Native.GetKeyboardLayoutList(0, null)];
            Native.GetKeyboardLayoutList(installed.Length, installed);
            Action focus = delegate
            {
                Tests.Check(Native.GetForegroundWindow() == form.Handle && form.TestEditor.Focused,
                    "Test field lost focus; no input sent");
            };
            Action<Language> activate = delegate(Language language)
            {
                focus();
                IntPtr handle = installed.OrderBy(h => Layouts.FromHandle(h).HasValue ? 1 : 0)
                    .FirstOrDefault(h => Native.InputLanguage(h) == language);
                Tests.Check(handle != IntPtr.Zero, "Missing " + language + " input profile");
                Native.ActivateKeyboardLayout(handle, 0);
                Tests.Check(Native.InputLanguage(Native.GetKeyboardLayout(0)) == language,
                    "Could not activate " + language + " input profile");
            };
            Action<ushort, bool> key = delegate(ushort vk, bool up)
            {
                focus();
                Native.Input input = Native.Key(vk, up);
                input.Data.Keyboard.Extra = new UIntPtr(Native.TestTag);
                Tests.Check(Native.SendInput(1, new[] { input }, Marshal.SizeOf(typeof(Native.Input))) == 1,
                    "SendInput failed");
                if (up) pressed.Remove(vk); else pressed.Add(vk);
            };
            Action<string, Action, int> add = (name, action, delay) =>
                steps.Enqueue(new Step { Name = name, Action = action, Delay = delay });
            Action<string> type = delegate(string value)
            {
                foreach (char character in value)
                {
                    Tests.Check(character == ' ' || (character >= 'a' && character <= 'z'),
                        "Only physical letter and space keys are supported by this test");
                    ushort vk = character == ' ' ? (ushort)32 : (ushort)char.ToUpperInvariant(character);
                    add("key " + character, delegate { key(vk, false); key(vk, true); }, character == ' ' ? 450 : 180);
                }
            };
            Action<Language> reset = delegate(Language language)
            {
                focus(); form.TestEditor.Clear(); key(27, false); key(27, true); activate(language);
            };
            Action<string, Language> expect = delegate(string text, Language language)
            {
                focus(); log.Add(form.TestState);
                Tests.Equal(text, form.TestEditor.Text);
                Tests.Check(Native.InputLanguage(Native.GetKeyboardLayout(0)) == language,
                    "Unexpected input profile after correction");
            };

            add("Activate spelling test window", delegate
            {
                form.Show(); form.WindowState = FormWindowState.Normal; form.Activate();
                Native.SetForegroundWindow(form.Handle); form.TestEditor.Focus(); focus(); activated = true;
            }, 600);
            add("Spelling setting is enabled by default", delegate
            {
                Tests.Check(form.IsWatching, "watcher unavailable");
                Tests.Check(!Native.ModifiersDown, "A physical modifier is held; no input sent");
                Tests.Check((Native.GetKeyState(20) & 1) == 0, "Caps Lock is on; no input sent");
                Tests.Check(form.TestSpellCheck != null && form.TestSpellCheck.Checked, "spelling disabled by default");
                reset(Language.Russian);
            }, 350);

            type("ghbdn ");
            add("Russian missing letter corrected", delegate { expect("привет ", Language.Russian); }, 500);
            add("Undo modifiers down", delegate { key(17, false); key(18, false); }, 80);
            add("Undo shortcut", delegate { key(8, false); key(8, true); }, 80);
            add("Undo modifiers up", delegate { key(18, true); key(17, true); }, 450);
            add("Undo restores original spelling and space", delegate { expect("привт ", Language.Russian); }, 500);
            type("ghbdn ");
            add("Next word is corrected after undo", delegate { expect("привт привет ", Language.Russian); }, 500);

            add("Reset Russian input", delegate { reset(Language.Russian); }, 300);
            type("ghjuhfvf ");
            add("Russian missing repeated letter corrected", delegate { expect("программа ", Language.Russian); }, 500);
            add("Reset English input", delegate { reset(Language.English); }, 300);
            type("helllo ");
            add("English extra letter corrected", delegate { expect("hello ", Language.English); }, 500);
            add("Reset Ukrainian input", delegate { reset(Language.Ukrainian); }, 300);
            type("ghbddsn ");
            add("Ukrainian extra letter corrected", delegate { expect("привіт ", Language.Ukrainian); }, 500);

            add("Disable spelling through settings control", delegate
            {
                form.TestSpellCheck.Checked = false;
                Tests.Check(!form.TestSpellCheck.Checked && !Settings.Load(settingsPath).SpellCheck,
                    "disabled spelling setting was not persisted");
                reset(Language.Russian);
            }, 350);
            type("ghbdn ");
            add("Disabled spelling leaves typo intact", delegate { expect("привт ", Language.Russian); }, 500);
            add("Reset English input with spelling disabled", delegate { reset(Language.English); }, 300);
            type("ghbdtn ");
            add("Layout correction still works with spelling disabled", delegate { expect("привет ", Language.Russian); }, 500);
            add("Enable spelling through settings control", delegate
            {
                form.TestSpellCheck.Checked = true;
                Tests.Check(form.TestSpellCheck.Checked && Settings.Load(settingsPath).SpellCheck,
                    "enabled spelling setting was not persisted");
                reset(Language.Russian);
            }, 350);
            type("ghbdn ");
            add("Spelling resumes immediately", delegate { expect("привет ", Language.Russian); }, 500);
            add("Custom replacement before spelling", delegate
            {
                string error;
                Tests.Check(form.ApplyReplacements(new List<ReplacementRule> {
                    new ReplacementRule { From = "привт", To = "Моя замена" } }, out error), error);
                reset(Language.Russian);
            }, 350);
            type("ghbdn ");
            add("Custom rule wins over spelling", delegate { expect("Моя замена ", Language.Russian); }, 500);

            timer.Tick += delegate
            {
                timer.Stop();
                if (steps.Count == 0) { complete = true; form.FinishTest(); return; }
                Step step = steps.Dequeue();
                try
                {
                    if (activated) focus();
                    step.Action();
                    if (!step.Name.StartsWith("key ")) log.Add("PASS " + step.Name);
                    timer.Interval = step.Delay; timer.Start();
                }
                catch (Exception error)
                {
                    failures++; log.Add("FAIL " + step.Name + ": " + error.Message);
                    form.FinishTest();
                }
            };
            form.Shown += delegate { timer.Start(); };
            try { Application.Run(form); }
            finally
            {
                timer.Stop(); timer.Dispose();
                // Release only keys successfully pressed by this test, even if focus was lost.
                foreach (ushort vk in pressed)
                {
                    Native.Input release = Native.Key(vk, true);
                    release.Data.Keyboard.Extra = new UIntPtr(Native.TestTag);
                    Native.SendInput(1, new[] { release }, Marshal.SizeOf(typeof(Native.Input)));
                }
                Native.ActivateKeyboardLayout(original, 0);
                if (!form.IsDisposed) form.FinishTest();
            }
            if (!complete && failures == 0) { failures++; log.Add("FAIL Spelling test ended before all steps completed"); }
            log.Add("Spelling integration results: " + failures + " failed");
            File.WriteAllLines(Path.Combine(directory, "spelling-test-results.txt"), log, Encoding.UTF8);
            return failures == 0 ? 0 : 1;
        }
    }
}
