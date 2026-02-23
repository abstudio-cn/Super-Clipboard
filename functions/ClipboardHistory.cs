using superClipboard;
using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Threading;

namespace superClipboard
{
    /// <summary>
    /// 剪贴板历史记录管理器（核心）
    /// </summary>
    public class ClipboardHistoryManager
    {
        private readonly object _lock = new();
        private bool _ignoreNextChange = false;
        private const int MaxHistoryCount = 50;

        /// <summary>
        /// 历史记录集合（UI可绑定）
        /// </summary>
        public ObservableCollection<ClipboardData> HistoryItems { get; } = [];

        public ClipboardHistoryManager()
        {
            // 订阅全局剪贴板变化事件
            GlobalData._clipboardCore.ClipboardChanged += OnClipboardChanged;
        }

        private void OnClipboardChanged(ClipboardData data)
        {
            // 必须在UI线程更新集合
            Application.Current.Dispatcher.BeginInvoke(new Action(() =>
            {
                lock (_lock)
                {
                    if (_ignoreNextChange)
                    {
                        // 由自身设置触发，忽略并重置标志
                        _ignoreNextChange = false;
                        return;
                    }
                }
                RemoveDuplicates(data);
                // 添加到历史（最新在前）
                HistoryItems.Insert(0, data);

                // 限制数量
                while (HistoryItems.Count > MaxHistoryCount)
                    HistoryItems.RemoveAt(HistoryItems.Count - 1);

            }), DispatcherPriority.Background);
        }

        private void RemoveDuplicates(ClipboardData newData)
        {
            lock (_lock)
            {
                // 从后往前遍历，避免索引变化问题
                for (int i = HistoryItems.Count - 1; i >= 0; i--)
                {
                    var existing = HistoryItems[i];
                    if (IsDuplicate(newData, existing))
                    {
                        HistoryItems.RemoveAt(i);
                    }
                }
            }
        }

        /// <summary>
        /// 判断两个剪贴板数据是否代表相同内容
        /// </summary>
        private bool IsDuplicate(ClipboardData a, ClipboardData b)
        {
            if (a.Type != b.Type) return false;

            switch (a.Type)
            {
                case DataType.Text:
                    return a.TextContent == b.TextContent;

                case DataType.Files:
                    // 比较文件路径集合（忽略顺序）
                    if (a.FilePaths == null || b.FilePaths == null)
                        return false;

                    var setA = new HashSet<string>(a.FilePaths);
                    var setB = new HashSet<string>(b.FilePaths);
                    return setA.SetEquals(setB);

                case DataType.Image:
                    // 图片内容比对复杂，暂不实现去重
                    return false;

                default:
                    return false;
            }
        }

        /// <summary>
        /// 将指定历史条目设置为当前剪贴板内容
        /// </summary>
        public void SetClipboardFromHistory(ClipboardData data)
        {
            if (!Application.Current.Dispatcher.CheckAccess())
            {
                Application.Current.Dispatcher.Invoke(() => SetClipboardFromHistory(data));
                return;
            }

            lock (_lock)
            {
                _ignoreNextChange = true;
            }

            try
            {
                SetClipboardData(data);
                // 启动一个定时器，防止事件未触发导致标志残留
                StartIgnoreResetTimer();
            }
            catch
            {
                lock (_lock)
                {
                    _ignoreNextChange = false;
                }
            }
        }

        private void SetClipboardData(ClipboardData data)
        {
            switch (data.Type)
            {
                case DataType.Text:
                    Clipboard.SetText(data.TextContent);
                    break;
                case DataType.Image:
                    Clipboard.SetImage(data.ImageContent);
                    break;
                case DataType.Files:
                    var fileList = new StringCollection();
                    fileList.AddRange([.. data.FilePaths]);
                    Clipboard.SetFileDropList(fileList);
                    break;
                    // 其他类型可在此扩展
            }
        }

        private DispatcherTimer _resetTimer;
        private void StartIgnoreResetTimer()
        {
            if (_resetTimer != null)
            {
                _resetTimer.Stop();
                _resetTimer = null;
            }

            _resetTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(500)
            };
            _resetTimer.Tick += (s, e) =>
            {
                _resetTimer.Stop();
                lock (_lock)
                {
                    if (_ignoreNextChange)
                        _ignoreNextChange = false;
                }
            };
            _resetTimer.Start();
        }
    }
}