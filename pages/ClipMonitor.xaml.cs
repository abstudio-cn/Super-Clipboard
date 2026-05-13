using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace superClipboard
{
    public partial class ClipMonitor : UserControl
    {
        CancellationTokenSource cts = new CancellationTokenSource();
        private readonly LocalizationService _loc;

        public ClipMonitor()
        {
            InitializeComponent();
            _loc = LocalizationService.Instance;
            ApplyLocalization();
            GlobalData._clipboardCore.ClipboardChanged += OnClipboardChanged;
            Loaded += (s, e) =>
            {
                var data = GlobalData._clipboardCore.CurrentData;
                if (data != null) UpdateUI(data);
            };
        }

        private void ApplyLocalization()
        {
            LblShortcutHint.Text = _loc["monitor.shortcut_hint"];
            LblShortcutNormal.Text = _loc["monitor.shortcut_normal"];
            LblShortcutSimulate.Text = _loc["monitor.shortcut_simulate"];
            LblCurrentClipboard.Text = _loc["monitor.current"];
            NoContentText.Text = _loc["monitor.empty"];
        }

        private void OnClipboardChanged(ClipboardData data)
        {
            Dispatcher.BeginInvoke(() =>
            {
                if (!cts.Token.IsCancellationRequested) UpdateUI(data);
            }, DispatcherPriority.Background);
        }

        private void UpdateUI(ClipboardData data)
        {
            DataTypeText.Text = string.Format(_loc["monitor.data_type"],
                data.Type == DataType.Text ? _loc["monitor.type_text"] :
                data.Type == DataType.Image ? _loc["monitor.type_image"] :
                data.Type == DataType.Files ? _loc["monitor.type_files"] : data.Type.ToString());
            TimeText.Text = string.Format(_loc["monitor.time"], data.Timestamp.ToString("HH:mm:ss"));

            TextContentBox.Visibility = Visibility.Collapsed;
            ImageContentBox.Visibility = Visibility.Collapsed;
            FileListBox.Visibility = Visibility.Collapsed;
            NoContentText.Visibility = Visibility.Visible;

            switch (data.Type)
            {
                case DataType.Text:
                    PreviewText.Text = string.Format(_loc["monitor.preview"],
                        data.TextContent.Substring(0, Math.Min(50, data.TextContent.Length)) + "...");
                    TextContentBox.Text = data.TextContent;
                    TextContentBox.Visibility = Visibility.Visible;
                    NoContentText.Visibility = Visibility.Collapsed;
                    break;

                case DataType.Image:
                    PreviewText.Text = _loc["monitor.preview_image"];
                    ImageContentBox.Source = data.ImageContent;
                    ImageContentBox.Visibility = Visibility.Visible;
                    NoContentText.Visibility = Visibility.Collapsed;
                    break;

                case DataType.Files:
                    PreviewText.Text = string.Format(_loc["monitor.preview_files"], data.FilePaths.Count);
                    FileListBox.ItemsSource = data.FilePaths;
                    FileListBox.Visibility = Visibility.Visible;
                    NoContentText.Visibility = Visibility.Collapsed;
                    break;

                default:
                    PreviewText.Text = _loc["monitor.preview_unsupported"];
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
