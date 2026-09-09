using superClipboard;
using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace superClipboard
{
    public partial class ClipHistory : UserControl
    {
        private readonly LocalizationService _loc;

        public ClipHistory()
        {
            InitializeComponent();
            _loc = LocalizationService.Instance;
            BtnClearHistory.Content = _loc["history.clear_all"];
            HistoryListBox.ItemsSource = GlobalData.HistoryManager.HistoryItems;

            // 初始化粘贴队列模式按钮，并订阅模式改变事件
            UpdatePasteQueueButton(GlobalData._clipboardCore.QueueMode);
            GlobalData._clipboardCore.PasteQueueModeChanged += OnPasteQueueModeChanged;
        }

        /// <summary>
        /// 粘贴队列模式改变时刷新按钮文本。
        /// </summary>
        private void OnPasteQueueModeChanged(PasteQueueMode mode)
            => UpdatePasteQueueButton(mode);

        /// <summary>
        /// 按钮文本 = 当前模式：关闭 / 顺序粘贴 / 倒序粘贴。
        /// </summary>
        private void UpdatePasteQueueButton(PasteQueueMode mode)
        {
            BtnPasteQueueMode.Content = mode switch
            {
                PasteQueueMode.Sequential => _loc["history.paste_queue.sequential"],
                PasteQueueMode.Reverse => _loc["history.paste_queue.reverse"],
                _ => _loc["history.paste_queue.off"]
            };
        }

        /// <summary>
        /// 点击按钮循环切换：关闭 → 顺序粘贴 → 倒序粘贴 → 关闭。
        /// </summary>
        private void PasteQueueMode_Click(object sender, RoutedEventArgs e)
        {
            GlobalData._clipboardCore.CyclePasteQueueMode();
        }

        private void HistoryListBox_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (HistoryListBox.SelectedItem is ClipboardData selectedData)
            {
                // 打开预览窗口
                var previewWindow = new PreviewWindow(selectedData);
                previewWindow.Owner = Window.GetWindow(this);
                previewWindow.ShowDialog();
            }
        }

        private void ClearHistory_Click(object sender, RoutedEventArgs e)
        {
            GlobalData.HistoryManager.HistoryItems.Clear();
        }
        
        private void FavoriteButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is ClipboardData data)
            {
                // 切换收藏状态
                GlobalData.HistoryManager.ToggleFavorite(data);
                
                // 更新按钮文本（通过绑定会自动更新）
                // 强制刷新UI
                HistoryListBox.Items.Refresh();
            }
        }
    }

    // 数据类型 → 图标字符转换器
    public class DataTypeToIconConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is DataType type)
            {
                return type switch
                {
                    DataType.Text => "📄",
                    DataType.Image => "🖼️",
                    DataType.Files => "📁",
                    _ => "📋"
                };
            }
            return "📋";
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }

    // ClipboardData → 预览文本转换器
    public class ClipboardDataPreviewConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is ClipboardData data)
            {
                return data.Type switch
                {
                    DataType.Text => data.TextContent?.Length > 50
                                        ? data.TextContent.Substring(0, 50) + "..."
                                        : data.TextContent ?? "",
                    DataType.Image => "[图片]",
                    DataType.Files => $"{data.FilePaths?.Count ?? 0} 个文件",
                    _ => "[其他]"
                };
            }
            return "";
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }

    // 文件信息转换器
    public class FileInfoConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is ClipboardData data && data.Type == DataType.Files && data.FilePaths != null)
            {
                int fileCount = data.FilePaths.Count;
                if (fileCount == 0)
                    return "空文件列表";
                
                // 尝试获取第一个文件的信息
                string firstFile = data.FilePaths[0];
                string fileName = System.IO.Path.GetFileName(firstFile);
                
                if (fileCount == 1)
                {
                    // 单个文件，显示文件名和大小
                    try
                    {
                        var fileInfo = new System.IO.FileInfo(firstFile);
                        string size = FormatFileSize(fileInfo.Length);
                        return $"{fileName} ({size})";
                    }
                    catch
                    {
                        return fileName;
                    }
                }
                else
                {
                    // 多个文件
                    return $"{fileCount} 个文件: {fileName} 等";
                }
            }
            return string.Empty;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();

        private string FormatFileSize(long bytes)
        {
            string[] suffixes = { "B", "KB", "MB", "GB", "TB" };
            int counter = 0;
            decimal number = bytes;
            
            while (System.Math.Round(number / 1024) >= 1)
            {
                number /= 1024;
                counter++;
            }
            
            return $"{number:n2} {suffixes[counter]}";
        }
    }

    // 图片缩略图转换器
    public class ImageThumbnailConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is System.Windows.Media.Imaging.BitmapImage originalImage)
            {
                // 创建缩略图
                var thumbnail = new System.Windows.Media.Imaging.BitmapImage();
                thumbnail.BeginInit();
                thumbnail.DecodePixelWidth = 60; // 缩略图宽度
                thumbnail.DecodePixelHeight = 40; // 缩略图高度
                thumbnail.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                thumbnail.UriSource = originalImage.UriSource;
                thumbnail.EndInit();
                thumbnail.Freeze();
                return thumbnail;
            }
            return null;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }

    // 收藏图标转换器
    public class FavoriteIconConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is bool isFavorite)
            {
                return isFavorite ? "★" : "☆";
            }
            return "☆";
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}