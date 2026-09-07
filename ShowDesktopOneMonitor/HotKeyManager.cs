using System;
using System.Windows.Forms;
using System.Runtime.InteropServices;
using System.Threading;

namespace ShowDesktopOneMonitor
{
    public static class HotKeyManager
    {
        public static event EventHandler<HotKeyEventArgs> HotKeyPressed;

        public static int RegisterHotKey(Keys key, KeyModifiers modifiers)
        {
            _windowReadyEvent.WaitOne();
            int id = System.Threading.Interlocked.Increment(ref _id);
            _wnd.Invoke(new RegisterHotKeyDelegate(RegisterHotKeyInternal), _hwnd, id, (uint)modifiers, (uint)key);
            return id;
        }

        public static void UnregisterHotKey(int id)
        {
            _wnd.Invoke(new UnRegisterHotKeyDelegate(UnRegisterHotKeyInternal), _hwnd, id);
        }

        delegate void RegisterHotKeyDelegate(IntPtr hwnd, int id, uint modifiers, uint key);
        delegate void UnRegisterHotKeyDelegate(IntPtr hwnd, int id);

        private static void RegisterHotKeyInternal(IntPtr hwnd, int id, uint modifiers, uint key)
        {
            RegisterHotKey(hwnd, id, modifiers, key);
        }

        private static void UnRegisterHotKeyInternal(IntPtr hwnd, int id)
        {
            UnregisterHotKey(_hwnd, id);
        }

        private static void OnHotKeyPressed(HotKeyEventArgs e)
        {
            if ((e.Modifiers & KeyModifiers.Windows) != 0)
            {
                // 延迟修复被卡住的 Win 键：
                // 用 MOD_WIN 注册的热键触发后，如果处理过程中前台窗口被切换，
                // Win 键的 key-up 事件可能丢失，系统会认为 Win 仍处于按下状态
                // （之后按 D 会触发 Win+D）。延迟片刻后检查物理按键状态，
                // 若已松开则补发 key-up 复位。
                _stuckWinFixTimer.Change(StuckWinFixDelayMs, Timeout.Infinite);
            }

            if (HotKeyManager.HotKeyPressed != null)
            {
                HotKeyManager.HotKeyPressed(null, e);
            }
        }

        private const int StuckWinFixDelayMs = 300;
        private const byte VK_LWIN = 0x5B;
        private const byte VK_RWIN = 0x5C;
        private const int KEYEVENTF_KEYUP = 0x0002;
        private static readonly System.Threading.Timer _stuckWinFixTimer = new System.Threading.Timer(StuckWinFixTimerCallback);

        private static void StuckWinFixTimerCallback(object state)
        {
            // 物理上仍按住则不干预，等真实的 key-up
            if ((GetAsyncKeyState(VK_LWIN) & 0x8000) == 0)
            {
                keybd_event(VK_LWIN, 0, KEYEVENTF_KEYUP, 0);
            }
            if ((GetAsyncKeyState(VK_RWIN) & 0x8000) == 0)
            {
                keybd_event(VK_RWIN, 0, KEYEVENTF_KEYUP, 0);
            }
        }

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(byte vKey);

        [DllImport("user32.dll")]
        private static extern void keybd_event(byte bVk, byte bScan, int dwFlags, int dwExtraInfo);

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

            private const int WM_HOTKEY = 0x312;
        }

        [DllImport("user32", SetLastError = true)]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32", SetLastError = true)]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

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
