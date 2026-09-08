using System;
using System.Collections.Generic;
using System.Windows.Forms;
using System.Runtime.InteropServices;
using System.Threading;

namespace ShowDesktopOneMonitor
{
    /// <summary>
    /// 用低级键盘钩子 (WH_KEYBOARD_LL) 自行实现热键检测，替代 RegisterHotKey。
    ///
    /// 为什么不用 RegisterHotKey：
    /// 系统处理带 MOD_WIN 的注册热键时，会在触发后吞掉 Win 键的 key-up
    /// （本意是阻止开始菜单弹出），但因此 Win 键的系统按键状态会残留在
    /// “按下”状态（之后再按 D 会被当成 Win+D）。这是 OS 层面的已知行为，
    /// 事后补发 key-up 只是补丁，时序上永远堵不完。
    ///
    /// 钩子的做法从根源上消除这个问题：
    /// - Win 键的 down/up 原样透传给系统，系统状态永远一致，不可能卡键；
    /// - 热键组合由我们自己检测，只吞掉热键主键（如 D）的 down/up；
    /// - 组合键触发时注入一个无害的空按键（vk 0xE8），让系统认为 Win 已
    ///   被用于组合键，从而不会在松开 Win 时弹出开始菜单。
    /// </summary>
    public static class HotKeyManager
    {
        public static event EventHandler<HotKeyEventArgs> HotKeyPressed;

        public static int RegisterHotKey(Keys key, KeyModifiers modifiers)
        {
            _windowReadyEvent.WaitOne();
            int id = System.Threading.Interlocked.Increment(ref _id);
            _wnd.Invoke(new RegisterHotKeyDelegate(RegisterHotKeyInternal), id, key, modifiers);
            return id;
        }

        public static void UnregisterHotKey(int id)
        {
            _wnd.Invoke(new UnRegisterHotKeyDelegate(UnRegisterHotKeyInternal), id);
        }

        delegate void RegisterHotKeyDelegate(int id, Keys key, KeyModifiers modifiers);
        delegate void UnRegisterHotKeyDelegate(int id);

        // 以下成员只在消息循环线程上访问（钩子回调与注册都在该线程）
        private static readonly Dictionary<int, HotKeyCombo> _combos = new Dictionary<int, HotKeyCombo>();

        private class HotKeyCombo
        {
            public Keys Key;
            public KeyModifiers Modifiers;
            public bool WaitingKeyUp; // 已触发并吞掉 key-down，等待吞掉对应的 key-up
        }

        private static void RegisterHotKeyInternal(int id, Keys key, KeyModifiers modifiers)
        {
            _combos[id] = new HotKeyCombo { Key = key, Modifiers = modifiers & ~KeyModifiers.NoRepeat };
        }

        private static void UnRegisterHotKeyInternal(int id)
        {
            _combos.Remove(id);
        }

        private static void OnHotKeyPressed(HotKeyEventArgs e)
        {
            if (HotKeyManager.HotKeyPressed != null)
            {
                HotKeyManager.HotKeyPressed(null, e);
            }
        }

        // ---- 修饰键状态 ----
        //
        // 直接向系统查询实时状态 (GetAsyncKeyState)，不自己缓存布尔值。
        //
        // 为什么不缓存：低级键盘钩子在安全桌面（Win+L 锁屏、UAC 提权、
        // Ctrl+Alt+Del）期间收不到任何按键事件。若锁屏时 Win 还按着，
        // 松开 Win 的 key-up 会落在安全桌面、钩子永远看不到，缓存的
        // “Win 按下”状态就此卡住；回到桌面后单按 D 会被误判为 Win+D
        // （表现为：锁屏解锁后按 D 稳定触发“显示桌面”，其它键正常）。
        // 改为每次现查系统真实状态，取丢事件也能自愈；注入按键（如 AHK
        // 重映射）同样会反映到 GetAsyncKeyState，故原有行为不变。
        private static bool IsKeyDown(uint vk)
        {
            return (GetAsyncKeyState((int)vk) & 0x8000) != 0;
        }

        private static bool IsWinDown { get { return IsKeyDown(VK_LWIN) || IsKeyDown(VK_RWIN); } }
        private static bool IsShiftDown { get { return IsKeyDown(VK_LSHIFT) || IsKeyDown(VK_RSHIFT); } }
        private static bool IsCtrlDown { get { return IsKeyDown(VK_LCONTROL) || IsKeyDown(VK_RCONTROL); } }
        private static bool IsAltDown { get { return IsKeyDown(VK_LMENU) || IsKeyDown(VK_RMENU); } }

