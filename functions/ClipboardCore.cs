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
using System.Windows.Threading;
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

    /// <summary>
    /// 粘贴队列模式：关闭 / 顺序粘贴 / 倒序粘贴
    /// </summary>
    public enum PasteQueueMode
    {
        Off,
        Sequential,
        Reverse
    }

    public class ClipboardCore : IDisposable
    {

        /// <summary>
        /// 获取剪贴板变更序号：每次剪贴板内容变化时递增，用于可靠检测内容变化。
        /// </summary>
        [DllImport("user32.dll")]
        private static extern uint GetClipboardSequenceNumber();

        private Thread _monitorThread;
        private ClipboardData? _currentData;
        private readonly KeyboardHookManager _keyboardHook;
        private readonly KeyboardHookManager _hookManager;
        private readonly SettingsManager _settings;
        private readonly HashSet<Key> _pressedKeys = new();
        private bool _pendingKeystrokesPaste = false;

        // ── 历史粘贴选择状态 ──────────────────────────────
        // 按下粘贴热键后进入可配置的等待窗口（默认 1 秒，见设置）:
        //   数字键 1-9 选择历史第 1-9 条，0 选择第 10 条，立即粘贴；
        //   窗口内无任何输入则默认粘贴第一条；
        //   其他按键输入则取消本次等待。
        private bool _historySelectionPending = false;
        private PasteMode _pendingPasteMode = PasteMode.Normal;
        private DispatcherTimer? _historySelectTimer;
        // ──────────────────────────────────────────────────

        // ── 顺序/倒序粘贴队列模式 ──────────────────────────────
        // Off=关闭; Sequential=顺序粘贴(最新→最旧循环); Reverse=倒序粘贴(最旧→最新循环)
        private PasteQueueMode _queueMode = PasteQueueMode.Off;
        private List<ClipboardData> _queueSnapshot = new();
        private int _queueIndex = 0;
        // ──────────────────────────────────────────────────────

        // ── Excel 编辑模式缓冲 ──────────────────────────────
        // 复制 Excel 时剪贴板会同时写入文字与图片。开启该模式后，
        // 文本捕获先缓冲 1 秒（等待所有格式完全复制），
        // 期间若图片随后到达则图片先入列表，1 秒后文本入列表 → 文本保持在最前。
        private ClipboardData? _pendingExcelText;
        private DateTime _excelTextBufferTime;
        // ──────────────────────────────────────────────────────

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

        /// <summary>当前顺序/倒序粘贴队列模式。</summary>
        public PasteQueueMode QueueMode => _queueMode;

        /// <summary>粘贴队列模式改变事件（用于更新标题栏与历史页按钮）。</summary>
        public event Action<PasteQueueMode>? PasteQueueModeChanged;

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
            _keyboardHook.OnKeyDownIntercept += OnKeyDownInterceptHandler;
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

            // 检查是否匹配停止模拟输入快捷键
            if (_simulationCts != null &&
                GetCurrentModifiers() == _settings.StopSimulationHotkey.Modifiers &&
                key == _settings.StopSimulationHotkey.Key)
            {
                StopSimulation();
            }
        }

        /// <summary>
        /// 当前按住的修饰键集合。
        /// </summary>
        private ModifierKeys GetCurrentModifiers()
        {
            ModifierKeys mods = ModifierKeys.None;
            if (_ctrlPressed) mods |= ModifierKeys.Control;
            if (_altPressed) mods |= ModifierKeys.Alt;
            if (_shiftPressed) mods |= ModifierKeys.Shift;
            if (_winPressed) mods |= ModifierKeys.Windows;
            return mods;
        }

        /// <summary>
        /// 按键拦截处理：实现历史粘贴选择流程。返回 true 表示吞掉该按键。
        /// </summary>
        /// <param name="key">按键</param>
        /// <param name="isInjected">是否为程序注入的模拟按键</param>
        private bool OnKeyDownInterceptHandler(Key key, bool isInjected)
        {
            // 程序自身注入的模拟按键（如模拟 Ctrl+V）一律放行，
            // 否则会拦截自己的模拟粘贴形成死循环
            if (isInjected)
            {
                return false;
            }

            // 修饰键：不拦截、不取消等待
            if (key == Key.LeftCtrl || key == Key.RightCtrl ||
                key == Key.LeftAlt || key == Key.RightAlt ||
                key == Key.LeftShift || key == Key.RightShift ||
                key == Key.LWin || key == Key.RWin)
            {
                return false;
            }

            // Excel 编辑模式开关快捷键：切换开启/关闭并吞掉按键
            ModifierKeys excelMods = GetCurrentModifiers();
            if (excelMods == _settings.ExcelModeHotkey.Modifiers && key == _settings.ExcelModeHotkey.Key)
            {
                _settings.SetExcelEditMode(!_settings.ExcelEditMode);
                Logger.Info($"Excel 编辑模式: {(_settings.ExcelEditMode ? "已开启" : "已关闭")} (快捷键)");
                return true;
            }

            // 顺序/倒序粘贴队列模式开启时：粘贴热键直接粘贴队列中的下一条
            if (_queueMode != PasteQueueMode.Off)
            {
                ModifierKeys queueMods = GetCurrentModifiers();
                if (queueMods == _settings.NormalPasteHotkey.Modifiers && key == _settings.NormalPasteHotkey.Key)
                {
                    ExecuteQueuePaste(PasteMode.Normal);
                    return true;
                }
                if (queueMods == _settings.KeystrokesPasteHotkey.Modifiers && key == _settings.KeystrokesPasteHotkey.Key)
                {
                    ExecuteQueuePaste(PasteMode.Keystrokes);
                    return true;
                }
            }

            if (_historySelectionPending)
            {
                // 数字键：选择对应历史条目并立即粘贴
                if (TryGetHistoryIndex(key, out int index))
                {
                    Logger.Info($"历史粘贴: 数字键 {key} 选择第 {index + 1} 条");
                    ExecuteHistoryPaste(index);
                    return true; // 吞掉数字键，避免其被输入到目标窗口
                }

                // 重复按下粘贴热键：重置 1 秒等待窗口
                ModifierKeys pendingMods = GetCurrentModifiers();
                if (pendingMods == _settings.NormalPasteHotkey.Modifiers && key == _settings.NormalPasteHotkey.Key)
                {
                    ArmHistorySelection(PasteMode.Normal);
                    return true;
                }
                if (pendingMods == _settings.KeystrokesPasteHotkey.Modifiers && key == _settings.KeystrokesPasteHotkey.Key)
                {
                    ArmHistorySelection(PasteMode.Keystrokes);
                    return true;
                }

                // 其他按键：取消本次历史粘贴等待，按键正常放行
                Logger.Info("历史粘贴: 检测到其他输入, 取消等待");
                CancelHistorySelection();
                return false;
            }

            ModifierKeys mods = GetCurrentModifiers();

            // 普通粘贴快捷键 → 进入历史选择模式
            if (mods == _settings.NormalPasteHotkey.Modifiers && key == _settings.NormalPasteHotkey.Key)
            {
                ArmHistorySelection(PasteMode.Normal);
                return true; // 吞掉快捷键，粘贴时机由选择/超时决定
            }

            // 模拟按键粘贴快捷键 → 进入历史选择模式
            if (mods == _settings.KeystrokesPasteHotkey.Modifiers && key == _settings.KeystrokesPasteHotkey.Key)
            {
                ArmHistorySelection(PasteMode.Keystrokes);
                return true;
            }

            return false;
        }

        /// <summary>
        /// 数字键 → 历史条目索引映射：1-9 → 第 1-9 条，0 → 第 10 条。
        /// </summary>
        private static bool TryGetHistoryIndex(Key key, out int index)
        {
            switch (key)
            {
                case Key.D1: case Key.NumPad1: index = 0; return true;
                case Key.D2: case Key.NumPad2: index = 1; return true;
                case Key.D3: case Key.NumPad3: index = 2; return true;
                case Key.D4: case Key.NumPad4: index = 3; return true;
                case Key.D5: case Key.NumPad5: index = 4; return true;
                case Key.D6: case Key.NumPad6: index = 5; return true;
                case Key.D7: case Key.NumPad7: index = 6; return true;
                case Key.D8: case Key.NumPad8: index = 7; return true;
                case Key.D9: case Key.NumPad9: index = 8; return true;
                case Key.D0: case Key.NumPad0: index = 9; return true;
                default: index = -1; return false;
            }
        }

        /// <summary>
        /// 进入历史粘贴选择模式：启动等待窗口计时器（时长取自设置，200-2000ms，默认 1000ms）。
        /// </summary>
        private void ArmHistorySelection(PasteMode mode)
        {
            _historySelectionPending = true;
            _pendingPasteMode = mode;

            int waitMs = Math.Max(200, Math.Min(2000, _settings.HistoryPasteWaitMs));

            _historySelectTimer?.Stop();
            _historySelectTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(waitMs)
            };
            _historySelectTimer.Tick += (s, e) =>
            {
                _historySelectTimer.Stop();
                _historySelectTimer = null;
                if (!_historySelectionPending) return;

                Logger.Info($"历史粘贴: {waitMs} 毫秒超时, 默认粘贴第一条");
                ExecuteHistoryPaste(0);
            };
            _historySelectTimer.Start();

            Logger.Info($"历史粘贴: 进入选择模式 ({mode}), 数字键 1-9=第 1-9 条, 0=第 10 条, {waitMs} 毫秒无输入默认第 1 条");
        }

        /// <summary>
        /// 取消历史粘贴等待。
        /// </summary>
        private void CancelHistorySelection()
        {
            _historySelectionPending = false;
            _historySelectTimer?.Stop();
            _historySelectTimer = null;
        }

        /// <summary>
        /// 执行历史条目粘贴。
        /// </summary>
        /// <param name="index">历史条目索引（0 = 第一条/最新，9 = 第十条）</param>
        private void ExecuteHistoryPaste(int index)
        {
            _historySelectionPending = false;
            _historySelectTimer?.Stop();
            _historySelectTimer = null;

            var history = GlobalData.HistoryManager.HistoryItems;
            ClipboardData? item = null;

            if (history != null && history.Count > 0)
            {
                if (index >= 0 && index < history.Count)
                {
                    item = history[index];
                }
                else
                {
                    Logger.Warn($"历史粘贴: 第 {index + 1} 条不存在 (历史共 {history.Count} 条), 取消粘贴");
                    return;
                }
            }

            // 历史为空时回退到当前剪贴板数据
            item ??= _currentData;

            if (item == null)
            {
                Logger.Warn("历史粘贴: 无可用的粘贴内容");
                return;
            }

            PasteItem(item, _pendingPasteMode);
        }

        /// <summary>
        /// 循环切换粘贴队列模式：关闭 → 顺序粘贴 → 倒序粘贴 → 关闭。
        /// </summary>
        public void CyclePasteQueueMode()
        {
            switch (_queueMode)
            {
                case PasteQueueMode.Off:
                    SetPasteQueueMode(PasteQueueMode.Sequential);
                    break;
                case PasteQueueMode.Sequential:
                    SetPasteQueueMode(PasteQueueMode.Reverse);
                    break;
                default:
                    SetPasteQueueMode(PasteQueueMode.Off);
                    break;
            }
        }

        /// <summary>
        /// 设置粘贴队列模式。开启时快照当前历史列表并重置队列指针：
        /// 顺序粘贴从最新条目开始（列表第 1 条），倒序粘贴从最旧条目开始（列表最后 1 条）。
        /// </summary>
        public void SetPasteQueueMode(PasteQueueMode mode)
        {
            _queueMode = mode;

            if (mode == PasteQueueMode.Off)
            {
                _queueSnapshot.Clear();
                _queueIndex = 0;
                Logger.Info("队列粘贴: 模式已关闭");
            }
            else
            {
                var history = GlobalData.HistoryManager.HistoryItems;
                _queueSnapshot = history != null && history.Count > 0
                    ? new List<ClipboardData>(history)
                    : new List<ClipboardData>();

                if (_queueSnapshot.Count == 0)
                {
                    Logger.Warn("队列粘贴: 历史为空, 开启后每次粘贴将回退到当前剪贴板内容");
                    _queueIndex = 0;
                }
                else
                {
                    _queueIndex = mode == PasteQueueMode.Sequential
                        ? 0                            // 最新条目（列表顶部）
                        : _queueSnapshot.Count - 1;    // 最旧条目（列表底部）
                }

                Logger.Info($"队列粘贴: {mode} 模式开启, 快照 {_queueSnapshot.Count} 条");
            }

            PasteQueueModeChanged?.Invoke(_queueMode);
        }

        /// <summary>
        /// 队列模式下的粘贴：取出队列指针指向的条目并粘贴，然后推进指针（循环）。
        /// </summary>
        private void ExecuteQueuePaste(PasteMode pasteMode)
        {
            ClipboardData? item = null;

            if (_queueSnapshot.Count > 0)
            {
                // 指针归一化（防止快照变化导致越界）
                int count = _queueSnapshot.Count;
                int idx = ((_queueIndex % count) + count) % count;
                item = _queueSnapshot[idx];

                Logger.Info($"队列粘贴: {_queueMode} 第 {idx + 1}/{count} 条");

                // 推进指针：顺序 → 向后(最新→最旧)，倒序 → 向前(最旧→最新)，循环
                if (_queueMode == PasteQueueMode.Sequential)
                {
                    _queueIndex = (idx + 1) % count;
                }
                else
                {
                    _queueIndex = idx - 1;
                    if (_queueIndex < 0) _queueIndex = count - 1;
                }
            }

            // 快照为空时回退到当前剪贴板内容
            item ??= _currentData;

            if (item == null)
            {
                Logger.Warn("队列粘贴: 无可用的粘贴内容");
                return;
            }

            PasteItem(item, pasteMode);
        }

        /// <summary>
        /// 按指定模式粘贴历史条目内容。
        /// </summary>
        private void PasteItem(ClipboardData item, PasteMode mode)
        {
            if (item.Type == DataType.Text && mode == PasteMode.Keystrokes)
            {
                // 模拟键入文本（穿透远程桌面/VM 等不支持剪贴板同步的场景）
                PasteAsKeystrokes(item.TextContent ?? string.Empty);
                return;
            }

            // 普通模式或非文本内容：写入剪贴板后模拟 Ctrl+V 完成粘贴
            RestoreClipboardFromItem(item);
            SimulateCtrlV();
        }

        /// <summary>
        /// 模拟 Ctrl+V 粘贴（等待修饰键释放后执行，避免组合键冲突）。
        /// </summary>
        private void SimulateCtrlV()
        {
            Task.Run(async () =>
            {
                try
                {
                    // 等待所有修饰键释放
                    while (_ctrlPressed || _altPressed || _shiftPressed || _winPressed)
                    {
                        await Task.Delay(50);
                    }
                    await Task.Delay(80);

                    NativeInputSimulator.KeyDown(0x11);                 // VK_CONTROL
                    await Task.Delay(30);
                    NativeInputSimulator.KeyPress(0x56, false, 15);     // VK_V
                    NativeInputSimulator.KeyUp(0x11);                   // VK_CONTROL
                }
                catch (Exception ex)
                {
                    Logger.Error($"模拟 Ctrl+V 失败: {ex.Message}");
                }
            });
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
            uint lastSeq = GetClipboardSequenceNumber();

            while (GlobalData._isMonitoring)
            {
                try
                {
                    Thread.Sleep(100); // 适当降低频率以减少 CPU 占用

                    // Excel 编辑模式：缓冲的文本满 1 秒后发布到历史。
                    // 若期间图片已先入列表，文本将插到列表最前。
                    if (_pendingExcelText != null &&
                        (DateTime.Now - _excelTextBufferTime).TotalMilliseconds >= 1000)
                    {
                        var buffered = _pendingExcelText;
                        _pendingExcelText = null;
                        ClipboardChanged?.Invoke(buffered);
                    }

                    // 剪贴板变更序号：每次剪贴板内容变化时递增，
                    // 用于避免同一内容被反复捕获（尤其图片每次 GetImage 都是新对象）
                    uint seq = GetClipboardSequenceNumber();
                    bool seqChanged = seq != lastSeq;

                    // 检查剪贴板文本
                    if (System.Windows.Forms.Clipboard.ContainsText())
                    {
                        string currentText = System.Windows.Forms.Clipboard.GetText();
                        if (currentText != lastText)
                        {
                            lastText = currentText;

                            // Excel 编辑模式：若上一段缓冲文本尚未发布（1 秒内再次复制），先补发
                            if (_pendingExcelText != null)
                            {
                                var prev = _pendingExcelText;
                                _pendingExcelText = null;
                                ClipboardChanged?.Invoke(prev);
                            }

                            _currentData = new ClipboardData
                            {
                                Type = DataType.Text,
                                TextContent = currentText,
                                Timestamp = DateTime.Now
                            };
                            _currentData.Id = GenerateDataId(_currentData);

                            if (_settings.ExcelEditMode)
                            {
                                // 等待 1 秒让剪贴板所有格式完全写入；
                                // 期间若图片随后到达，图片先入列表，1 秒后文本入列表 → 文本保持在最前
                                _pendingExcelText = _currentData;
                                _excelTextBufferTime = DateTime.Now;
                                continue; // 继续轮询其他格式（图片），文本稍后发布
                            }

                            ClipboardChanged?.Invoke(_currentData);
                            continue; // 避免重复检查其他格式
                        }
                    }

                    // 检查剪贴板图像（仅当剪贴板序号变化时捕获，防止同一图片重复捕获）
                    if (seqChanged && System.Windows.Forms.Clipboard.ContainsImage())
                    {
                        var image = System.Windows.Forms.Clipboard.GetImage();
                        if (image != null)
                        {
                            lastSeq = seq;
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

                    // 检查剪贴板文件列表（仅当序号变化时捕获，防止同一文件列表重复捕获）
                    if (seqChanged && System.Windows.Forms.Clipboard.ContainsFileDropList())
                    {
                        var files = System.Windows.Forms.Clipboard.GetFileDropList();
                        var fileList = new List<string>();
                        foreach (string file in files)
                        {
                            fileList.Add(file);
                        }

                        lastSeq = seq;
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

                    // 本轮已完整检查所有格式，消费序号防止重复检查
                    if (seqChanged)
                    {
                        lastSeq = seq;
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

        private void RestoreClipboardFromItem(ClipboardData item)
        {
            if (item == null) return;

            try
            {
                switch (item.Type)
                {
                    case DataType.Text:
                        System.Windows.Forms.Clipboard.SetText(item.TextContent ?? string.Empty);
                        break;
                    case DataType.Image:
                        if (item.ImageContent != null)
                        {
                            var bitmap = ConvertToSystemDrawingBitmap(item.ImageContent);
                            System.Windows.Forms.Clipboard.SetImage(bitmap);
                        }
                        break;
                    case DataType.Files:
                        var collection = new System.Collections.Specialized.StringCollection();
                        collection.AddRange([.. item.FilePaths]);
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