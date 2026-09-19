using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace TriSwitch
{
    public static class Native
    {
        public const uint OwnTag = 0x54524953;
        internal const uint TestTag = 0x54524954;
        public delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);
        [StructLayout(LayoutKind.Sequential)] public struct Point { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)] public struct Rect { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] public struct GuiInfo
        {
            public int Size; public uint Flags;
            public IntPtr Active, Focus, Capture, MenuOwner, MoveSize, Caret;
            public Rect CaretRect;
        }
        [StructLayout(LayoutKind.Sequential)] public struct KeyData { public uint Vk, Scan, Flags, Time; public UIntPtr Extra; }
        [StructLayout(LayoutKind.Sequential)] public struct KeyboardInput { public ushort Vk, Scan; public uint Flags, Time; public UIntPtr Extra; }
        [StructLayout(LayoutKind.Sequential)] public struct MouseInput { public int X, Y; public uint Data, Flags, Time; public UIntPtr Extra; }
        [StructLayout(LayoutKind.Explicit)] public struct InputUnion
        {
            [FieldOffset(0)] public KeyboardInput Keyboard;
            [FieldOffset(0)] public MouseInput Mouse;
        }
        [StructLayout(LayoutKind.Sequential)] public struct Input { public uint Type; public InputUnion Data; }
        [DllImport("user32.dll", SetLastError = true)] public static extern IntPtr SetWindowsHookEx(int id, HookProc proc, IntPtr module, uint thread);
        [DllImport("user32.dll")] public static extern bool UnhookWindowsHookEx(IntPtr hook);
        [DllImport("user32.dll")] public static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wp, IntPtr lp);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr GetModuleHandle(string name);
        [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
        [DllImport("user32.dll")] public static extern bool PostThreadMessage(uint thread, uint message, IntPtr wp, IntPtr lp);
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint process);
        [DllImport("user32.dll")] public static extern bool GetGUIThreadInfo(uint thread, ref GuiInfo info);
        [DllImport("user32.dll")] public static extern IntPtr GetKeyboardLayout(uint thread);
        [DllImport("user32.dll")] public static extern IntPtr ActivateKeyboardLayout(IntPtr layout, uint flags);
        [DllImport("user32.dll")] public static extern int GetKeyboardLayoutList(int count, [Out] IntPtr[] layouts);
        [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int key);
        [DllImport("user32.dll")] public static extern short GetKeyState(int key);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int ToUnicodeEx(uint vk, uint scan, byte[] state, StringBuilder text, int length, uint flags, IntPtr layout);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern short VkKeyScanEx(char ch, IntPtr layout);
        [DllImport("user32.dll")] public static extern uint MapVirtualKeyEx(uint code, uint type, IntPtr layout);
        [DllImport("user32.dll", SetLastError = true)] public static extern uint SendInput(uint count, Input[] inputs, int size);
        [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hwnd, uint message, IntPtr wp, IntPtr lp);
        [DllImport("user32.dll", SetLastError = true)] public static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint key);
        [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr hwnd, int id);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr hwnd, StringBuilder name, int size);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] public static extern int GetWindowLong(IntPtr hwnd, int index);
        [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hwnd);
        public static bool Down(int key) { return GetAsyncKeyState(key) < 0; }
        public static bool ModifiersDown { get { return Down(0x10) || Down(0x11) || Down(0x12) || Down(0x5b) || Down(0x5c); } }

        public static FocusStamp Focus()
        {
            IntPtr window = GetForegroundWindow(); uint process;
            uint thread = GetWindowThreadProcessId(window, out process);
            var info = new GuiInfo { Size = Marshal.SizeOf(typeof(GuiInfo)) };
            if (window == IntPtr.Zero || thread == 0 || !GetGUIThreadInfo(thread, ref info)) return new FocusStamp();
            return new FocusStamp { Window = window, Control = info.Focus, Process = process, Thread = thread };
        }
        public static Input Key(ushort key, bool up)
        {
            return new Input { Type = 1, Data = new InputUnion { Keyboard = new KeyboardInput { Vk = key, Flags = up ? 2u : 0u, Extra = new UIntPtr(OwnTag) } } };
        }
        public static bool Replace(int remove, string text)
        {
            var inputs = new List<Input>();
            for (int i = 0; i < remove; i++) { inputs.Add(Key(8, false)); inputs.Add(Key(8, true)); }
            foreach (char c in text)
            {
                inputs.Add(new Input { Type = 1, Data = new InputUnion { Keyboard = new KeyboardInput { Scan = c, Flags = 4, Extra = new UIntPtr(OwnTag) } } });
                inputs.Add(new Input { Type = 1, Data = new InputUnion { Keyboard = new KeyboardInput { Scan = c, Flags = 6, Extra = new UIntPtr(OwnTag) } } });
            }
            return SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf(typeof(Input))) == inputs.Count;
        }
    }

    public struct FocusStamp
    {
        public IntPtr Window, Control;
        public uint Process, Thread;
        public bool Same(FocusStamp other) { return Window != IntPtr.Zero && Window == other.Window && Control == other.Control && Process == other.Process; }
    }

    public sealed class KeyEvent
    {
        public long Serial;
        public uint Vk, Scan;
        public bool Reset, Shift, Ctrl, Alt, Win, Caps;
        public FocusStamp Focus;
        public IntPtr Layout;
    }

    // Hooks have their own message loop. Accessibility/dictionaries/UI never run
    // inside the hook, so a slow application cannot stall physical keyboard input.
    public sealed class InputWatcher : IDisposable
    {
        private readonly Action<KeyEvent> deliver;
        private readonly bool testMode;
        private Native.HookProc keyProc, mouseProc;
        private IntPtr keyboard, mouse;
        private uint threadId;
        private Thread thread;
        private long serial;
        private Exception failure;
        private readonly ManualResetEvent ready = new ManualResetEvent(false);
        public long Serial { get { return Interlocked.Read(ref serial); } }
        public InputWatcher(Action<KeyEvent> deliver, bool testMode)
        {
            this.deliver = deliver; this.testMode = testMode;
            thread = new Thread(Run) { IsBackground = true, Name = "TriSwitch input" };
            thread.SetApartmentState(ApartmentState.STA); thread.Start();
            if (!ready.WaitOne(5000)) throw new InvalidOperationException("Не удалось запустить обработчик клавиатуры.");
            if (failure != null) throw failure;
        }
        private void Run()
        {
            threadId = Native.GetCurrentThreadId();
            keyProc = Keyboard; mouseProc = Mouse;
            try
            {
                keyboard = Native.SetWindowsHookEx(13, keyProc, Native.GetModuleHandle(null), 0);
                mouse = Native.SetWindowsHookEx(14, mouseProc, Native.GetModuleHandle(null), 0);
                if (keyboard == IntPtr.Zero || mouse == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            catch (Exception error) { failure = error; }
            ready.Set();
            if (failure == null) Application.Run();
            if (keyboard != IntPtr.Zero) Native.UnhookWindowsHookEx(keyboard);
            if (mouse != IntPtr.Zero) Native.UnhookWindowsHookEx(mouse);
        }
        private void Emit(KeyEvent e)
        {
            e.Serial = Interlocked.Increment(ref serial);
            try { deliver(e); } catch (InvalidOperationException) { }
        }
        private IntPtr Keyboard(int code, IntPtr wp, IntPtr lp)
        {
            if (code >= 0 && (wp.ToInt32() == 0x100 || wp.ToInt32() == 0x104))
            {
                var k = (Native.KeyData)Marshal.PtrToStructure(lp, typeof(Native.KeyData));
                if (k.Extra.ToUInt64() != Native.OwnTag)
                {
                    bool modifier = k.Vk == 16 || k.Vk == 17 || k.Vk == 18 || (k.Vk >= 160 && k.Vk <= 165);
                    if (!modifier)
                    {
                        FocusStamp focus = Native.Focus();
                        Emit(new KeyEvent { Vk = k.Vk, Scan = k.Scan, Reset = (k.Flags & 0x10) != 0 && !(testMode && k.Extra.ToUInt64() == Native.TestTag),
                            Focus = focus, Layout = Native.GetKeyboardLayout(focus.Thread), Shift = Native.Down(16), Ctrl = Native.Down(17),
                            Alt = Native.Down(18), Win = Native.Down(0x5b) || Native.Down(0x5c), Caps = (Native.GetKeyState(20) & 1) != 0 });
                    }
                }
            }
            return Native.CallNextHookEx(keyboard, code, wp, lp);
        }
        private IntPtr Mouse(int code, IntPtr wp, IntPtr lp)
        {
            int message = wp.ToInt32();
            if (code >= 0 && (message == 0x201 || message == 0x204 || message == 0x207 || message == 0x20a || message == 0x20b || message == 0x20e)) Emit(new KeyEvent { Reset = true });
            return Native.CallNextHookEx(mouse, code, wp, lp);
        }
        public void Dispose() { if (threadId != 0) Native.PostThreadMessage(threadId, 0x12, IntPtr.Zero, IntPtr.Zero); thread.Join(1000); ready.Dispose(); }
    }

    public sealed class LayoutCatalog
    {
        private readonly IntPtr[] handles = new IntPtr[3];
        public LayoutCatalog() { Refresh(); }
        public void Refresh()
        {
            Array.Clear(handles, 0, handles.Length);
            int count = Native.GetKeyboardLayoutList(0, null); var all = new IntPtr[count];
            Native.GetKeyboardLayoutList(count, all);
            foreach (IntPtr h in all) { Language? lang = Layouts.FromHandle(h); if (lang.HasValue && handles[(int)lang.Value] == IntPtr.Zero) handles[(int)lang.Value] = h; }
        }
        public bool Available(Language language) { return handles[(int)language] != IntPtr.Zero; }
        public string Status { get { return string.Join("   ·   ", new[] { Language.English, Language.Russian, Language.Ukrainian }.SelectStatus(this)); } }
        public string Convert(string text, Language source, Language target)
        {
            if (!Available(source) || !Available(target)) return Layouts.Convert(text, source, target);
            var result = new StringBuilder();
            foreach (char c in text)
            {
                short key = Native.VkKeyScanEx(c, handles[(int)source]);
                if (key == -1) { result.Append(c); continue; }
                var state = new byte[256]; int modifiers = (key >> 8) & 0xff;
                if ((modifiers & 1) != 0) state[16] = 0x80;
                if ((modifiers & 6) != 0) { result.Append(c); continue; }
                uint vk = (uint)(key & 0xff); var output = new StringBuilder(8);
                int length = Native.ToUnicodeEx(vk, Native.MapVirtualKeyEx(vk, 0, handles[(int)target]), state, output, 8, 4, handles[(int)target]);
                result.Append(length == 1 ? output[0] : c);
            }
            return result.ToString();
        }
        public bool Switch(FocusStamp focus, Language language)
        {
            return Available(language) && Native.PostMessage(focus.Window, 0x50, IntPtr.Zero, handles[(int)language]);
        }
    }
    internal static class CatalogExtensions
    {
        public static IEnumerable<string> SelectStatus(this Language[] values, LayoutCatalog catalog)
        { foreach (Language l in values) yield return Layouts.Names[(int)l] + (catalog.Available(l) ? " ✓" : " — не установлена"); }
    }
}
