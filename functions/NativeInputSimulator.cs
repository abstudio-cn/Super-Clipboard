using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace superClipboard
{
    /// <summary>
    /// 基于 Win32 SendInput API 的底层输入模拟器。
    /// SendInput 将输入事件直接注入系统输入流，比 keybd_event/SendKeys 更底层，
    /// 能在 VM 捕获键鼠、UIPI 隔离等场景下正常工作。
    /// 
    /// 修复记录 (2026-05-11): 为 SendInput 调用添加返回值检查和重试机制，
    /// 解决长文本模拟输入时因 SendInput 静默失败导致的截断问题。
    /// </summary>
    public static class NativeInputSimulator
    {
        #region Win32 API 声明

        private const uint INPUT_KEYBOARD = 1;
        private const uint KEYEVENTF_KEYDOWN = 0x0000;
        private const uint KEYEVENTF_KEYUP = 0x0002;
        private const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
        private const uint KEYEVENTF_UNICODE = 0x0004;

        // MapVirtualKey map types
        private const uint MAPVK_VK_TO_VSC = 0;
        private const uint MAPVK_VSC_TO_VK = 1;
        private const uint MAPVK_VK_TO_CHAR = 2;

        // 重试参数
        private const int MAX_RETRIES = 3;
        private const int RETRY_DELAY_MS = 10;

        [StructLayout(LayoutKind.Sequential)]
        private struct INPUT
        {
            public uint type;
            public INPUTUNION u;
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct INPUTUNION
        {
            [FieldOffset(0)] public KEYBDINPUT ki;
            [FieldOffset(0)] public MOUSEINPUT mi;
            [FieldOffset(0)] public HARDWAREINPUT hi;
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

        // MOUSEINPUT 和 HARDWAREINPUT 仅用于确保 INPUT 联合体大小与
        // Windows SDK 的 sizeof(INPUT) 一致（x86=28, x64=40）。
        // 修复 Win32Err=87 (ERROR_INVALID_PARAMETER) — cbSize 不匹配。
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
        private struct HARDWAREINPUT
        {
            public uint uMsg;
            public ushort wParamL;
            public ushort wParamH;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern short VkKeyScanEx(char ch, IntPtr dwhkl);

        [DllImport("user32.dll")]
        private static extern uint MapVirtualKey(uint uCode, uint uMapType);

        [DllImport("user32.dll")]
        private static extern IntPtr GetKeyboardLayout(uint idThread);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr lpdwProcessId);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        #endregion

        #region 公共方法

        /// <summary>
        /// 发送按键按下事件
        /// </summary>
        public static void KeyDown(ushort virtualKey, bool extended = false)
        {
            SendKeyEvent(virtualKey, extended ? KEYEVENTF_EXTENDEDKEY : KEYEVENTF_KEYDOWN);
        }

        /// <summary>
        /// 发送按键释放事件
        /// </summary>
        public static void KeyUp(ushort virtualKey, bool extended = false)
        {
            SendKeyEvent(virtualKey, KEYEVENTF_KEYUP | (extended ? KEYEVENTF_EXTENDEDKEY : 0));
        }

        /// <summary>
        /// 模拟完整按键（按下 + 释放）
        /// </summary>
        public static void KeyPress(ushort virtualKey, bool extended = false, int delayMs = 10)
        {
            KeyDown(virtualKey, extended);
            if (delayMs > 0) Thread.Sleep(delayMs);
            KeyUp(virtualKey, extended);
        }

        /// <summary>
        /// 使用 SendInput 键入文本。
        /// 通过 VkKeyScanEx 将字符映射为虚拟键码 + 修饰键组合，
        /// 无法映射的字符回退到 Unicode 包方式注入。
        /// </summary>
        /// <param name="text">要键入的文本</param>
        /// <param name="delayMs">每个字符之间的延迟（毫秒）</param>
        /// <param name="cancellationToken">用于中途停止模拟输入的取消令牌</param>
        public static void TypeText(string text, int delayMs = 10, CancellationToken cancellationToken = default)
        {
            // 获取当前前台窗口所在线程的键盘布局
            IntPtr foregroundWindow = GetForegroundWindow();
            uint threadId = foregroundWindow != IntPtr.Zero
                ? GetWindowThreadProcessId(foregroundWindow, IntPtr.Zero)
                : 0;
            IntPtr keyboardLayout = GetKeyboardLayout(threadId);

            Logger.Info($"TypeText 开始: 长度={text.Length}, 前台窗口=0x{foregroundWindow.ToInt64():X}, 线程ID={threadId}");

            foreach (char c in text)
            {
                // 检查是否需要取消模拟输入
                if (cancellationToken.IsCancellationRequested)
                {
                    Logger.Info("TypeText 被取消");
                    return;
                }

                // 处理换行
                if (c == '\n')
                {
                    KeyPress(0x0D); // VK_RETURN
                    Thread.Sleep(delayMs);
                    continue;
                }

                if (c == '\r')
                    continue; // 跳过独立的 \r

                if (c == '\t')
                {
                    KeyPress(0x09); // VK_TAB
                    Thread.Sleep(delayMs);
                    continue;
                }

                if (c == '\b')
                {
                    KeyPress(0x08, true); // VK_BACK (extended)
                    Thread.Sleep(delayMs);
                    continue;
                }

                // 尝试使用 VkKeyScanEx 将字符转换为虚拟键码
                short vkScanResult = VkKeyScanEx(c, keyboardLayout);
                byte vkCode = (byte)(vkScanResult & 0xFF);
                byte shiftState = (byte)((vkScanResult >> 8) & 0xFF);

                // 如果 VkKeyScanEx 无法映射，回退到 Unicode 包方式
                if (vkScanResult == -1 || vkCode == 0xFF)
                {
                    SendUnicodeChar(c);
                    Thread.Sleep(delayMs);
                    continue;
                }

                bool needShift = (shiftState & 1) != 0;
                bool needCtrl = (shiftState & 2) != 0;
                bool needAlt = (shiftState & 4) != 0;

                ushort scanCode = (ushort)MapVirtualKey(vkCode, MAPVK_VK_TO_VSC);
                bool isExtended = IsExtendedKey(vkCode);

                // 按下必要的修饰键
                if (needShift) KeyDown(0x10); // VK_SHIFT
                if (needCtrl) KeyDown(0x11);  // VK_CONTROL
                if (needAlt) KeyDown(0x12);   // VK_MENU

                Thread.Sleep(5);

                // 发送按键 (按下)
                SendKeyEvent(vkCode, isExtended ? KEYEVENTF_EXTENDEDKEY : KEYEVENTF_KEYDOWN);
                Thread.Sleep(5);
                // 发送按键 (释放)
                SendKeyEvent(vkCode, KEYEVENTF_KEYUP | (isExtended ? KEYEVENTF_EXTENDEDKEY : 0));

                // 释放修饰键（逆序释放）
                if (needAlt) KeyUp(0x12);
                if (needCtrl) KeyUp(0x11);
                if (needShift) KeyUp(0x10);

                Thread.Sleep(delayMs);
            }
        }

        #endregion

        #region 私有方法

        /// <summary>
        /// 发送单个键盘事件到系统输入流（带返回值检查和重试）。
        /// 
        /// 【修复关键】: 原始代码未检查 SendInput 返回值，
        /// 当 SendInput 因 UIPI/队列满等原因返回 0 时，字符被静默丢弃，
        /// 长文本因此出现"中间截断"的问题。
        /// 现在失败时会重试最多 MAX_RETRIES 次。
        /// </summary>
        private static void SendKeyEvent(ushort virtualKey, uint flags)
        {
            uint scanCode = MapVirtualKey(virtualKey, MAPVK_VK_TO_VSC);

            var inputs = new INPUT[1];
            inputs[0] = new INPUT
            {
                type = INPUT_KEYBOARD,
                u = new INPUTUNION
                {
                    ki = new KEYBDINPUT
                    {
                        wVk = virtualKey,
                        wScan = (ushort)scanCode,
                        dwFlags = flags,
                        time = 0,
                        dwExtraInfo = IntPtr.Zero
                    }
                }
            };

            int cbSize = Marshal.SizeOf(typeof(INPUT));

            // 尝试发送，失败则重试
            for (int i = 0; i < MAX_RETRIES; i++)
            {
                uint result = SendInput(1, inputs, cbSize);
                if (result == 1)
                    return; // 成功

                if (i < MAX_RETRIES - 1)
                    Thread.Sleep(RETRY_DELAY_MS);
            }

            // 3 次全部失败才记录日志（含 Win32 错误码）
            int lastErr = Marshal.GetLastWin32Error();
            Logger.Error($"SendKeyEvent 失败: VK=0x{virtualKey:X2}, flags=0x{flags:X4}, Win32Err={lastErr}");
        }

        /// <summary>
        /// 使用 KEYEVENTF_UNICODE 标志通过 SendInput 注入 Unicode 字符（带返回值检查和重试）。
        /// </summary>
        private static void SendUnicodeChar(char c)
        {
            var inputs = new INPUT[2];

            // Key down（Unicode 包）
            inputs[0] = new INPUT
            {
                type = INPUT_KEYBOARD,
                u = new INPUTUNION
                {
                    ki = new KEYBDINPUT
                    {
                        wVk = 0,
                        wScan = c,
                        dwFlags = KEYEVENTF_UNICODE,
                        time = 0,
                        dwExtraInfo = IntPtr.Zero
                    }
                }
            };

            // Key up（Unicode 包）
            inputs[1] = new INPUT
            {
                type = INPUT_KEYBOARD,
                u = new INPUTUNION
                {
                    ki = new KEYBDINPUT
                    {
                        wVk = 0,
                        wScan = c,
                        dwFlags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP,
                        time = 0,
                        dwExtraInfo = IntPtr.Zero
                    }
                }
            };

            int cbSize = Marshal.SizeOf(typeof(INPUT));

            for (int i = 0; i < MAX_RETRIES; i++)
            {
                uint result = SendInput(2, inputs, cbSize);
                if (result == 2)
                    return; // 成功

                if (i < MAX_RETRIES - 1)
                    Thread.Sleep(RETRY_DELAY_MS);
            }

            int lastErr = Marshal.GetLastWin32Error();
            Logger.Error($"SendUnicodeChar 失败: char='{c}' (U+{(int)c:X4}), Win32Err={lastErr}");
        }

        /// <summary>
        /// 判断虚拟键码是否属于"扩展键"（需要 KEYEVENTF_EXTENDEDKEY 标志）
        /// </summary>
        private static bool IsExtendedKey(ushort vkCode)
        {
            return vkCode == 0x2E || // VK_DELETE
                   vkCode == 0x21 || // VK_PRIOR (Page Up)
                   vkCode == 0x22 || // VK_NEXT (Page Down)
                   vkCode == 0x23 || // VK_END
                   vkCode == 0x24 || // VK_HOME
                   vkCode == 0x25 || // VK_LEFT
                   vkCode == 0x26 || // VK_UP
                   vkCode == 0x27 || // VK_RIGHT
                   vkCode == 0x28 || // VK_DOWN
                   vkCode == 0x2D || // VK_INSERT
                   vkCode == 0xA2 || // VK_RCONTROL
                   vkCode == 0xA3 || // VK_RMENU
                   vkCode == 0xA5;   // VK_RMENU (AltGr)
        }

        #endregion
    }
}
