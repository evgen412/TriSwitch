using System;
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

        public static bool ReplaceChecked(FocusStamp focus, string original, string replacement, Func<bool> stillCurrent)
        {
            if (!stillCurrent() || !focus.Same(Focus())) return false;
            var name = new StringBuilder(128); GetClassName(focus.Control, name, name.Capacity);
            string kind = name.ToString();
            bool nativeEdit = kind.StartsWith("RichEdit", StringComparison.OrdinalIgnoreCase)
                || kind.Equals("Edit", StringComparison.OrdinalIgnoreCase)
                || kind.StartsWith("WindowsForms10.EDIT.", StringComparison.OrdinalIgnoreCase);
            if (!nativeEdit) return Replace(original.Length, replacement);

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
