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
using System.Security.Cryptography;



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
        private CancellationTokenSource? _simulationCts;

        /// <summary>
        /// 获取当前剪贴板数据（供 UI 页面初始化时读取）
        /// </summary>
        public ClipboardData? CurrentData => _currentData;



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
            // 检查是否匹配停止模拟输入快捷键
            else if (_simulationCts != null &&
                     currentModifiers == _settings.StopSimulationHotkey.Modifiers &&
                     key == _settings.StopSimulationHotkey.Key)
            {
                StopSimulation();
            }
        }

        /// <summary>
        /// 停止当前正在进行的模拟输入
        /// </summary>
        private void StopSimulation()
        {
            Logger.Info("用户触发停止模拟输入");
            _simulationCts?.Cancel();
            _simulationCts?.Dispose();
            _simulationCts = null;
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
            Logger.Info($"ProcessPaste 触发, mode={mode}, _currentData={(_currentData != null ? _currentData.Type.ToString() : "null")}");

            if (_currentData == null)
            {
                Logger.Warn("ProcessPaste 中止: _currentData 为 null");
                return;
            }

            if (_currentData.Type == DataType.Text && mode == PasteMode.Keystrokes)
            {
                Logger.Info($"进入模拟输入路径, 文本长度={_currentData.TextContent?.Length ?? 0}");
                PasteAsKeystrokes(_currentData.TextContent);
            }
            else
            {
                Logger.Info($"进入普通粘贴路径 (type={_currentData.Type}, mode={mode})");
                // 普通模式或非文本内容，恢复原始剪贴板内容
                RestoreClipboardContent();
            }
        }

        private void PasteAsKeystrokes(string text)
        {
            // 取消之前可能正在进行的模拟
            _simulationCts?.Cancel();
            _simulationCts?.Dispose();

            // 创建新的取消令牌
            var cts = new CancellationTokenSource();
            _simulationCts = cts;

            Task.Run(async () =>
            {
                try
                {
                    // 等待所有修饰键释放（避免干扰）
                    while (_ctrlPressed || _altPressed || _shiftPressed || _winPressed)
                    {
                        await Task.Delay(50);
                    }

                    // 可选延迟，确保目标窗口准备就绪
                    await Task.Delay(500);

                    // 使用 SendInput 模拟 Delete 键（清空可能预选的内容）
                    NativeInputSimulator.KeyPress(0x2E, true); // VK_DELETE, extended key
                    await Task.Delay(100);

                    // 使用 SendInput 发送文本，传入取消令牌以支持中途停止
                    await Task.Run(() => NativeInputSimulator.TypeText(text, 20, cts.Token));
                }
                finally
                {
                    // 清理：仅当此 CTS 仍是当前活动的 CTS 时才清理
                    if (_simulationCts == cts)
                    {
                        _simulationCts = null;
                    }
                    cts.Dispose();
                }
            });
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
                            _currentData.Id = GenerateDataId(_currentData);
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
                            _currentData.Id = GenerateDataId(_currentData);
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
                        _currentData.Id = GenerateDataId(_currentData);
                        ClipboardChanged?.Invoke(_currentData);
                        continue;
                    }

                    // 可选：处理其他格式
                }
                catch (Exception ex)
                {
                    // 输出错误信息，便于调试（在 Output 窗口可见）
                    Logger.Error($"剪贴板监控异常: {ex.Message}");
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

        private string GenerateDataId(ClipboardData data)
        {
            // 使用时间戳和内容哈希生成唯一ID
            StringBuilder sb = new StringBuilder();
            sb.Append(data.Timestamp.ToString("yyyyMMddHHmmssffff"));
            
            // 根据类型添加内容哈希
            string contentHash = "";
            switch (data.Type)
            {
                case DataType.Text:
                    contentHash = ComputeHash(data.TextContent ?? "");
                    break;
                case DataType.Image:
                    // 图片使用时间戳作为部分标识
                    contentHash = ComputeHash(data.Timestamp.ToString("O"));
                    break;
                case DataType.Files:
                    contentHash = ComputeHash(string.Join("|", data.FilePaths ?? new List<string>()));
                    break;
                default:
                    contentHash = ComputeHash(data.Timestamp.ToString("O"));
                    break;
            }
            
            sb.Append("_");
            sb.Append(contentHash.Substring(0, Math.Min(8, contentHash.Length)));
            
            return sb.ToString();
        }
        
        private string ComputeHash(string input)
        {
            using (var sha256 = SHA256.Create())
            {
                byte[] hashBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(input));
                return Convert.ToBase64String(hashBytes).Replace("=", "").Replace("/", "_").Replace("+", "-");
            }
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