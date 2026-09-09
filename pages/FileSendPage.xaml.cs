using BinaryToTextEncoding;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace superClipboard
{
    public partial class FileSendPage : UserControl
    {
        private string? _selectedFilePath;
        private long _fileSize;
        private CancellationTokenSource? _cancellationTokenSource;
        private bool _isSending = false;
        private bool _isPaused = false;
        private int _totalChunks = 0;
        private int _sentChunks = 0;
        private long _totalCharacters = 0;

        /// <summary>分割块提示符：传输头结束标记，同时作为各数据块之间的分隔符</summary>
        private const string TransferMarker = "<!@@@@@@@!>";

        /// <summary>传输头中"分割块总数"字段的固定宽度（不足前补0）</summary>
        private const int HeaderChunkFieldLength = 20;

        /// <summary>传输头中"字符总数"字段的固定宽度（不足前补0）</summary>
        private const int HeaderCharFieldLength = 80;

        /// <summary>
        /// 文件名标识前缀：所有字符传输完成时在末尾附带 "<@@@filename@@@>文件名"，
        /// 供接收端还原原文件名（文件名内容为 UTF-8 + Base64 编码，保证可经模拟键盘传输）。
        /// </summary>
        private const string FileNameTagPrefix = "<@@@filename@@@>";
        private DispatcherTimer? _progressTimer;
        private readonly KeyboardHookManager _keyboardHook;
        private readonly object _lockObject = new();
        private LocalizationService _loc = null!;

        public FileSendPage()
        {
            InitializeComponent();
            _keyboardHook = new KeyboardHookManager();
            _keyboardHook.OnKeyDown += OnKeyDown;
            _loc = LocalizationService.Instance;
            ApplyLocalization();
        }

        private void ApplyLocalization()
        {
            PageTitle.Text = _loc["filesend.title"];
            LblSelectFile.Text = _loc["filesend.select_file_prompt"];
            BrowseButton.Content = _loc["filesend.browse_file"];
            FileInfoText.Text = _loc["filesend.no_file"];
            LblSendSettings.Text = _loc["filesend.send_settings"];
            CompressCheckBox.Content = _loc["filesend.compress"];
            LblChunkSize.Text = _loc["filesend.chunk_size"];
            LblChunkUnit.Text = _loc["filesend.chunk_unit"];
            ShowNotificationsCheckBox.Content = _loc["filesend.notifications"];
            LblProgress.Text = _loc["filesend.send_progress"];
            LblStatus.Text = _loc["filesend.status_label"];
            StatusText.Text = _loc["filesend.status_ready"];
            StartSendButton.Content = _loc["filesend.start_send"];
            PauseButton.Content = _loc["filesend.pause"];
            CancelButton.Content = _loc["filesend.cancel"];
            SendNoClientButton.Content = _loc["filesend.send_no_client"];
        }

        private void OnKeyDown(Key key)
        {
            // ESC 取消发送（倒计时期间也可取消）
            if (key == Key.Escape && _cancellationTokenSource != null)
            {
                Dispatcher.Invoke(() =>
                {
                    CancelSend();
                });
            }
        }

        private void BrowseButton_Click(object sender, RoutedEventArgs e)
        {
            var openFileDialog = new OpenFileDialog();
            openFileDialog.Filter = "所有文件 (*.*)|*.*";
            if (openFileDialog.ShowDialog() == true)
            {
                _selectedFilePath = openFileDialog.FileName;
                FilePathTextBox.Text = _selectedFilePath;
                SendNoClientButton.IsEnabled = true;

                try
                {
                    var fileInfo = new FileInfo(_selectedFilePath);
                    _fileSize = fileInfo.Length;
                    FileInfoText.Text = $"文件: {fileInfo.Name} | 大小: {FormatFileSize(_fileSize)} | 修改时间: {fileInfo.LastWriteTime:yyyy-MM-dd HH:mm:ss}";
                }
                catch (Exception ex)
                {
                    FileInfoText.Text = $"读取文件信息失败: {ex.Message}";
                }
            }
        }

        private async void StartSendButton_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_selectedFilePath) || !File.Exists(_selectedFilePath))
                return;

            if (_isSending)
                return;

            // 确认对话框
            var result = MessageBox.Show(
                "文件发送过程一旦开始将无法中断，是否继续？\n\n" +
                "发送过程中请勿操作键盘，否则可能导致传输失败。\n" +
                "系统将在10秒后开始传输。",
                "确认发送",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (result != MessageBoxResult.Yes)
                return;

            // 显示10秒倒计时通知
            if (ShowNotificationsCheckBox.IsChecked == true)
            {
                ShowNotification("文件发送", "10秒后开始传输文件...", System.Windows.Forms.ToolTipIcon.Info);
            }

            // 提前创建取消源：倒计时期间点击取消即可中止
            _isSending = true;
            _cancellationTokenSource = new CancellationTokenSource();
            var cancellationToken = _cancellationTokenSource.Token;

            // 等待10秒
            for (int i = 10; i > 0; i--)
            {
                if (cancellationToken.IsCancellationRequested)
                    return;

                StatusText.Text = string.Format(_loc["filesend.status_preparing"], i);
                await Task.Delay(1000);
            }

            // 开始发送
            await StartFileSend();
        }

        private async Task StartFileSend()
        {
            try
            {
                _isSending = true;
                _isPaused = false;
                _sentChunks = 0;
                _totalChunks = 0;
                _totalCharacters = 0;

                // 更新UI
                StartSendButton.IsEnabled = false;
                PauseButton.IsEnabled = true;
                CancelButton.IsEnabled = true;

                // 复用点击处理时创建的取消源（倒计时期间即已生效）
                _cancellationTokenSource ??= new CancellationTokenSource();
                var cancellationToken = _cancellationTokenSource.Token;

                // 读取文件
                byte[] fileData;
                try
                {
                    fileData = await File.ReadAllBytesAsync(_selectedFilePath!, cancellationToken);
                }
                catch (Exception)
                {
                    ResetUI();
                    return;
                }

                // 编码文件
                byte[] encodedData;
                if (CompressCheckBox.IsChecked == true)
                {
                    try
                    {
                        encodedData = BinaryTextCodec.Encode(fileData);
                    }
                    catch (Exception)
                    {
                        ResetUI();
                        return;
                    }
                }
                else
                {
                    // 非压缩模式：标准 Base64 编码
                    encodedData = Encoding.UTF8.GetBytes(Convert.ToBase64String(fileData));
                }

                string textToSend = Encoding.ASCII.GetString(encodedData);
                _totalCharacters = textToSend.Length;

                // 计算分割
                int chunkSize = int.TryParse(ChunkSizeNumberBox.Text, out int size) ? size : 1000;
                _totalChunks = (int)Math.Ceiling((double)textToSend.Length / chunkSize);

                // 构建传输头：[20位分割块总数][80位字符总数][分割块提示符]
                string transferHeader = BuildTransferHeader(_totalChunks, _totalCharacters);

                // 构建文件名标识：<@@@filename@@@> + 原文件名（UTF-8 + Base64 编码）
                string fileNameTag = FileNameTagPrefix + Convert.ToBase64String(
                    Encoding.UTF8.GetBytes(Path.GetFileName(_selectedFilePath ?? "file.bin")));

                // 更新进度显示
                Dispatcher.Invoke(() =>
                {
                    ChunksText.Text = $"分割块：0/{_totalChunks}";
                    CharactersText.Text = string.Format(_loc["filesend.characters_fmt"], _totalCharacters);
                    SendProgressBar.Maximum = _totalChunks;
                });

                if (ShowNotificationsCheckBox.IsChecked == true)
                {
                    ShowNotification("文件发送", $"开始传输文件，共{_totalChunks}个数据块", System.Windows.Forms.ToolTipIcon.Info);
                }

                // 开始发送
                StatusText.Text = _loc["filesend.status_sending"];

                // 启动进度定时器
                _progressTimer = new DispatcherTimer();
                _progressTimer.Interval = TimeSpan.FromMilliseconds(100);
                _progressTimer.Tick += ProgressTimer_Tick;
                _progressTimer.Start();

                // 发送传输头 + 数据块（每块后跟分割块提示符，末尾附带文件名标识）
                await SendTextWithProtocol(transferHeader, fileNameTag, textToSend, chunkSize, cancellationToken);

                if (!cancellationToken.IsCancellationRequested)
                {
                    StatusText.Text = _loc["filesend.status_complete"];
                    SendProgressBar.Value = _totalChunks;
                    ProgressPercentText.Text = "100%";

                    if (ShowNotificationsCheckBox.IsChecked == true)
                    {
                        ShowNotification("文件发送", "文件发送完成", System.Windows.Forms.ToolTipIcon.Info);
                    }

                }
            }
            catch (OperationCanceledException)
            {
                StatusText.Text = _loc["filesend.status_cancelled"];
                if (ShowNotificationsCheckBox.IsChecked == true)
                {
                    ShowNotification("文件发送", _loc["filesend.status_cancelled"], System.Windows.Forms.ToolTipIcon.Warning);
                }
            }
            catch (Exception ex)
            {
                StatusText.Text = _loc["filesend.status_failed"] + ex.Message;
                if (ShowNotificationsCheckBox.IsChecked == true)
                {
                    ShowNotification("文件发送", _loc["filesend.status_failed"] + ex.Message, System.Windows.Forms.ToolTipIcon.Error);
                }
            }
            finally
            {
                ResetUI();
            }
        }

        /// <summary>
        /// 构建传输头：前0-20位为分割块总数（不足20位前补0），
        /// 20-100位为字符总数（不足80位前补0），末尾为分割块提示符 "<!@@@@@@@!>"。
        /// </summary>
        private static string BuildTransferHeader(int totalChunks, long totalCharacters)
        {
            return totalChunks.ToString().PadLeft(HeaderChunkFieldLength, '0') +
                   totalCharacters.ToString().PadLeft(HeaderCharFieldLength, '0') +
                   TransferMarker;
        }

        /// <summary>
        /// 按新传输协议发送：先发送传输头，再按块发送数据，
        /// 每个数据块之后发送一个分割块提示符作为块分隔标记；
        /// 最后一个数据块之后、结束提示符之前附带文件名标识。
        /// </summary>
        private async Task SendTextWithProtocol(string header, string fileNameTag, string text, int chunkSize, CancellationToken cancellationToken)
        {
            // 先发送传输头（接收端识别提示符后会自动删除并转换为分割块计数）
            await SendChunk(header, cancellationToken);

            for (int i = 0; i < text.Length; i += chunkSize)
            {
                if (cancellationToken.IsCancellationRequested)
                    break;

                // 等待暂停
                while (_isPaused && !cancellationToken.IsCancellationRequested)
                {
                    await Task.Delay(100);
                }

                if (cancellationToken.IsCancellationRequested)
                    break;

                int length = Math.Min(chunkSize, text.Length - i);
                string chunk = text.Substring(i, length);

                // 发送当前数据块
                await SendChunk(chunk, cancellationToken);

                // 所有字符传输完成时，在末尾附带文件名标识 <@@@filename@@@>文件名
                if (i + length >= text.Length)
                    await SendChunk(fileNameTag, cancellationToken);

                // 分割块提示符
                await SendChunk(TransferMarker, cancellationToken);

                lock (_lockObject)
                {
                    _sentChunks++;
                }

                // 更新进度
                Dispatcher.Invoke(() =>
                {
                    SendProgressBar.Value = _sentChunks;
                    ChunksText.Text = string.Format(_loc["filesend.chunks_fmt"], _sentChunks, _totalChunks);
                });
            }
        }

        private async Task SendTextByTyping(string text, int chunkSize, CancellationToken cancellationToken)
        {
            for (int i = 0; i < text.Length; i += chunkSize)
            {
                if (cancellationToken.IsCancellationRequested)
                    break;

                // 等待暂停
                while (_isPaused && !cancellationToken.IsCancellationRequested)
                {
                    await Task.Delay(100);
                }

                if (cancellationToken.IsCancellationRequested)
                    break;

                int length = Math.Min(chunkSize, text.Length - i);
                string chunk = text.Substring(i, length);

                // 发送当前块
                await SendChunk(chunk, cancellationToken);

                lock (_lockObject)
                {
                    _sentChunks++;
                }

                // 更新进度
                Dispatcher.Invoke(() =>
                {
                    SendProgressBar.Value = _sentChunks;
                    ChunksText.Text = string.Format(_loc["filesend.chunks_fmt"], _sentChunks, _totalChunks);
                });
            }
        }

        private async Task SendChunk(string chunk, CancellationToken cancellationToken)
        {
            // 使用基于 Win32 SendInput 的底层输入模拟（NativeInputSimulator）。
            //
            // 【重要修复】原实现使用 SendKeys.SendWait，其中 '+','^','%','~','(',')','{','}'
            // 是特殊控制字符：SendWait("+") 只会按一下 Shift 而不会输出 '+'，
            // 导致 Base64 文本中的所有 '+' 静默丢失，解码后的文件损坏。
            // NativeInputSimulator 通过 VkKeyScanEx 映射 + 修饰键组合，能正确输入全部字符。
            await Task.Run(() =>
            {
                NativeInputSimulator.TypeText(chunk, 10, cancellationToken);
            });
        }

        private void ProgressTimer_Tick(object sender, EventArgs e)
        {
            lock (_lockObject)
            {
                if (_totalChunks > 0)
                {
                    double progressPercent = (_sentChunks * 100.0) / _totalChunks;
                    ProgressPercentText.Text = $"{progressPercent:F1}%";
                }
            }
        }

        private void PauseButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isSending)
            {
                _isPaused = !_isPaused;
                PauseButton.Content = _isPaused ? "继续" : "暂停";
                StatusText.Text = _isPaused ? _loc["filesend.status_paused"] : _loc["filesend.status_sending"];

                if (ShowNotificationsCheckBox.IsChecked == true)
                {
                    ShowNotification("文件发送", _isPaused ? "发送已暂停" : "发送已继续", System.Windows.Forms.ToolTipIcon.Info);
                }
            }
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            CancelSend();
        }

        #region 无软件本体发送

        /// <summary>
        /// "开始发送（无软件本体）"按钮点击：
        /// 强制禁用压缩，使用 Base64 编码，传输完成后弹出平台选择对话框，
        /// 生成对应平台的解码命令并复制到剪贴板。
        /// </summary>
        private async void SendNoClientButton_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_selectedFilePath) || !File.Exists(_selectedFilePath))
                return;

            if (_isSending)
                return;

            var result = MessageBox.Show(
                "「无软件本体」模式说明：\n\n" +
                "1. 文件将使用 Base64 编码（不压缩），接收方无需安装本软件\n" +
                "2. 所有数据块将通过模拟键盘输入发送\n" +
                "3. 接收方将收到的文本保存到 .txt 文件后，运行解密命令即可还原\n" +
                "4. 发送完成后会弹出平台选择，自动生成解码命令并复制到剪贴板\n\n" +
                "发送过程中请勿操作键盘，否则可能导致传输失败。\n" +
                "系统将在 10 秒后开始传输。",
                "确认发送（无软件本体）",
                MessageBoxButton.YesNo,
                MessageBoxImage.Information);

            if (result != MessageBoxResult.Yes)
                return;

            // 提前创建取消源：倒计时期间点击取消即可中止
            _isSending = true;
            _cancellationTokenSource = new CancellationTokenSource();
            var cancellationToken = _cancellationTokenSource.Token;

            // 10秒倒计时
            for (int i = 10; i > 0; i--)
            {
                if (cancellationToken.IsCancellationRequested)
                    return;
                StatusText.Text = string.Format(_loc["filesend.status_preparing"], i);
                await Task.Delay(1000);
            }

            await StartNoClientSend();
        }

        /// <summary>
        /// 无软件本体模式：Base64 编码 → 分块 → 模拟键盘发送
        /// </summary>
        private async Task StartNoClientSend()
        {
            try
            {
                _isSending = true;
                _isPaused = false;
                _sentChunks = 0;
                _totalChunks = 0;
                _totalCharacters = 0;

                Dispatcher.Invoke(() =>
                {
                    StartSendButton.IsEnabled = false;
                    SendNoClientButton.IsEnabled = false;
                    PauseButton.IsEnabled = true;
                    CancelButton.IsEnabled = true;
                    // 视觉上取消勾选压缩选项（不修改实际 CheckBox，仅示意）
                    CompressCheckBox.IsChecked = false;
                });

                // 复用点击处理时创建的取消源（倒计时期间即已生效）
                _cancellationTokenSource ??= new CancellationTokenSource();
                var cancellationToken = _cancellationTokenSource.Token;

                // 读取文件
                byte[] fileData;
                try
                {
                    fileData = await File.ReadAllBytesAsync(_selectedFilePath!, cancellationToken);
                }
                catch (Exception)
                {
                    ResetUI();
                    return;
                }

                // 强制使用 Base64 编码（不压缩）
                string textToSend = Convert.ToBase64String(fileData);
                _totalCharacters = textToSend.Length;

                // 计算分块
                int chunkSize = int.TryParse(ChunkSizeNumberBox.Text, out int size) ? size : 1000;
                _totalChunks = (int)Math.Ceiling((double)textToSend.Length / chunkSize);

                Dispatcher.Invoke(() =>
                {
                    ChunksText.Text = $"分割块：0/{_totalChunks}";
                    CharactersText.Text = string.Format(_loc["filesend.characters_fmt"], _totalCharacters);
                    SendProgressBar.Maximum = _totalChunks;
                    StatusText.Text = _loc["filesend.status_sending"];
                });

                // 启动进度定时器
                _progressTimer = new DispatcherTimer();
                _progressTimer.Interval = TimeSpan.FromMilliseconds(100);
                _progressTimer.Tick += ProgressTimer_Tick;
                _progressTimer.Start();

                // 发送数据
                await SendTextByTyping(textToSend, chunkSize, cancellationToken);

                if (!cancellationToken.IsCancellationRequested)
                {
                    StatusText.Text = _loc["filesend.status_complete"];
                    SendProgressBar.Value = _totalChunks;
                    ProgressPercentText.Text = "100%";

                    // 提取文件名用于生成指令
                    string originalFileName = Path.GetFileName(_selectedFilePath ?? "file.bin");

                    // 弹出平台选择对话框
                    ShowDecodeDialog(originalFileName, CompressCheckBox.IsChecked == true);
                }
            }
            catch (OperationCanceledException)
            {
                StatusText.Text = _loc["filesend.status_cancelled"];
            }
            catch (Exception ex)
            {
                StatusText.Text = _loc["filesend.status_failed"] + ex.Message;
            }
            finally
            {
                ResetUI();
            }
        }

        /// <summary>
        /// 显示平台选择对话框，生成解码命令并复制到剪贴板。
        /// </summary>
        private void ShowDecodeDialog(string originalFileName, bool isCompressed)
        {
            Dispatcher.Invoke(() =>
            {
                // 生成不含扩展名的输出文件名建议
                string outputName = Path.GetFileNameWithoutExtension(originalFileName);
                string inputFile = "received.txt";
                string outputFile = string.IsNullOrEmpty(outputName) ? "output.bin" : outputName + "_restored.bin";

                string modeHint = isCompressed
                    ? "（当前模式：压缩传输 — Deflate + Base93）\n" +
                      "解码需使用 typingTransfer CLI 工具"
                    : "（当前模式：原始传输 — 标准 Base64）\n" +
                      "Windows: certutil 解码  |  Linux: base64 解码";

                // 构建平台选择对话框
                var dialog = new Wpf.Ui.Controls.MessageBox
                {
                    Title = "接收方平台选择",
                    Content = "传输完成！请选择接收方操作系统，\n系统将生成对应的解码命令并复制到剪贴板。\n\n" +
                              modeHint + "\n\n" +
                              "接收方操作步骤：\n" +
                              $"1. 将收到的全部文本保存到 {inputFile}\n" +
                              $"2. 在终端中运行下面的解码命令",
                    PrimaryButtonText = "Windows (CMD)",
                    PrimaryButtonIcon = new Wpf.Ui.Controls.SymbolIcon(Wpf.Ui.Controls.SymbolRegular.WindowConsole20),
                    PrimaryButtonAppearance = Wpf.Ui.Controls.ControlAppearance.Primary,
                    CloseButtonText = "Linux (bash)",
                    CloseButtonIcon = new Wpf.Ui.Controls.SymbolIcon(Wpf.Ui.Controls.SymbolRegular.WindowConsole20),
                    CloseButtonAppearance = Wpf.Ui.Controls.ControlAppearance.Secondary,
                    ShowTitle = true,
                };

                // 异步显示对话框
                _ = ShowDecodeDialogAsync(dialog, inputFile, outputFile, isCompressed);
            });
        }

        private async Task ShowDecodeDialogAsync(Wpf.Ui.Controls.MessageBox dialog, string inputFile, string outputFile, bool isCompressed)
        {
            var result = await dialog.ShowDialogAsync(showAsDialog: true);
            string command = GenerateDecodeCommand(result, inputFile, outputFile, isCompressed);

            if (!string.IsNullOrEmpty(command))
            {
                // 复制到剪贴板
                try
                {
                    System.Windows.Clipboard.SetText(command);

                    string platform = result == Wpf.Ui.Controls.MessageBoxResult.Primary ? "Windows" : "Linux";
                    string extraHint = isCompressed
                        ? "\n\n注意：压缩模式解码需要 typingTransfer CLI 工具。"
                        : "";
                    var infoDialog = new Wpf.Ui.Controls.MessageBox
                    {
                        Title = "解码命令已复制",
                        Content = $"已为 {platform} 生成解码命令并复制到剪贴板！\n\n" +
                                  "请将以下命令粘贴到接收方终端中执行：\n\n" +
                                  command + extraHint + "\n\n" +
                                  "提示：接收方需先将收到的全部文本保存到文本文件中。",
                        CloseButtonText = "确定",
                        CloseButtonIcon = new Wpf.Ui.Controls.SymbolIcon(Wpf.Ui.Controls.SymbolRegular.Checkmark24),
                        CloseButtonAppearance = Wpf.Ui.Controls.ControlAppearance.Primary,
                        ShowTitle = true,
                    };
                    _ = infoDialog.ShowDialogAsync(showAsDialog: true);
                }
                catch (Exception)
                {
                }
            }
        }

        /// <summary>
        /// 根据平台和压缩模式生成解码命令。
        /// </summary>
        private static string GenerateDecodeCommand(Wpf.Ui.Controls.MessageBoxResult platform, string inputFile, string outputFile, bool isCompressed)
        {
            if (platform == Wpf.Ui.Controls.MessageBoxResult.Primary)
            {
                if (isCompressed)
                {
                    // Windows CMD: typingTransfer 解码 (Deflate + Base93)
                    // typingTransfer 为独立 CLI 工具，可单独发布为自包含应用
                    return $"typingTransfer decode {inputFile} {outputFile}";
                }
                else
                {
                    // Windows CMD: certutil 纯 Base64 解码（零依赖，所有 Windows 版本均可用）
                    return $"certutil -decode {inputFile} {outputFile}";
                }
            }
            else
            {
                if (isCompressed)
                {
                    // Linux: typingTransfer 解码 (Deflate + Base93)
                    // typingTransfer 为 .NET 8 应用，可单独发布为自包含
                    return $"./typingTransfer decode {inputFile} {outputFile}";
                }
                else
                {
                    // Linux bash: 纯 Base64 解码
                    return $"base64 -d {inputFile} > {outputFile}";
                }
            }
        }

        #endregion

        private void CancelSend()
        {
            if (_cancellationTokenSource == null)
                return;

            // 直接取消，无需确认；状态重置为就绪
            _cancellationTokenSource.Cancel();
            _progressTimer?.Stop();
            _isSending = false;
            _isPaused = false;
            StatusText.Text = _loc["filesend.status_ready"];
            StartSendButton.IsEnabled = true;
            SendNoClientButton.IsEnabled = !string.IsNullOrEmpty(_selectedFilePath);
            PauseButton.IsEnabled = false;
            PauseButton.Content = "暂停";
            CancelButton.IsEnabled = true;
        }

        private void ResetUI()
        {
            _isSending = false;
            _isPaused = false;

            Dispatcher.Invoke(() =>
            {
                StartSendButton.IsEnabled = true;
                SendNoClientButton.IsEnabled = !string.IsNullOrEmpty(_selectedFilePath);
                PauseButton.IsEnabled = false;
                PauseButton.Content = "暂停";
                CancelButton.IsEnabled = true;

                if (_progressTimer != null)
                {
                    _progressTimer.Stop();
                    _progressTimer.Tick -= ProgressTimer_Tick;
                    _progressTimer = null;
                }
            });
        }

        private void ShowNotification(string title, string message, System.Windows.Forms.ToolTipIcon icon)
        {
            // 这里可以使用系统通知或任务栏气泡提示
            // 简化版本：使用任务栏图标（如果已实现）
            try
            {
            }
            catch (Exception)
            {
            }
        }

        private static string FormatFileSize(long bytes)
        {
            string[] suffixes = { "B", "KB", "MB", "GB", "TB" };
            int counter = 0;
            decimal number = bytes;

            while (Math.Round(number / 1024) >= 1)
            {
                number /= 1024;
                counter++;
            }

            return $"{number:n2} {suffixes[counter]}";
        }

        private void UserControl_Unloaded(object sender, RoutedEventArgs e)
        {
            _keyboardHook.OnKeyDown -= OnKeyDown;
            _keyboardHook.Dispose();
            _cancellationTokenSource?.Dispose();
            _progressTimer?.Stop();
        }
    }
}