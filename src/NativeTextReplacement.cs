using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace TriSwitch
{
    public static partial class Native
    {
        public static bool SwitchInputLayout(FocusStamp focus, IntPtr layout)
        {
            // Let the application's outer window coordinate its input-language change.
            return focus.Same(Focus()) && PostMessage(focus.Window, 0x50, IntPtr.Zero, layout);
        }
        [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW", SetLastError = true)]
        private static extern IntPtr EditSelection(IntPtr window, uint message, ref int start, ref int end, uint flags, uint timeout, out IntPtr result);
        [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW", SetLastError = true)]
        private static extern IntPtr EditMessage(IntPtr window, uint message, IntPtr first, IntPtr last, uint flags, uint timeout, out IntPtr result);
        [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr EditText(IntPtr window, uint message, IntPtr undo, string text, uint flags, uint timeout, out IntPtr result);

        internal static bool IsNativeEditor(IntPtr control)
        {
            var name = new StringBuilder(128); GetClassName(control, name, name.Capacity);
            string kind = name.ToString();
            return kind.StartsWith("RichEdit", StringComparison.OrdinalIgnoreCase)
                || kind.Equals("Edit", StringComparison.OrdinalIgnoreCase)
                || kind.StartsWith("WindowsForms10.EDIT.", StringComparison.OrdinalIgnoreCase);
        }
        internal static bool ReadSelection(IntPtr control, out int start, out int end)
        {
            start = end = -1; IntPtr result;
            return EditSelection(control, 0xB0, ref start, ref end, 2, 500, out result) != IntPtr.Zero && start >= 0 && end >= start;
        }
        internal static bool ReplaceSelection(SelectionSnapshot selection, string replacement, Func<bool> stillCurrent, Func<bool> canContinue = null)
        {
            if (!stillCurrent() || !selection.Focus.Same(Focus()) || !SelectionCycle.Supported(replacement)) return false;
            if (selection.NativeEditor)
            {
                int start, end; IntPtr result;
                if (!ReadSelection(selection.Focus.Control, out start, out end) || start != selection.Start || end != selection.End
                    || (GetWindowLong(selection.Focus.Control, -16) & (0x20 | 0x800)) != 0 || !stillCurrent()) return false;
                // One undoable edit. Never fall back to injected input after a delivery timeout.
                if (EditText(selection.Focus.Control, 0xC2, new IntPtr(1), replacement, 2, 500, out result) == IntPtr.Zero) return false;
                if (!selection.Focus.Same(Focus()) || ModifiersDown || (canContinue != null && !canContinue())) return false;
                return EditMessage(selection.Focus.Control, 0xB1, new IntPtr(start), new IntPtr(start + replacement.Length), 2, 500, out result) != IntPtr.Zero;
            }
            // Accessible non-native editors receive a single input batch. No clipboard access.
            // Supported text excludes grapheme clusters and line breaks, so Shift+Left counts
            // the same BMP characters that were inserted. Verify the resulting selection later.
            var inputs = new List<Input>();
            foreach (char c in replacement)
            {
                inputs.Add(new Input { Type = 1, Data = new InputUnion { Keyboard = new KeyboardInput { Scan = c, Flags = 4, Extra = new UIntPtr(OwnTag) } } });
                inputs.Add(new Input { Type = 1, Data = new InputUnion { Keyboard = new KeyboardInput { Scan = c, Flags = 6, Extra = new UIntPtr(OwnTag) } } });
            }
            inputs.Add(Key(16, false));
            for (int i = 0; i < replacement.Length; i++) { inputs.Add(Key(37, false)); inputs.Add(Key(37, true)); }
            inputs.Add(Key(16, true));
            if (!stillCurrent()) return false;
            uint sent = SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf(typeof(Input)));
            if (sent != inputs.Count)
            {
                // A partial batch may have pressed our Shift; release it without retrying text.
                if (sent > replacement.Length * 2) SendInput(1, new[] { Key(16, true) }, Marshal.SizeOf(typeof(Input)));
                return false;
            }
            return true;
        }

        public static bool ReplaceChecked(FocusStamp focus, string original, string replacement, Func<bool> stillCurrent)
        {
            if (!stillCurrent() || !focus.Same(Focus())) return false;
            if (!IsNativeEditor(focus.Control)) return Replace(original.Length, replacement);

            // Native editors accept a UTF-16 string in one undoable operation. In particular,
            // modern Notepad need not interpret a burst of VK_PACKET events during a layout switch.
            // System messages (< WM_USER) are marshalled by Windows across process boundaries.
            int start = -1, end = -1; IntPtr result;
            if (EditSelection(focus.Control, 0xB0, ref start, ref end, 2, 500, out result) == IntPtr.Zero
                || start != end || start < original.Length || (GetWindowLong(focus.Control, -16) & (0x20 | 0x800)) != 0
                || !stillCurrent() || !focus.Same(Focus())) return false;
            int first = start - original.Length;
            if (EditMessage(focus.Control, 0xB1, new IntPtr(first), new IntPtr(end), 2, 500, out result) == IntPtr.Zero) return false;
            int selectedStart = -1, selectedEnd = -1;
            bool selected = EditSelection(focus.Control, 0xB0, ref selectedStart, ref selectedEnd, 2, 500, out result) != IntPtr.Zero
                && selectedStart == first && selectedEnd == end;
            if (!selected || !stillCurrent() || !focus.Same(Focus()))
            {
                if (selected && stillCurrent() && focus.Same(Focus()))
                    EditMessage(focus.Control, 0xB1, new IntPtr(end), new IntPtr(end), 2, 500, out result);
                return false;
            }
            // Never retry with Backspace/SendInput after a timeout: delivery may have occurred.
            return EditText(focus.Control, 0xC2, new IntPtr(1), replacement, 2, 500, out result) != IntPtr.Zero;
        }
    }
}
