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
        public ClipHistory()
        {
            InitializeComponent();
            // 绑定历史数据源
            HistoryListBox.ItemsSource = GlobalData.HistoryManager.HistoryItems;
        }

        private void HistoryListBox_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (HistoryListBox.SelectedItem is ClipboardData selectedData)
            {
                GlobalData.HistoryManager.SetClipboardFromHistory(selectedData);
            }
        }

        private void ClearHistory_Click(object sender, RoutedEventArgs e)
        {
            GlobalData.HistoryManager.HistoryItems.Clear();
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
}