using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;



namespace superClipboard
{
    public enum PasteMode
    {
        Normal,
        Keystrokes
    }

    public class ClipboardCore : IDisposable
    {

        
        private Thread _monitorThread;
        private ClipboardData? _currentData;
        private readonly KeyboardHookManager _keyboardHook;
        private readonly KeyboardHookManager _hookManager;
        private readonly SettingsManager _settings;
        private readonly HashSet<Key> _pressedKeys = new();
        private bool _pendingKeystrokesPaste = false;
        private bool _ctrlPressed = false;
        private bool _altPressed = false;
        private bool _shiftPressed = false;
        private bool _winPressed = false;


        private const uint KEYEVENTF_EXTENDEDKEY = 0x0001; // 键盘按下事件
        private const uint KEYEVENTF_KEYUP = 0x0002; // 键盘释放事件
        private const int VK_DELETE = 0x2E; // Delete键的虚拟键码

        public event Action<ClipboardData>? ClipboardChanged;

        public ClipboardCore(SettingsManager settings)
        {
            _settings = settings;
            _keyboardHook = new KeyboardHookManager();
            SetupKeyboardHooks();
            StartMonitoring(); // 假设此方法启动剪贴板监控
        }


        private void SetupKeyboardHooks()
        {
            _keyboardHook.OnKeyDown += OnKeyDown;
            _keyboardHook.OnKeyUp += OnKeyUp;
        }

        private void OnKeyDown(Key key)
        {
            // 更新修饰键状态
            if (key == Key.LeftCtrl || key == Key.RightCtrl)
                _ctrlPressed = true;
            if (key == Key.LeftAlt || key == Key.RightAlt)
                _altPressed = true;
            if (key == Key.LeftShift || key == Key.RightShift)
                _shiftPressed = true;
            if (key == Key.LWin || key == Key.RWin)
                _winPressed = true;

            ModifierKeys currentModifiers = ModifierKeys.None;
            if (_ctrlPressed) currentModifiers |= ModifierKeys.Control;
            if (_altPressed) currentModifiers |= ModifierKeys.Alt;
            if (_shiftPressed) currentModifiers |= ModifierKeys.Shift;
            if (_winPressed) currentModifiers |= ModifierKeys.Windows;

            // 检查是否匹配普通粘贴快捷键
            if (currentModifiers == _settings.NormalPasteHotkey.Modifiers && key == _settings.NormalPasteHotkey.Key)
            {
                ProcessPaste(PasteMode.Normal);
            }
            // 检查是否匹配模拟按键粘贴快捷键
            else if (currentModifiers == _settings.KeystrokesPasteHotkey.Modifiers && key == _settings.KeystrokesPasteHotkey.Key)
            {
                ProcessPaste(PasteMode.Keystrokes);
            }
        }

        private void OnKeyUp(Key key)
        {
            if (key == Key.LeftCtrl || key == Key.RightCtrl)
                _ctrlPressed = false;
            if (key == Key.LeftAlt || key == Key.RightAlt)
                _altPressed = false;
            if (key == Key.LeftShift || key == Key.RightShift)
                _shiftPressed = false;
            if (key == Key.LWin || key == Key.RWin)
                _winPressed = false;
        }

        private void ProcessPaste(PasteMode mode)
        {
            if (_currentData == null) return;

            if (_currentData.Type == DataType.Text && mode == PasteMode.Keystrokes)
            {
                PasteAsKeystrokes(_currentData.TextContent);
            }
            else
            {
                // 普通模式或非文本内容，恢复原始剪贴板内容
                RestoreClipboardContent();
            }
        }

        private void PasteAsKeystrokes(string text)
        {
            Task.Run(async () =>
            {
                // 等待所有修饰键释放（避免干扰）
                while (_ctrlPressed || _altPressed || _shiftPressed || _winPressed)
                {
                    await Task.Delay(50);
                }

                // 可选延迟，确保目标窗口准备就绪
                await Task.Delay(1000);

                // 模拟按下 Delete 键（清空可能预选的内容）
                KeyboardHookManager.keybd_event(VK_DELETE, 0x45, KEYEVENTF_EXTENDEDKEY, 0);
                await Task.Delay(50);
                KeyboardHookManager.keybd_event(VK_DELETE, 0x45, KEYEVENTF_KEYUP, 0);
                await Task.Delay(100);
                // 转义特殊字符并发送文本
                string[] escapedText = EscapeSendKeysString(text);
                for (long i = 0; i < escapedText.Length; i++)
                {
                    SendKeys.SendWait(escapedText[i].ToString());
                    Thread.Sleep(50);
                }
            });
        }

        private static string[] EscapeSendKeysString(string text)
        {
            // SendKeys 特殊字符: + ^ % ~ ( ) [ ] { }
            // 需要将每个特殊字符用花括号包围，例如 '(' 变为 "{(}"
            var aa = new System.Text.StringBuilder();
            foreach (char c in text)
            {
                switch (c)
                {
                    case '+':
                    case '^':
                    case '%':
                    case '~':
                    case '(':
                    case ')':
                    case '[':
                    case ']':
                    case '{':
                    case '}':
                        aa.Append('{').Append(c).Append('}');
                        break;
                    default:
                        aa.Append(c);
                        break;
                }
            }
            string textWithEscapes = aa.ToString();
            string[] split = SplitStringByLength(textWithEscapes, 100); // 100字符串切割以实现动态监控
            return split;
        }

