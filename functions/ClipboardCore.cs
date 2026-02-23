using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
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
using System.Runtime.InteropServices;


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
        private bool _ctrlPressed = false;
        private bool _vPressed = false;
        private const uint KEYEVENTF_EXTENDEDKEY = 0x0001; // 键盘按下事件
        private const uint KEYEVENTF_KEYUP = 0x0002; // 键盘释放事件
        private const int VK_DELETE = 0x2E; // Delete键的虚拟键码
        private bool _altPressed = false;

        public event Action<ClipboardData>? ClipboardChanged;

        public ClipboardCore()
        {
            _keyboardHook = new KeyboardHookManager();
            SetupKeyboardHooks();
            StartMonitoring();
        }

        private void SetupKeyboardHooks()
        {
            _keyboardHook.OnKeyDown += OnKeyDown;
            _keyboardHook.OnKeyUp += OnKeyUp;
        }

        private void OnKeyDown(Key key)
        {
            Console.WriteLine("ok");
            if (key == Key.LeftCtrl || key == Key.RightCtrl)
                _ctrlPressed = true;
            if (key == Key.LeftAlt || key == Key.RightAlt)
                _altPressed = true;
            if (key == Key.V)
            {
                _vPressed = true;
            }   


            if (_ctrlPressed && key == Key.V)
            {
                if (_altPressed)
                {
                    // Ctrl+Alt+V - 模拟键盘输入模式
                    while(true)
                    {
                        // 等待1秒钟，确保剪贴板内容已准备好
                        Task.Delay(200);  
                        if(_altPressed == false && _ctrlPressed == false && _vPressed == false) break;
                    }
                    ProcessPaste(PasteMode.Keystrokes);
                    Console.WriteLine(key + " pressed - Paste as Keystrokes");
                }
                else
                {
                    // Ctrl+V - 普通粘贴模式
                    ProcessPaste(PasteMode.Normal);
                    Console.WriteLine(key + " pressed - Paste as Normal");
                }
            }
        }

        private void OnKeyUp(Key key)
        {
            if (key == Key.LeftCtrl || key == Key.RightCtrl)
                _ctrlPressed = false;
            if (key == Key.LeftAlt || key == Key.RightAlt)
                _altPressed = false;
            if (key == Key.V)
            {
                _vPressed = false;
            }
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
        Task.Run(() =>
            {
                Thread.Sleep(1000);
                KeyboardHookManager.keybd_event(VK_DELETE, 0x45, KEYEVENTF_EXTENDEDKEY | 0, 0); // 按下Delete键
                Thread.Sleep(200);
                KeyboardHookManager.keybd_event(VK_DELETE, 0x45, KEYEVENTF_KEYUP | 0, 0);
                Thread.Sleep(200); // 等待窗口切换
                SendKeys.SendWait(text);
            });
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