        /// <summary>是否为修饰键本身（修饰键永远透传给系统，不参与热键主键判定）。</summary>
        private static bool IsModifierKey(uint vk)
        {
            switch (vk)
            {
                case VK_LWIN: case VK_RWIN:
                case VK_LSHIFT: case VK_RSHIFT:
                case VK_LCONTROL: case VK_RCONTROL:
                case VK_LMENU: case VK_RMENU:
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>修饰键必须精确匹配（与 RegisterHotKey 行为一致：多按 Ctrl 等则不触发）。</summary>
        private static bool ModifiersMatch(KeyModifiers modifiers)
        {
            if ((modifiers & KeyModifiers.Windows) != 0 != IsWinDown) return false;
            if ((modifiers & KeyModifiers.Shift) != 0 != IsShiftDown) return false;
            if ((modifiers & KeyModifiers.Control) != 0 != IsCtrlDown) return false;
            if ((modifiers & KeyModifiers.Alt) != 0 != IsAltDown) return false;
            return true;
        }

        // ---- 低级键盘钩子 ----

        private static IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                KBDLLHOOKSTRUCT kbd = (KBDLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(KBDLLHOOKSTRUCT));

                // 只跳过我们自己注入的空按键（通过 dwExtraInfo 标记识别）。
                // 其它注入事件（如 AHK 重映射发来的按键）必须正常参与检测——
                // RegisterHotKey 同样不区分物理/注入按键，否则 AHK 的
                // Win+D → Win+Shift+D 重映射会完全失效。
                bool isOwnDummy = (kbd.flags & LLKHF_INJECTED) != 0
                                  && kbd.dwExtraInfo == (IntPtr)DummyKeyExtraInfo;
                if (!isOwnDummy)
                {
                    int msg = wParam.ToInt32();
                    bool isDown = msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN;
                    bool isUp = msg == WM_KEYUP || msg == WM_SYSKEYUP;

                    if ((isDown || isUp) && !IsModifierKey(kbd.vkCode))
                    {
                        if (isDown)
                        {
                            // 只取第一个匹配的组合，避免多个组合绑定同一按键时重复触发
                            HotKeyCombo matched = null;
                            foreach (var combo in _combos.Values)
                            {
                                if ((uint)combo.Key == kbd.vkCode && ModifiersMatch(combo.Modifiers))
                                {
                                    matched = combo;
                                    break;
                                }
                            }
                            if (matched != null)
                            {
                                if (!matched.WaitingKeyUp)
                                {
                                    matched.WaitingKeyUp = true;
                                    // 含 Win 的组合：趁 Win 还按着注入空按键，
                                    // 防止松开 Win 时系统弹出开始菜单
                                    if ((matched.Modifiers & KeyModifiers.Windows) != 0)
                                    {
                                        InjectDummyKey();
                                    }
                                    // 通知消息窗口异步处理（钩子回调里不能做重活，
                                    // 否则超时被系统静默摘除钩子）
                                    PostMessage(_hwnd, WM_HOTKEY, IntPtr.Zero,
                                        MakeHotKeyLParam(matched.Key, matched.Modifiers));
                                }
                                // 吞掉热键主键的 down（含按住时的自动重复）
                                return (IntPtr)1;
                            }
                        }
                        else // isUp
                        {
                            foreach (var combo in _combos.Values)
                            {
                                if ((uint)combo.Key == kbd.vkCode && combo.WaitingKeyUp)
                                {
                                    combo.WaitingKeyUp = false;
                                    // 吞掉与被吞的 key-down 配对的 key-up
                                    return (IntPtr)1;
                                }
                            }
                        }
                    }
                }
            }
            return CallNextHookEx(_hHook, nCode, wParam, lParam);
        }

        private static IntPtr MakeHotKeyLParam(Keys key, KeyModifiers modifiers)
        {
            // 与 RegisterHotKey 的 WM_HOTKEY lParam 布局一致：低 16 位修饰键，高 16 位按键
            return (IntPtr)(unchecked((int)((uint)modifiers | ((uint)key << 16))));
        }

        /// <summary>
        /// 注入一个无功能按键（vk 0xE8，未分配）。系统只要看到 Win 按下期间有其它按键，
        /// 松开 Win 时就不会弹出开始菜单。
        /// </summary>
        private static void InjectDummyKey()
        {
            INPUT[] inputs = new INPUT[]
            {
                MakeKeyInput(VK_DUMMY, 0),
                MakeKeyInput(VK_DUMMY, KEYEVENTF_KEYUP),
            };
            SendInput((uint)inputs.Length, inputs, Marshal.SizeOf(typeof(INPUT)));
        }

        private static INPUT MakeKeyInput(uint vk, uint flags)
        {
            INPUT input = new INPUT();
            input.type = INPUT_KEYBOARD;
            input.u.ki.wVk = (ushort)vk;
            input.u.ki.dwFlags = flags;
            input.u.ki.dwExtraInfo = (IntPtr)DummyKeyExtraInfo;
            return input;
        }