        public static string[] SplitStringByLength(string input, int chunkSize)
        {
            if (string.IsNullOrEmpty(input) || chunkSize <= 0)
                return new string[] { input }; // 如果输入为空，返回仅包含原字符串的数组；也可根据需求改为返回空数组

            int length = input.Length;
            int numChunks = (int)Math.Ceiling((double)length / chunkSize);
            string[] chunks = new string[numChunks];

            for (int i = 0; i < numChunks; i++)
            {
                int start = i * chunkSize;
                int remaining = length - start;
                int currentChunkSize = Math.Min(chunkSize, remaining);
                chunks[i] = input.Substring(start, currentChunkSize);
            }

            return chunks;
        }

        public void StartMonitoring()
        {
            if (GlobalData._isMonitoring) return;

            GlobalData._isMonitoring = true;
            System.Windows.Forms.Clipboard.Clear();

            // 创建并启动 STA 线程
            _monitorThread = new Thread(MonitorClipboard)
            {
                Name = "ClipboardMonitor",
                IsBackground = true   // 设置为后台线程，程序退出时自动终止
            };
            // 使用 SetApartmentState 方法设置线程单元状态（必须在 Start 之前调用）
            _monitorThread.SetApartmentState(ApartmentState.STA);
            _monitorThread.Start();
        }

        private void MonitorClipboard()
        {
            string lastText = string.Empty;
            object lastData = null;

            while (GlobalData._isMonitoring)
            {
                try
                {
                    Thread.Sleep(100); // 适当降低频率以减少 CPU 占用

                    // 检查剪贴板文本
                    if (System.Windows.Forms.Clipboard.ContainsText())
                    {
                        string currentText = System.Windows.Forms.Clipboard.GetText();
                        if (currentText != lastText)
                        {
                            lastText = currentText;
                            _currentData = new ClipboardData
                            {
                                Type = DataType.Text,
                                TextContent = currentText,
                                Timestamp = DateTime.Now
                            };
                            ClipboardChanged?.Invoke(_currentData);
                            continue; // 避免重复检查其他格式
                        }
                    }

                    // 检查剪贴板图像
                    if (System.Windows.Forms.Clipboard.ContainsImage())
                    {
                        var image = System.Windows.Forms.Clipboard.GetImage();
                        if (image != lastData)
                        {
                            lastData = image;
                            _currentData = new ClipboardData
                            {
                                Type = DataType.Image,
                                ImageContent = ConvertToBitmapImage(image),
                                Timestamp = DateTime.Now
                            };
                            ClipboardChanged?.Invoke(_currentData);
                            continue;
                        }
                    }

                    // 检查剪贴板文件列表
                    if (System.Windows.Forms.Clipboard.ContainsFileDropList())
                    {
                        var files = System.Windows.Forms.Clipboard.GetFileDropList();
                        var fileList = new List<string>();
                        foreach (string file in files)
                        {
                            fileList.Add(file);
                        }

                        _currentData = new ClipboardData
                        {
                            Type = DataType.Files,
                            FilePaths = fileList,
                            Timestamp = DateTime.Now
                        };
                        ClipboardChanged?.Invoke(_currentData);
                        continue;
                    }

                    // 可选：处理其他格式
                }
                catch (Exception ex)
                {
                    // 输出错误信息，便于调试（在 Output 窗口可见）
                    Debug.WriteLine($"剪贴板监控异常: {ex.Message}");
                    // 如果发生严重错误，可以选择停止监控
                    // _isMonitoring = false;
                }
            }
        }

        private void RestoreClipboardContent()
        {
            if (_currentData == null) return;

            try
            {
                switch (_currentData.Type)
                {
                    case DataType.Text:
                        System.Windows.Forms.Clipboard.SetText(_currentData.TextContent);
                        break;
                    case DataType.Image:
                        if (_currentData.ImageContent != null)
                        {
                            var bitmap = ConvertToSystemDrawingBitmap(_currentData.ImageContent);
                            System.Windows.Forms.Clipboard.SetImage(bitmap);
                        }
                        break;
                    case DataType.Files:
                        var collection = new System.Collections.Specialized.StringCollection();
                        collection.AddRange([.. _currentData.FilePaths]);
                        System.Windows.Forms.Clipboard.SetFileDropList(collection);
                        break;
                }
            }
            catch { }
        }

        private BitmapImage ConvertToBitmapImage(System.Drawing.Image image)
        {
            var ms = new MemoryStream();
            image.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
            ms.Position = 0;

            var bitmapImage = new BitmapImage();
            bitmapImage.BeginInit();
            bitmapImage.CacheOption = BitmapCacheOption.OnLoad;
            bitmapImage.StreamSource = ms;
            bitmapImage.EndInit();
            bitmapImage.Freeze();

            return bitmapImage;
        }

        private System.Drawing.Bitmap ConvertToSystemDrawingBitmap(BitmapImage bitmapImage)
        {
            var outStream = new MemoryStream();
            BitmapEncoder enc = new BmpBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(bitmapImage));
            enc.Save(outStream);
            return new System.Drawing.Bitmap(outStream);
        }

        public void StopMonitoring()
        {
            GlobalData._isMonitoring = false;
            // 等待线程结束（可选）
            if (_monitorThread?.IsAlive == true)
            {
                _monitorThread.Join(1000);
            }
        }

        public void Dispose()
        {
            StopMonitoring();
            _keyboardHook?.Dispose();
        }
    }



}