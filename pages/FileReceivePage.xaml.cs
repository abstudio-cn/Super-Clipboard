using BinaryToTextEncoding;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Diagnostics;
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
    public partial class FileReceivePage : UserControl
    {
        private readonly KeyboardHookManager _keyboardHook;
        private readonly StringBuilder _receivedText = new();
        private bool _isListening = false;
        private string? _saveDirectory;
        private DispatcherTimer? _progressTimer;
        private int _totalExpectedChunks = 0;
        private int _receivedChunks = 0;
        private long _totalCharacters = 0;
        private DateTime _lastReceiveTime = DateTime.MinValue;
        private readonly object _lockObject = new();
        private string? _decodedFilePath;
        private LocalizationService _loc = null!;

        /// <summary>分割块提示符：传输头结束标记，同时作为各数据块之间的分隔符</summary>
        private const string TransferMarker = "<!@@@@@@@!>";

        /// <summary>
        /// 文件名标识前缀：发送端在末尾附带 "<@@@filename@@@>文件名"，
        /// 文件名内容为 UTF-8 + Base64 编码，接收端解码后用于保存文件命名。
        /// </summary>
        private const string FileNameTagPrefix = "<@@@filename@@@>";

        /// <summary>传输头中"分割块总数"字段的固定宽度（不足前补0）</summary>
        private const int HeaderChunkFieldLength = 20;

        /// <summary>传输头中"字符总数"字段的固定宽度（不足前补0）</summary>
        private const int HeaderCharFieldLength = 80;

        /// <summary>头部扫描上限：超过此长度仍未发现提示符则放弃协议解析，按原始数据接收</summary>
        private const int MaxHeaderScanLength = 2048;

        /// <summary>是否已完成传输头解析（提示符出现后置true）</summary>
        private bool _headerResolved = false;

        /// <summary>是否识别到有效传输协议（头部字段解析成功）</summary>
        private bool _protocolDetected = false;

        /// <summary>已接收的数据字符数（不含传输头与提示符）</summary>
        private long _receivedCharacters = 0;

        /// <summary>协议数据开始接收的时间（用于倒计时估算）</summary>
        private DateTime _receiveStartTime = DateTime.MinValue;

        /// <summary>Shift 状态：由键盘钩子的按下/释放事件维护，避免 GetAsyncKeyState 时序竞态</summary>
        private bool _shiftDown = false;

        /// <summary>
        /// 待定缓冲：新接收的字符先进入此处，确认不属于提示符组成部分后才提交到
        /// _receivedText。因此传输头与分割块提示符永远不会出现在预览/数据中，
        /// 预览只增不删。
        /// </summary>
        private readonly StringBuilder _pending = new();

        /// <summary>是否已进入传输状态，需持续过滤数据块之间的提示符</summary>
        private bool _stripChunkMarkers = false;

        /// <summary>文件名缓冲：识别到文件名标识后累积文件名内容</summary>
        private readonly StringBuilder _fileNameBuffer = new();

        /// <summary>是否处于文件名标识接收模式</summary>
        private bool _inFileNameTag = false;

        /// <summary>从传输标识中解析出的原文件名</summary>
        private string? _receivedFileName = null;

        public FileReceivePage()
        {
            InitializeComponent();
            _keyboardHook = new KeyboardHookManager();
            _keyboardHook.OnKeyDown += OnKeyDown;
            _keyboardHook.OnKeyUp += OnKeyUp;
            _loc = LocalizationService.Instance;
            ApplyLocalization();

            _saveDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "ReceivedFiles");
            SavePathTextBox.Text = _saveDirectory;
            if (!Directory.Exists(_saveDirectory))
                Directory.CreateDirectory(_saveDirectory);
        }

        private void ApplyLocalization()
        {
            PageTitle.Text = _loc["filereceive.title"];
            LblReceiveSettings.Text = _loc["filereceive.receive_settings"];
            LblSavePath.Text = _loc["filereceive.save_path"];
            BrowseSaveButton.Content = _loc["filereceive.browse_save"];
            AutoDetectCheckBox.Content = _loc["filereceive.auto_detect"];
            ShowNotificationsCheckBox.Content = _loc["filereceive.notifications"];
            LblReceiveMode.Text = _loc["filereceive.mode"];
            ModeBase93.Content = _loc["filereceive.mode_base93"];
            ModeBase64.Content = _loc["filereceive.mode_base64"];
            ModeText.Content = _loc["filereceive.mode_text"];
            LblReceiveProgress.Text = _loc["filereceive.receive_progress"];
            LblStatus.Text = _loc["filesend.status_label"];
            StatusText.Text = _loc["filereceive.status_ready"];
            LblPreview.Text = _loc["filereceive.preview"];
            StartListeningButton.Content = _loc["filereceive.start_listening"];
            StopListeningButton.Content = _loc["filereceive.stop_listening"];
            ClearCacheButton.Content = _loc["filereceive.clear_cache"];
            SaveFileButton.Content = _loc["filereceive.save_file"];
        }

        /// <summary>
        /// 钩子释放事件：维护 Shift 状态（钩子事件流与字符按键同序，无时序竞态）。
        /// </summary>
        private void OnKeyUp(Key key)
        {
            if (key == Key.LeftShift || key == Key.RightShift)
                _shiftDown = false;
        }

        private void OnKeyDown(Key key)
        {
            // 维护 Shift 状态：必须独立于监听状态跟踪，保证任何时刻状态准确
            if (key == Key.LeftShift || key == Key.RightShift)
            {
                _shiftDown = true;
                return;
            }
            if (!_isListening) return;
            string? charStr = KeyToChar(key);
            if (charStr == null) return;

            bool protocolStartedNow = false;
            lock (_lockObject)
            {
                // 新字符先进入待定缓冲，确认不是提示符后才提交（预览只增不删）
                _pending.Append(charStr);
                _lastReceiveTime = DateTime.Now;
                protocolStartedNow = ProcessIncomingData();
            }
            Dispatcher.Invoke(() =>
            {
                if (protocolStartedNow)
                    StartEtaTimer();
                UpdatePreview();
                UpdateProgress();
            });
        }

        /// <summary>
        /// 处理新接收的字符（调用方需持有 _lockObject）。
        /// 采用"待定缓冲"机制：新字符先进入 _pending，确认不属于提示符组成部分后才提交，
        /// 因此传输头与分割块提示符永远不会出现在预览/数据中，预览只增不删。
        /// 1. 头部解析阶段（_headerResolved=false）：在待定缓冲中查找提示符。
        ///    - 提示符前紧跟 100 位全数字 → 有效传输头：提交头部之前的文本（如有），
        ///      丢弃头部本体，解析出总块数/总字符数。
        ///    - 提示符前为纯数字残头（从传输头中间开始接收）→ 整体丢弃。
        ///    - 其他（数据块提示符，如中途开始接收）→ 提交其前的数据，丢弃提示符本身。
        /// 2. 传输阶段：待定缓冲末尾凑成完整提示符则丢弃并计数，
        ///    否则超过 10 字符即提交（确认是数据）。
        /// 返回 true 表示本次调用解析出了有效传输头（已获得总块数/总字符数）。
        /// </summary>
        private bool ProcessIncomingData()
        {
            if (!_headerResolved)
            {
                string pendingText = _pending.ToString();
                int markerPos = pendingText.IndexOf(TransferMarker, StringComparison.Ordinal);
                if (markerPos >= 0)
                {
                    bool protocolStarted = false;

                    // 判断提示符前是否紧跟着完整头部：[20位分割块总数][80位字符总数]（全数字）
                    int headerStart = markerPos - HeaderChunkFieldLength - HeaderCharFieldLength;
                    bool isHeader = headerStart >= 0 &&
                                    IsAllDigits(pendingText, headerStart, HeaderChunkFieldLength + HeaderCharFieldLength);

                    if (isHeader)
                    {
                        // 有效传输头：先提交头部之前的文本（保留监听前/头部前的输入），再丢弃头部本体
                        for (int i = 0; i < headerStart; i++) _receivedText.Append(pendingText[i]);
                        string chunkField = pendingText.Substring(headerStart, HeaderChunkFieldLength);
                        string charField = pendingText.Substring(headerStart + HeaderChunkFieldLength, HeaderCharFieldLength);
                        if (int.TryParse(chunkField, out int chunks)) _totalExpectedChunks = chunks;
                        if (long.TryParse(charField, out long chars)) _totalCharacters = chars;
                        if (_totalExpectedChunks > 0 || _totalCharacters > 0)
                        {
                            _protocolDetected = true;
                            protocolStarted = true;
                            _receiveStartTime = DateTime.Now;
                        }
                        _receivedChunks = 0;
                    }
                    else if (markerPos > 0 && IsAllDigits(pendingText, 0, markerPos))
                    {
                        // 纯数字残头（从传输头中间开始接收）：整体丢弃，不提交
                    }
                    else
                    {
                        // 数据块提示符（中途开始接收）：提交其前的数据，丢弃提示符本身
                        for (int i = 0; i < markerPos; i++) _receivedText.Append(pendingText[i]);
                        _receivedChunks++;
                    }

                    _pending.Clear();
                    _headerResolved = true;
                    _stripChunkMarkers = true;
                    _receivedCharacters = _receivedText.Length;
                    return protocolStarted;
                }

                // 提示符尚未出现：提交超出头部窗口的字符，仅保留末尾最多 111 字符
                int headerWindow = HeaderChunkFieldLength + HeaderCharFieldLength + TransferMarker.Length;
                if (_pending.Length > headerWindow)
                {
                    int excess = _pending.Length - headerWindow;
                    for (int i = 0; i < excess; i++) _receivedText.Append(_pending[i]);
                    _pending.Remove(0, excess);
                }

                if (_receivedText.Length + _pending.Length > MaxHeaderScanLength)
                {
                    // 未发现协议头：按原始数据接收，后续不再过滤提示符
                    _receivedText.Append(_pending);
                    _pending.Clear();
                    _headerResolved = true;
                    _stripChunkMarkers = false;
                    _receivedCharacters = _receivedText.Length;
                }
                return false;
            }

            // 传输阶段
            if (_stripChunkMarkers)
            {
                if (_inFileNameTag)
                {
                    // 文件名模式：字符进入文件名缓冲，直到传输提示符终止
                    if (EndsWith(_pending, TransferMarker))
                    {
                        _pending.Remove(_pending.Length - TransferMarker.Length, TransferMarker.Length);
                        _fileNameBuffer.Append(_pending);
                        _pending.Clear();
                        _receivedFileName = DecodeFileNameTag(_fileNameBuffer.ToString());
                        _fileNameBuffer.Clear();
                        _inFileNameTag = false;
                        _receivedChunks++; // 该提示符同时是最后一个数据块的结束符
                    }
                    else if (_pending.Length > TransferMarker.Length)
                    {
                        // 提交非提示符字符到文件名缓冲（保留末尾最多11字符以防是提示符）
                        int excess = _pending.Length - TransferMarker.Length;
                        _fileNameBuffer.Append(_pending.ToString(0, excess));
                        _pending.Remove(0, excess);
                    }
                }
                else
                {
                    // 数据模式：先检测提示符，再识别文件名标识 <@@@filename@@@>文件名。
                    // 待定缓冲末尾若为文件名标识的残缺前缀，暂缓提交，等待后续字符确认。
                    while (_pending.Length >= TransferMarker.Length)
                    {
                        if (EndsWith(_pending, TransferMarker))
                        {
                            _pending.Remove(_pending.Length - TransferMarker.Length, TransferMarker.Length);
                            _receivedChunks++;
                            continue;
                        }

                        int tagPos = IndexOf(_pending, FileNameTagPrefix);
                        if (tagPos >= 0)
                        {
                            // 提交标识前的数据，随后进入文件名模式
                            for (int i = 0; i < tagPos; i++) _receivedText.Append(_pending[i]);
                            _pending.Remove(0, tagPos + FileNameTagPrefix.Length);
                            _inFileNameTag = true;
                            break;
                        }

                        // 末尾若为提示符或文件名标识的残缺前缀，暂缓提交；否则提交确认的数据字符
                        int partialLen = Math.Max(
                            TrailingPrefixLength(_pending, TransferMarker),
                            TrailingPrefixLength(_pending, FileNameTagPrefix));
                        int commitCount = _pending.Length - partialLen;
                        if (commitCount <= 0)
                            break; // 全部字符均可能是残缺前缀，等待后续字符确认
                        for (int i = 0; i < commitCount; i++) _receivedText.Append(_pending[i]);
                        _pending.Remove(0, commitCount);
                    }
                }
                _receivedCharacters = _inFileNameTag
                    ? _receivedText.Length // 文件名模式时数据已全部收齐
                    : _receivedText.Length + _pending.Length - TrailingMarkerPrefixLength(_pending);
            }
            else
            {
                // 原始模式：全部提交
                _receivedText.Append(_pending);
                _pending.Clear();
                _receivedCharacters = _receivedText.Length;
            }
            return false;
        }

        /// <summary>
        /// 解码文件名标识内容：UTF-8 + Base64；解码失败时按明文处理（向后兼容）。
        /// </summary>
        private static string DecodeFileNameTag(string content)
        {
            try
            {
                return Encoding.UTF8.GetString(Convert.FromBase64String(content.Trim()));
            }
            catch
            {
                return content.Trim();
            }
        }

        /// <summary>
        /// 在 StringBuilder 中查找指定字符串，返回首次出现位置（未找到返回 -1）。
        /// </summary>
        private static int IndexOf(StringBuilder sb, string value)
        {
            if (value.Length == 0) return 0;
            int max = sb.Length - value.Length;
            for (int i = 0; i <= max; i++)
            {
                bool match = true;
                for (int j = 0; j < value.Length; j++)
                {
                    if (sb[i + j] != value[j]) { match = false; break; }
                }
                if (match) return i;
            }
            return -1;
        }

        /// <summary>
        /// 返回待定缓冲末尾与指定字符串前缀匹配的长度（0 ~ 长度-1）。
        /// 用于识别尚未敲完的文件名标识残缺前缀，避免其被当作数据提交。
        /// </summary>
        private static int TrailingPrefixLength(StringBuilder sb, string prefix)
        {
            int maxLen = Math.Min(sb.Length, prefix.Length - 1);
            for (int len = maxLen; len >= 1; len--)
            {
                bool match = true;
                for (int i = 0; i < len; i++)
                {
                    if (sb[sb.Length - len + i] != prefix[i]) { match = false; break; }
                }
                if (match) return len;
            }
            return 0;
        }

        /// <summary>
        /// 判断 StringBuilder 是否以指定字符串结尾。
        /// </summary>
        private static bool EndsWith(StringBuilder sb, string value)
        {
            if (sb.Length < value.Length) return false;
            int offset = sb.Length - value.Length;
            for (int i = 0; i < value.Length; i++)
            {
                if (sb[offset + i] != value[i]) return false;
            }
            return true;
        }

        /// <summary>
        /// 返回待定缓冲末尾与提示符前缀匹配的长度（0~10）。
        /// 这些字符属于未敲完的提示符，不计入已接收数据字符数。
        /// </summary>
        private static int TrailingMarkerPrefixLength(StringBuilder sb)
        {
            int maxLen = Math.Min(sb.Length, TransferMarker.Length - 1);
            for (int len = maxLen; len >= 1; len--)
            {
                bool match = true;
                for (int i = 0; i < len; i++)
                {
                    if (sb[sb.Length - len + i] != TransferMarker[i]) { match = false; break; }
                }
                if (match) return len;
            }
            return 0;
        }

        /// <summary>
        /// 判断字符串指定区间是否全为数字字符。
        /// </summary>
        private static bool IsAllDigits(string text, int start, int count)
        {
            if (start < 0 || count <= 0 || start + count > text.Length)
                return false;
            for (int i = start; i < start + count; i++)
            {
                if (text[i] < '0' || text[i] > '9')
                    return false;
            }
            return true;
        }

        private string? KeyToChar(Key key)
        {
            // 使用钩子事件维护的 _shiftDown 状态。
            // 【重要】不能用 Keyboard.IsKeyDown 查询物理 Shift 状态：远程桌面等注入路径下
            // 它与按键事件流存在时序竞态，曾导致 Base93 数据随机损坏
            // （'<'→','、'-'→'_'、'~'→'`'、'"'→'''、'j'→'J' 等），无法解码。
            bool shift = _shiftDown;
            if (key >= Key.A && key <= Key.Z)
                return shift ? key.ToString().ToUpper() : key.ToString().ToLower();
            if (key >= Key.D0 && key <= Key.D9)
            {
                if (shift)
                {
                    char[] shiftedDigits = { ')', '!', '@', '#', '$', '%', '^', '&', '*', '(' };
                    return shiftedDigits[key - Key.D0].ToString();
                }
                return key.ToString().Substring(1);
            }
            if (key >= Key.NumPad0 && key <= Key.NumPad9)
                return key.ToString().Substring(6);
            return key switch
            {
                Key.Space => " ",
                Key.Enter => "\r\n",
                Key.Tab => "\t",
                Key.OemPlus => shift ? "+" : "=",
                Key.OemMinus => shift ? "_" : "-",
                Key.OemQuestion => shift ? "?" : "/",
                Key.OemPeriod => shift ? ">" : ".",
                Key.OemComma => shift ? "<" : ",",
                Key.OemSemicolon => shift ? ":" : ";",
                Key.OemQuotes => shift ? "\"" : "'",
                Key.OemOpenBrackets => shift ? "{" : "[",
                Key.OemCloseBrackets => shift ? "}" : "]",
                Key.OemPipe => shift ? "|" : "\\",
                Key.OemTilde => shift ? "~" : "`",
                _ => null
            };
        }

        private void StartListeningButton_Click(object sender, RoutedEventArgs e)
        {
            _isListening = true;
            lock (_lockObject)
            {
                _receivedText.Clear();
                _pending.Clear();
                _fileNameBuffer.Clear();
                _inFileNameTag = false;
                _receivedFileName = null;
                _receivedChunks = 0;
                _totalExpectedChunks = 0;
                _totalCharacters = 0;
                _receivedCharacters = 0;
                _headerResolved = false;
                _protocolDetected = false;
                _stripChunkMarkers = false;
                _decodedFilePath = null;
                _receiveStartTime = DateTime.MinValue;
            }
            StartListeningButton.IsEnabled = false;
            StopListeningButton.IsEnabled = true;
            SaveFileButton.IsEnabled = false;
            StatusText.Text = _loc["filereceive.status_listening"];
            UpdateProgress();
        }

        private void StopListeningButton_Click(object sender, RoutedEventArgs e)
        {
            _isListening = false;
            StartListeningButton.IsEnabled = true;
            StopListeningButton.IsEnabled = false;
            StopEtaTimer();
            TryDecodeReceivedData();
        }

        private void ClearCacheButton_Click(object sender, RoutedEventArgs e)
        {
            lock (_lockObject)
            {
                _receivedText.Clear();
                _pending.Clear();
                _fileNameBuffer.Clear();
                _inFileNameTag = false;
                _receivedFileName = null;
                _receivedChunks = 0;
                _totalExpectedChunks = 0;
                _totalCharacters = 0;
                _receivedCharacters = 0;
                _headerResolved = false;
                _protocolDetected = false;
                _stripChunkMarkers = false;
                _decodedFilePath = null;
                _receiveStartTime = DateTime.MinValue;
            }
            StopEtaTimer();
            SaveFileButton.IsEnabled = false;
            StatusText.Text = _loc["filereceive.status_ready"];
            PreviewTextBox.Text = "";
            ChunksText.Text = string.Format(_loc["filereceive.chunks_fmt"], 0, 0);
            CharactersText.Text = string.Format(_loc["filereceive.characters_fmt"], 0);
            EtaText.Text = "";
            ReceiveProgressBar.Value = 0;
            ProgressPercentText.Text = "0%";
        }

        private void BrowseSavePathButton_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = _loc["filereceive.save_path"],
                SelectedPath = _saveDirectory ?? ""
            };
            if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                _saveDirectory = dlg.SelectedPath;
                SavePathTextBox.Text = _saveDirectory;
            }
        }

        private void SaveFileButton_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_decodedFilePath) || !File.Exists(_decodedFilePath))
                return;
            var saveFileDialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = _loc["filereceive.save_file"],
                FileName = Path.GetFileName(_decodedFilePath),
                Filter = "All Files|*.*"
            };
            if (saveFileDialog.ShowDialog() == true)
            {
                try { File.Copy(_decodedFilePath, saveFileDialog.FileName, true); }
                catch (Exception) { }
            }
        }

        private void UpdatePreview()
        {
            // 预览只显示已确认的数据内容（传输头与提示符不会进入），只增不删
            string text;
            lock (_lockObject)
            {
                text = _receivedText.ToString();
            }
            PreviewTextBox.Text = text;
        }

        /// <summary>
        /// 更新接收进度显示：分割块计数、字符数（已接收/整体）、进度条与剩余时间估算。
        /// </summary>
        private void UpdateProgress()
        {
            long received, total;
            int chunksReceived, chunksTotal;
            bool protocol;
            lock (_lockObject)
            {
                received = _receivedCharacters;
                total = _totalCharacters;
                chunksReceived = _receivedChunks;
                chunksTotal = _totalExpectedChunks;
                protocol = _protocolDetected;
            }

            // 分割块计数（协议模式下封顶显示）
            if (protocol && chunksTotal > 0)
                ChunksText.Text = string.Format(_loc["filereceive.chunks_fmt"], Math.Min(chunksReceived, chunksTotal), chunksTotal);
            else
                ChunksText.Text = string.Format(_loc["filereceive.chunks_fmt"], chunksReceived, chunksTotal);

            // 字符数：已接收字符/整体字符数
            if (protocol && total > 0)
                CharactersText.Text = string.Format(_loc["filereceive.characters_progress_fmt"], received, total);
            else
                CharactersText.Text = string.Format(_loc["filereceive.characters_fmt"], received);

            if (protocol && total > 0)
            {
                double progress = Math.Min(1.0, (double)received / total);
                ReceiveProgressBar.Value = progress * 100;
                ProgressPercentText.Text = $"{(int)(progress * 100)}%";
                UpdateEta(received, total);

                // 传输完成（字符数与块数均达到预期）：自动停止监听并解码，启用保存按钮
                if (received >= total && chunksReceived >= chunksTotal && _isListening)
                {
                    FinalizeReception();
                }
            }
            else
            {
                EtaText.Text = "";
            }
        }

        /// <summary>
        /// 传输完成后自动收尾：停止监听、停止倒计时并尝试解码保存（启用保存按钮）。
        /// </summary>
        private void FinalizeReception()
        {
            _isListening = false;
            StopEtaTimer();
            StartListeningButton.IsEnabled = true;
            StopListeningButton.IsEnabled = false;
            TryDecodeReceivedData();
        }

        /// <summary>
        /// 更新传送倒计时估时（根据平均接收速度估算剩余时间）。
        /// </summary>
        private void UpdateEta(long received, long total)
        {
            long remainingChars = total - received;
            if (remainingChars <= 0)
            {
                EtaText.Text = _loc["filereceive.eta_done"];
                return;
            }

            double elapsedSeconds = (DateTime.Now - _receiveStartTime).TotalSeconds;
            if (elapsedSeconds <= 0 || received <= 0)
            {
                EtaText.Text = _loc["filereceive.eta_unknown"];
                return;
            }

            double speed = received / elapsedSeconds; // 字符/秒
            double remainingSeconds = remainingChars / speed;
            long totalSec = (long)Math.Ceiling(remainingSeconds);
            long hours = totalSec / 3600;
            long minutes = (totalSec % 3600) / 60;
            long secs = totalSec % 60;
            EtaText.Text = hours > 0
                ? string.Format(_loc["filereceive.eta_fmt_h"], hours, minutes, secs)
                : string.Format(_loc["filereceive.eta_fmt_m"], minutes, secs);
        }

        /// <summary>
        /// 启动倒计时定时器（每秒刷新剩余时间）。
        /// </summary>
        private void StartEtaTimer()
        {
            if (_progressTimer != null) return;
            _progressTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1000) };
            _progressTimer.Tick += EtaTimer_Tick;
            _progressTimer.Start();
        }

        /// <summary>
        /// 停止倒计时定时器。
        /// </summary>
        private void StopEtaTimer()
        {
            if (_progressTimer == null) return;
            _progressTimer.Stop();
            _progressTimer.Tick -= EtaTimer_Tick;
            _progressTimer = null;
        }

        private void EtaTimer_Tick(object? sender, EventArgs e)
        {
            UpdateProgress();
        }

        private void TryDecodeReceivedData()
        {
            if (_receivedText.Length == 0)
                return;
            try
            {
                string receivedText;
                lock (_lockObject)
                {
                    if (_inFileNameTag)
                    {
                        // 传输在文件名标识中途停止：丢弃文件名残符（不属于数据）
                        _pending.Clear();
                        _inFileNameTag = false;
                    }
                    // 提交待定缓冲中剩余的数据（可能有最多几个字符延迟确认）
                    if (_pending.Length > 0)
                    {
                        _receivedText.Append(_pending);
                        _pending.Clear();
                    }
                    receivedText = _receivedText.ToString();
                }
                if (_protocolDetected)
                {
                    // 去除可能残留的不完整分割块提示符尾部
                    receivedText = TrimPartialMarker(receivedText);
                }
                byte[] decodedData;
                int mode = ReceiveModeComboBox.SelectedIndex;
                switch (mode)
                {
                    case 0:
                        byte[] encodedBytes = Encoding.ASCII.GetBytes(receivedText);
                        decodedData = BinaryTextCodec.Decode(encodedBytes);
                        break;
                    case 1:
                        decodedData = Convert.FromBase64String(receivedText);
                        break;
                    case 2:
                        decodedData = Encoding.UTF8.GetBytes(receivedText);
                        break;
                    default:
                        throw new InvalidOperationException("Unknown receive mode");
                }
                string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");

                // 优先使用传输末尾文件名标识提供的原文件名
                string fileName;
                string taggedName = SanitizeFileName(_receivedFileName);
                if (!string.IsNullOrEmpty(taggedName))
                {
                    fileName = taggedName;
                    // 同名文件已存在时附加时间戳，避免覆盖
                    if (File.Exists(Path.Combine(_saveDirectory!, fileName)))
                    {
                        fileName = Path.GetFileNameWithoutExtension(fileName) + "_" + timestamp + Path.GetExtension(fileName);
                    }
                }
                else
                {
                    fileName = mode == 2 ? $"received_text_{timestamp}.txt" : $"received_file_{timestamp}.bin";
                }

                _decodedFilePath = Path.Combine(_saveDirectory!, fileName);
                File.WriteAllBytes(_decodedFilePath, decodedData);
                SaveFileButton.IsEnabled = true;
                StatusText.Text = _loc["filereceive.status_complete"];
                if (ShowNotificationsCheckBox.IsChecked == true)
                    ShowNotification(_loc["filereceive.decode_complete"], $"{_loc["filereceive.decode_success"]}: {fileName}", System.Windows.Forms.ToolTipIcon.Info);
            }
            catch (Exception)
            {
                // 解码失败时给出提示（例如接收模式与发送端不匹配、数据不完整）
                StatusText.Text = _loc["filereceive.decode_failed"];
            }
        }

        /// <summary>
        /// 清理文件名：仅取文件名部分，替换非法字符。
        /// </summary>
        private static string SanitizeFileName(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "";
            string cleaned = Path.GetFileName(name.Trim());
            foreach (char c in Path.GetInvalidFileNameChars())
                cleaned = cleaned.Replace(c, '_');
            return cleaned.Trim();
        }

        /// <summary>
        /// 去除文本尾部可能残留的不完整分割块提示符前缀。
        /// </summary>
        private static string TrimPartialMarker(string text)
        {
            for (int len = TransferMarker.Length - 1; len >= 1; len--)
            {
                if (text.EndsWith(TransferMarker.Substring(0, len), StringComparison.Ordinal))
                    return text.Substring(0, text.Length - len);
            }
            return text;
        }

        private void ShowNotification(string title, string text, System.Windows.Forms.ToolTipIcon icon)
        {
            var notifyIcon = new System.Windows.Forms.NotifyIcon { Visible = true, BalloonTipTitle = title, BalloonTipText = text, BalloonTipIcon = icon };
            notifyIcon.ShowBalloonTip(3000);
            notifyIcon.Dispose();
        }
    }
}
