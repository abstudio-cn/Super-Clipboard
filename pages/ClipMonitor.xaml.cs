using System;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace superClipboard
{
    public partial class ClipMonitor : UserControl
    {
        CancellationTokenSource cts = new CancellationTokenSource();

        public ClipMonitor()
        {
            InitializeComponent();
            GlobalData._clipboardCore.ClipboardChanged += OnClipboardChanged;
        }

        private void OnClipboardChanged(ClipboardData data)
        {
            Dispatcher.BeginInvoke(() =>
            {
                if (!cts.Token.IsCancellationRequested)
                {
                    UpdateUI(data); 
                }
                else
                {
                    return;
                }
            }, DispatcherPriority.Background);
        }

        private void UpdateUI(ClipboardData data)
        {
            // 更新信息面板
            DataTypeText.Text = $"数据类型：{data.Type}";
            TimeText.Text = $"时间：{data.Timestamp:HH:mm:ss}";

            // 清空所有内容显示
            TextContentBox.Visibility = Visibility.Collapsed;
            ImageContentBox.Visibility = Visibility.Collapsed;
            FileListBox.Visibility = Visibility.Collapsed;
            NoContentText.Visibility = Visibility.Visible;

            // 根据数据类型显示内容
            switch (data.Type)
            {
                case DataType.Text:
                    PreviewText.Text = $"预览：{data.TextContent.Substring(0, Math.Min(50, data.TextContent.Length))}...";
                    TextContentBox.Text = data.TextContent;
                    TextContentBox.Visibility = Visibility.Visible;
                    NoContentText.Visibility = Visibility.Collapsed;
                    break;

                case DataType.Image:
                    PreviewText.Text = "预览：[图片]";
                    ImageContentBox.Source = data.ImageContent;
                    ImageContentBox.Visibility = Visibility.Visible;
                    NoContentText.Visibility = Visibility.Collapsed;
                    break;

                case DataType.Files:
                    PreviewText.Text = $"预览：{data.FilePaths.Count} 个文件";
                    FileListBox.ItemsSource = data.FilePaths;
                    FileListBox.Visibility = Visibility.Visible;
                    NoContentText.Visibility = Visibility.Collapsed;
                    break;

                default:
                    PreviewText.Text = "预览：不支持的内容类型";
                    break;
            }
        }

        private void UserControl_Unloaded(object sender, RoutedEventArgs e)
        {
            GlobalData._clipboardCore.ClipboardChanged -= OnClipboardChanged;
            cts.Cancel();
        }

    }
}