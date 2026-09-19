using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace TriSwitch
{
    // Scope this workaround to live Notepad editors. It does not change Windows
    // language profiles or reformat existing document text.
    internal sealed class NotepadFontGuard : IDisposable
    {
        private const long FontOptions = 0x12; // IMF_AUTOFONT | IMF_AUTOFONTSIZEADJUST
        private readonly string marker = "TriSwitch.FontGuard." + Guid.NewGuid().ToString("N");
        private readonly Dictionary<IntPtr, long> originals = new Dictionary<IntPtr, long>();
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool SetProp(IntPtr window, string name, IntPtr value);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetProp(IntPtr window, string name);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr RemoveProp(IntPtr window, string name);
        [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW")]
        private static extern IntPtr Message(IntPtr window, uint message, IntPtr first, IntPtr last, uint flags, uint timeout, out IntPtr result);

        internal bool Protect(FocusStamp focus)
        {
            if (!focus.Same(Native.Focus())) return false;
            var kind = new StringBuilder(128); Native.GetClassName(focus.Control, kind, kind.Capacity);
            if (kind.ToString() != "RichEditD2DPT") return false;
            try { using (var process = Process.GetProcessById((int)focus.Process)) if (!process.ProcessName.Equals("Notepad", StringComparison.OrdinalIgnoreCase)) return false; }
            catch (ArgumentException) { return false; }
            catch (InvalidOperationException) { return false; }
            catch (System.ComponentModel.Win32Exception) { return false; }
            IntPtr options, result;
            if (Message(focus.Control, 0x479, IntPtr.Zero, IntPtr.Zero, 2, 100, out options) == IntPtr.Zero) return false;
            long value = options.ToInt64();
            if ((value & FontOptions) == 0) return true;
            foreach (IntPtr old in new List<IntPtr>(originals.Keys))
                if (GetProp(old, marker) == IntPtr.Zero) originals.Remove(old);
            if (!originals.ContainsKey(focus.Control))
            {
                if (!SetProp(focus.Control, marker, new IntPtr(1))) return false;
                originals.Add(focus.Control, value & FontOptions);
            }
            return focus.Same(Native.Focus()) && Message(focus.Control, 0x478, IntPtr.Zero, new IntPtr(value & ~FontOptions), 2, 100, out result) != IntPtr.Zero;
        }

        public void Dispose()
        {
            foreach (var entry in originals)
            {
                // Properties disappear when an HWND is destroyed, preventing restoration
                // into an unrelated window if Windows has reused its handle.
                if (GetProp(entry.Key, marker) == IntPtr.Zero) continue;
                IntPtr current, result;
                if (Message(entry.Key, 0x479, IntPtr.Zero, IntPtr.Zero, 2, 100, out current) != IntPtr.Zero)
                    Message(entry.Key, 0x478, IntPtr.Zero, new IntPtr((current.ToInt64() & ~FontOptions) | entry.Value), 2, 100, out result);
                RemoveProp(entry.Key, marker);
            }
            originals.Clear();
        }
    }
}