        // ---- Win32 ----

        private const int WH_KEYBOARD_LL = 13;
        private const int WM_KEYDOWN = 0x0100;
        private const int WM_KEYUP = 0x0101;
        private const int WM_SYSKEYDOWN = 0x0104;
        private const int WM_SYSKEYUP = 0x0105;
        private const int WM_HOTKEY = 0x0312;
        private const uint LLKHF_INJECTED = 0x00000010;
        private const uint KEYEVENTF_KEYUP = 0x0002;
        private const uint INPUT_KEYBOARD = 1;
        private const uint VK_LWIN = 0x5B;
        private const uint VK_RWIN = 0x5C;
        private const uint VK_LSHIFT = 0xA0;
        private const uint VK_RSHIFT = 0xA1;
        private const uint VK_LCONTROL = 0xA2;
        private const uint VK_RCONTROL = 0xA3;
        private const uint VK_LMENU = 0xA4;
        private const uint VK_RMENU = 0xA5;
        private const uint VK_DUMMY = 0xE8;
        private const uint DummyKeyExtraInfo = 0x5344574E; // "SDWN"，标记我们注入的按键

        [StructLayout(LayoutKind.Sequential)]
        private struct KBDLLHOOKSTRUCT
        {
            public uint vkCode;
            public uint scanCode;
            public uint flags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct INPUT
        {
            public uint type;
            public INPUTUNION u;
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct INPUTUNION
        {
            // MOUSEINPUT 是 union 中最大的成员，必须保留以保证 sizeof(INPUT) 正确，
            // 否则 SendInput 会因 cbSize 不匹配而失败
            [FieldOffset(0)] public MOUSEINPUT mi;
            [FieldOffset(0)] public KEYBDINPUT ki;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MOUSEINPUT
        {
            public int dx;
            public int dy;
            public uint mouseData;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct KEYBDINPUT
        {
            public ushort wVk;
            public ushort wScan;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string lpModuleName);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

        [DllImport("user32.dll")]
        private static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        // 必须保持委托引用，防止被 GC 回收
        private static readonly LowLevelKeyboardProc _hookProcDelegate = HookProc;
        private static IntPtr _hHook = IntPtr.Zero;

        private static volatile MessageWindow _wnd;
        private static volatile IntPtr _hwnd;
        private static ManualResetEvent _windowReadyEvent = new ManualResetEvent(false);
        static HotKeyManager()
        {
            Thread messageLoop = new Thread(delegate ()
            {
                Application.Run(new MessageWindow());
            });
            messageLoop.Name = "MessageLoopThread";
            messageLoop.IsBackground = true;
            messageLoop.Start();
        }

        private class MessageWindow : Form
        {
            public MessageWindow()
            {
                _wnd = this;
                _hwnd = this.Handle;
                _windowReadyEvent.Set();
            }

            protected override void OnHandleCreated(EventArgs e)
            {
                base.OnHandleCreated(e);
                // 在消息循环线程上安装全局低级键盘钩子
                _hHook = SetWindowsHookEx(WH_KEYBOARD_LL, _hookProcDelegate, GetModuleHandle(null), 0);
                if (_hHook == IntPtr.Zero)
                {
                    throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(),
                        "SetWindowsHookEx 失败，热键不可用");
                }
            }

            protected override void OnHandleDestroyed(EventArgs e)
            {
                if (_hHook != IntPtr.Zero)
                {
                    UnhookWindowsHookEx(_hHook);
                    _hHook = IntPtr.Zero;
                }
                base.OnHandleDestroyed(e);
            }

            protected override void WndProc(ref Message m)
            {
                if (m.Msg == WM_HOTKEY)
                {
                    HotKeyEventArgs e = new HotKeyEventArgs(m.LParam);
                    HotKeyManager.OnHotKeyPressed(e);
                }

                base.WndProc(ref m);
            }

            protected override void SetVisibleCore(bool value)
            {
                // Ensure the window never becomes visible
                base.SetVisibleCore(false);
            }
        }

        private static int _id = 0;
    }


    public class HotKeyEventArgs : EventArgs
    {
        public readonly Keys Key;
        public readonly KeyModifiers Modifiers;

        public HotKeyEventArgs(Keys key, KeyModifiers modifiers)
        {
            this.Key = key;
            this.Modifiers = modifiers;
        }

        public HotKeyEventArgs(IntPtr hotKeyParam)
        {
            uint param = (uint)hotKeyParam.ToInt64();
            Key = (Keys)((param & 0xffff0000) >> 16);
            Modifiers = (KeyModifiers)(param & 0x0000ffff);
        }
    }

    [Flags]
    public enum KeyModifiers
    {
        Alt = 1,
        Control = 2,
        Shift = 4,
        Windows = 8,
        NoRepeat = 0x4000
    }
}
