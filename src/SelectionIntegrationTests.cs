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
    internal static class SelectionIntegrationTests
    {
        private sealed class Step { internal string Name; internal Action Action; internal int Delay; }
        internal static int Run(string directory)
        {
            var log = new List<string>(); var steps = new Queue<Step>(); int failures = 0;
            var form = new MainWindow(false, true); var timer = new Timer { Interval = 300 }; DateTime activationDeadline = DateTime.UtcNow.AddSeconds(8);
            IntPtr originalLayout = Native.GetKeyboardLayout(0);
            var installed = new IntPtr[Native.GetKeyboardLayoutList(0, null)]; Native.GetKeyboardLayoutList(installed.Length, installed);
            Action<Language> activate = language =>
            {
                IntPtr layout = installed.FirstOrDefault(h => Native.InputLanguage(h) == language);
                Tests.Check(layout != IntPtr.Zero, "layout missing: " + language); Native.ActivateKeyboardLayout(layout, 0);
            };
            Action focus = delegate { Tests.Check(Native.GetForegroundWindow() == form.Handle && form.TestEditor.Focused, "Owned test editor lost focus; no input sent"); };
            Action<ushort, bool> key = (vk, up) =>
            {
                focus(); Native.Input input = Native.Key(vk, up); input.Data.Keyboard.Extra = new UIntPtr(Native.TestTag);
                Tests.Check(Native.SendInput(1, new[] { input }, Marshal.SizeOf(typeof(Native.Input))) == 1, "test input failed");
            };
            Action<string, Action, int> add = (name, action, delay) => steps.Enqueue(new Step { Name = name, Action = action, Delay = delay });
            Action<ushort, uint> shortcut = (vk, modifiers) =>
            {
                add("modifiers down", delegate { if ((modifiers & 2) != 0) key(17, false); if ((modifiers & 1) != 0) key(18, false); if ((modifiers & 4) != 0) key(16, false); }, 60);
                add("hotkey", delegate { key(vk, false); key(vk, true); }, 60);
                add("modifiers up", delegate { if ((modifiers & 4) != 0) key(16, true); if ((modifiers & 1) != 0) key(18, true); if ((modifiers & 2) != 0) key(17, true); }, 250);
            };
            Action<string, int, int, Language> fixture = (text, start, length, language) =>
            {
                add("Prepare selection", delegate
                {
                    focus(); key(27, false); key(27, true);
                    form.TestEditor.Text = text; form.TestEditor.Select(start, length); activate(language);
                }, 120);
            };
            Action<string, string, int, Language> verify = (name, text, start, language) => add(name, delegate
            {
                focus(); Tests.Equal(text, form.TestEditor.Text);
                Tests.Check(form.TestEditor.SelectionStart == start, "selection start changed");
                Tests.Check(form.TestEditor.SelectionLength > 0, "selection was lost");
                Tests.Check(Native.InputLanguage(Native.GetKeyboardLayout(0)) == language, "wrong input language");
            }, 50);
            add("Start owned editor", delegate
            {
                form.Show(); form.WindowState = FormWindowState.Normal; form.Activate(); Native.SetForegroundWindow(form.Handle); form.TestEditor.Focus(); focus();
                Tests.Check(form.IsWatching, "hooks not started");
            }, 200);
            fixture("left ghbdsn right", 5, 6, Language.Russian);
            shortcut(0x76, 3); verify("EN selection -> UK despite active RU; surrounding text preserved", "left привіт right", 5, Language.Ukrainian);
            shortcut(0x76, 3); verify("Second press -> RU with selection preserved", "left привыт right", 5, Language.Russian);
            shortcut(8, 3); verify("Selection undo restores previous text and layout", "left привіт right", 5, Language.Ukrainian);
            shortcut(0x76, 3); verify("Cycle can restart after undo", "left привыт right", 5, Language.Russian);
            shortcut(0x76, 3); verify("RU -> EN", "left ghbdsn right", 5, Language.English);
            shortcut(0x76, 3); verify("Full repeated cycle -> UK", "left привіт right", 5, Language.Ukrainian);
            fixture("ІЇЄ", 0, 3, Language.English);
            shortcut(0x76, 3); verify("Ukrainian capitals -> Russian", "ЫЪЭ", 0, Language.Russian);
            shortcut(0x76, 3); verify("Russian capitals -> physical English keys", "S}\"", 0, Language.English);
            shortcut(0x76, 3); verify("Round trip restores exact original capitals", "ІЇЄ", 0, Language.Ukrainian);
            fixture("ї", 0, 1, Language.Ukrainian);
            shortcut(0x76, 3); verify("Single Ukrainian letter -> Russian", "ъ", 0, Language.Russian);
            shortcut(0x76, 3); verify("Punctuation-only English variant stays selected", "]", 0, Language.English);
            shortcut(0x76, 3); verify("Cycle returns from punctuation to original letter", "ї", 0, Language.Ukrainian);
            fixture("мама", 0, 4, Language.Ukrainian);
            shortcut(0x76, 3); verify("Shared Cyrillic text still advances to RU", "мама", 0, Language.Russian);
            shortcut(0x76, 3); verify("Unchanged text does not stall cycling", "vfvf", 0, Language.English);
            fixture("s s", 0, 1, Language.English);
            shortcut(0x76, 3); verify("First occurrence converts", "і s", 0, Language.Ukrainian);
            add("Programmatically select a different occurrence", delegate { form.TestEditor.Select(2, 1); }, 70);
            shortcut(0x76, 3); verify("New range starts a new cycle", "і і", 2, Language.Ukrainian);
            fixture("ghbdsn", 6, 0, Language.English);
            shortcut(0x76, 3); add("Empty selection leaves word unchanged", delegate { Tests.Equal("ghbdsn", form.TestEditor.Text); Tests.Check(form.TestEditor.SelectionLength == 0, "unexpected selection"); }, 50);
            fixture("ghbdsn", 0, 6, Language.English);
            add("Make editor read-only", delegate { form.TestEditor.ReadOnly = true; }, 50);
            shortcut(0x76, 3); add("Read-only selection unchanged", delegate { Tests.Equal("ghbdsn", form.TestEditor.Text); form.TestEditor.ReadOnly = false; }, 50);
            fixture("ghbdsn", 0, 6, Language.English);
            add("Password mode", delegate { form.TestEditor.Multiline = false; form.TestEditor.UseSystemPasswordChar = true; form.TestEditor.SelectAll(); }, 50);
            shortcut(0x76, 3); add("Password selection unchanged", delegate { Tests.Equal("ghbdsn", form.TestEditor.Text); form.TestEditor.UseSystemPasswordChar = false; form.TestEditor.Multiline = true; }, 50);
            fixture("ghbdsn", 0, 6, Language.English);
            add("Exclude owned process", delegate { form.SetTestExclusions(Process.GetCurrentProcess().ProcessName); }, 50);
            shortcut(0x76, 3); add("Excluded app selection unchanged", delegate { Tests.Equal("ghbdsn", form.TestEditor.Text); form.SetTestExclusions(""); }, 50);
            fixture("ghbdsn", 0, 6, Language.English);
            shortcut(32, 3); shortcut(0x76, 3); add("Pause suppresses selection replacement", delegate { Tests.Equal("ghbdsn", form.TestEditor.Text); }, 50); shortcut(32, 3);
            fixture("wordслово", 0, 9, Language.English);
            shortcut(0x76, 3); add("Mixed alphabet unchanged", delegate { Tests.Equal("wordслово", form.TestEditor.Text); }, 50);
            fixture("left ghbdsn right", 5, 6, Language.English);
            add("Exercise accessible-editor input path", delegate
            {
                focus(); var guard = new FocusGuard(); var snapshot = SelectionSnapshot.Capture(Native.Focus(), guard);
                Tests.Check(snapshot != null, "cannot capture selection"); snapshot.NativeEditor = false;
                Tests.Check(Native.ReplaceSelection(snapshot, "привіт", () => snapshot.Same(SelectionSnapshot.Capture(snapshot.Focus, guard)) && snapshot.Focus.Same(Native.Focus())), "input path rejected");
            }, 300);
            add("Input path replaces only selection and reselects it", delegate { Tests.Equal("left привіт right", form.TestEditor.Text); Tests.Equal("привіт", form.TestEditor.SelectedText); Tests.Check(form.TestEditor.SelectionStart == 5, "input path selection drift"); }, 50);
            add("Stale range rejected before input", delegate
            {
                var guard = new FocusGuard(); var snapshot = SelectionSnapshot.Capture(Native.Focus(), guard);
                Tests.Check(snapshot != null, "snapshot missing"); form.TestEditor.Select(0, 4);
                Tests.Check(!Native.ReplaceSelection(snapshot, "wrong", () => snapshot.Same(SelectionSnapshot.Capture(snapshot.Focus, guard))), "stale selection accepted");
                Tests.Equal("left привіт right", form.TestEditor.Text);
            }, 50);
            add("Remap and persist selection shortcut", delegate
            {
                var bindings = HotkeyBinding.Defaults(); bindings[6] = new HotkeyBinding { Key = 0x78, Modifiers = 6 };
                string error; Tests.Check(form.ApplyHotkeys(bindings, out error), error);
                Tests.Check(Settings.Load(Path.Combine(directory, "test-preferences.json")).Hotkeys[6].Same(bindings[6]), "selection shortcut not persisted");
            }, 50);
            fixture("ghbdsn", 0, 6, Language.English);
            shortcut(0x76, 3); add("Old selection shortcut is released", delegate { Tests.Equal("ghbdsn", form.TestEditor.Text); }, 50);
            fixture("ghbdsn", 0, 6, Language.English);
            shortcut(0x78, 6); verify("Remapped selection shortcut works", "привіт", 0, Language.Ukrainian);
            timer.Tick += delegate
            {
                timer.Stop();
                if (steps.Count > 0 && steps.Peek().Name == "Start owned editor" && Native.GetForegroundWindow() != form.Handle && DateTime.UtcNow < activationDeadline)
                {
                    form.Show(); form.WindowState = FormWindowState.Normal; form.Activate(); Native.SetForegroundWindow(form.Handle); timer.Start(); return;
                }
                if (steps.Count == 0) { Native.ActivateKeyboardLayout(originalLayout, 0); form.FinishTest(); return; }
                Step step = steps.Dequeue();
                try { step.Action(); if (!step.Name.StartsWith("modifier") && step.Name != "hotkey") log.Add("PASS " + step.Name); timer.Interval = step.Delay; timer.Start(); }
                catch (Exception error)
                {
                    failures++; log.Add("FAIL " + step.Name + ": " + error.Message); log.Add(form.TestState);
                    Native.SendInput(3, new[] { Native.Key(16, true), Native.Key(18, true), Native.Key(17, true) }, Marshal.SizeOf(typeof(Native.Input)));
                    Native.ActivateKeyboardLayout(originalLayout, 0); form.FinishTest();
                }
            };
            form.Shown += delegate { timer.Start(); }; Application.Run(form); timer.Dispose();
            log.Add("Selection integration results: " + failures + " failed");
            File.WriteAllLines(Path.Combine(directory, "selection-test-results.txt"), log, Encoding.UTF8);
            return failures == 0 ? 0 : 1;
        }
    }
}
