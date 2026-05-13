using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using Wpf.Ui.Controls;

namespace superClipboard
{
    public partial class PreviewWindow : FluentWindow
    {
        private ClipboardData _data;
        
        public PreviewWindow(ClipboardData data)
        {
            InitializeComponent();
            _data = data;
            LoadData();
        }
        
        private void ResetVisibility()
        {
            TextContentBox.Visibility = Visibility.Collapsed;
            ImageContentBox.Visibility = Visibility.Collapsed;
            FileListBox.Visibility = Visibility.Collapsed;
            UnknownContentText.Visibility = Visibility.Collapsed;
        }
        
        private void LoadData()
        {
            // 重置所有内容控件的可见性
            ResetVisibility();
            
            // 设置标题和时间戳
            TitleText.Text = $"剪贴板内容预览 - {_data.Type}";
            TimestampText.Text = _data.Timestamp.ToString("yyyy-MM-dd HH:mm:ss");
            
            switch (_data.Type)
            {
                case DataType.Text:
                    ShowTextContent();
                    break;
                case DataType.Image:
                    ShowImageContent();
                    break;
                case DataType.Files:
                    ShowFileContent();
                    break;
                default:
                    ShowUnknownContent();
                    break;
            }
        }
        
        private void ShowTextContent()
        {
            TextContentBox.Text = _data.TextContent ?? string.Empty;
            TextContentBox.Visibility = Visibility.Visible;
        }
        
        private void ShowImageContent()
        {
            if (_data.ImageContent != null)
            {
                ImageContentBox.Source = _data.ImageContent;
                ImageContentBox.Visibility = Visibility.Visible;
            }
            else
            {
                ShowUnknownContent();
            }
        }
        
        private void ShowFileContent()
        {
            if (_data.FilePaths == null || _data.FilePaths.Count == 0)
            {
                ShowUnknownContent();
                return;
            }
            
            // 如果只有一个文件，尝试预览内容
            if (_data.FilePaths.Count == 1)
            {
                string filePath = _data.FilePaths[0];
                if (TryPreviewSingleFile(filePath))
                {
                    return; // 预览成功
                }
            }
            
            // 多个文件或无法预览单个文件，显示文件列表
            ShowFileList();
        }
        
        private bool TryPreviewSingleFile(string filePath)
        {
            try
            {
                if (!File.Exists(filePath))
                    return false;
                    
                string extension = Path.GetExtension(filePath).ToLower();
                
                // 文本文件预览
                if (IsTextFile(extension))
                {
                    string content = File.ReadAllText(filePath, System.Text.Encoding.UTF8);
                    TextContentBox.Text = content;
                    TextContentBox.Visibility = Visibility.Visible;
                    TitleText.Text = $"文本文件预览 - {Path.GetFileName(filePath)}";
                    return true;
                }
                
                // 图片文件预览
                if (IsImageFile(extension))
                {
                    var bitmapImage = new BitmapImage();
                    bitmapImage.BeginInit();
                    bitmapImage.CacheOption = BitmapCacheOption.OnLoad;
                    bitmapImage.UriSource = new Uri(filePath);
                    bitmapImage.EndInit();
                    bitmapImage.Freeze();
                    
                    ImageContentBox.Source = bitmapImage;
                    ImageContentBox.Visibility = Visibility.Visible;
                    TitleText.Text = $"图片预览 - {Path.GetFileName(filePath)}";
                    return true;
                }
            }
            catch
            {
                // 预览失败，回退到文件列表
            }
            
            return false;
        }
        
        private bool IsTextFile(string extension)
        {
            string[] textExtensions = { ".txt", ".log", ".json", ".xml", ".csv", ".yml", ".yaml", ".md", ".js", ".cs", ".java", ".cpp", ".h", ".html", ".css", ".py", ".rb", ".php", ".sql", ".config", ".ini" };
            return Array.Exists(textExtensions, ext => ext.Equals(extension));
        }
        
        private bool IsImageFile(string extension)
        {
            string[] imageExtensions = { ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".tiff", ".ico", ".webp" };
            return Array.Exists(imageExtensions, ext => ext.Equals(extension));
        }
        
        private void ShowFileList()
        {
            var fileItems = new List<FileItemViewModel>();
            foreach (var filePath in _data.FilePaths)
            {
                try
                {
                    var fileInfo = new FileInfo(filePath);
                    fileItems.Add(new FileItemViewModel
                    {
                        Name = fileInfo.Name,
                        FullPath = fileInfo.FullName,
                        Size = fileInfo.Length,
                        SizeFormatted = FormatFileSize(fileInfo.Length)
                    });
                }
                catch
                {
                    fileItems.Add(new FileItemViewModel
                    {
                        Name = Path.GetFileName(filePath),
                        FullPath = filePath,
                        Size = -1,
                        SizeFormatted = "未知"
                    });
                }
            }
            
            FileListBox.ItemsSource = fileItems;
            FileListBox.Visibility = Visibility.Visible;
        }
        
        private void ShowUnknownContent()
        {
            UnknownContentText.Text = $"无法预览此类型的内容: {_data.Type}";
            UnknownContentText.Visibility = Visibility.Visible;
        }
        
        private string FormatFileSize(long bytes)
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
        
        private void CopyButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                GlobalData.HistoryManager.SetClipboardFromHistory(_data);
            }
            catch (Exception)
            {
            }
        }
        
        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
    
    public class FileItemViewModel
    {
        public string Name { get; set; }
        public string FullPath { get; set; }
        public long Size { get; set; }
        public string SizeFormatted { get; set; }
    }
}