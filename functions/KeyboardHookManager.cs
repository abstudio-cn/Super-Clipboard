using System;
using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;

namespace superClipboard
{
    public class KeyboardHookManager : IDisposable
    {
        public event Action<Key> OnKeyDown;
        public event Action<Key> OnKeyUp;

        /// <summary>
        /// 按键拦截事件：在 OnKeyDown 之前触发。
        /// 参数 key = 按键，isInjected = 是否为程序注入的模拟按键（SendInput 等）。
        /// 返回 true 表示吞掉该按键（不再传递给系统/目标窗口）。
        /// </summary>
        public event Func<Key, bool, bool> OnKeyDownIntercept;

        private const int WH_KEYBOARD_LL = 13;
        private const int WM_KEYDOWN = 0x0100;
        private const int WM_KEYUP = 0x0101;
        private const int WM_SYSKEYDOWN = 0x0104;
        private const int WM_SYSKEYUP = 0x0105;
        private const int LLKHF_INJECTED = 0x10;

        private IntPtr _hookId = IntPtr.Zero;
        private LowLevelKeyboardProc _proc;

        public KeyboardHookManager()
        {
            _proc = HookCallback;
            _hookId = SetHook(_proc);
        }

        private IntPtr SetHook(LowLevelKeyboardProc proc)
        {
            var curProcess = System.Diagnostics.Process.GetCurrentProcess();
            var curModule = curProcess.MainModule;
            return SetWindowsHookEx(WH_KEYBOARD_LL, proc,
                GetModuleHandle(curModule.ModuleName), 0);
        }

        private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

        private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                // KBDLLHOOKSTRUCT: vkCode@0, scanCode@4, flags@8, time@12, dwExtraInfo@16
                int vkCode = Marshal.ReadInt32(lParam);
                int flags = Marshal.ReadInt32(lParam, 8);
                bool isInjected = (flags & LLKHF_INJECTED) != 0;
                Key key = KeyInterop.KeyFromVirtualKey(vkCode);

                if (wParam == (IntPtr)WM_KEYDOWN || wParam == (IntPtr)WM_SYSKEYDOWN)
                {
                    // 拦截判断：返回 true 时吞掉按键，阻止其传递到目标窗口
                    if (OnKeyDownIntercept?.Invoke(key, isInjected) == true)
                    {
                        return (IntPtr)1;
                    }

                    OnKeyDown?.Invoke(key);
                }
                else if (wParam == (IntPtr)WM_KEYUP || wParam == (IntPtr)WM_SYSKEYUP)
                {
                    OnKeyUp?.Invoke(key);
                }
            }

            return CallNextHookEx(_hookId, nCode, wParam, lParam);
        }

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook,
            LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode,
            IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string lpModuleName);

        [DllImport("user32.dll")]
        public static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, uint dwExtraInfo);

        public void Dispose()
        {
            if (_hookId != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_hookId);
                _hookId = IntPtr.Zero;
            }
        }
    }
}