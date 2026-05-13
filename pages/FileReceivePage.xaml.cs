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

        public FileReceivePage()
        {
            InitializeComponent();
            _keyboardHook = new KeyboardHookManager();
            _keyboardHook.OnKeyDown += OnKeyDown;
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

        private void OnKeyDown(Key key)
        {
            if (!_isListening) return;
            string? charStr = KeyToChar(key);
            if (charStr != null)
            {
                lock (_lockObject)
                {
                    _receivedText.Append(charStr);
                    _totalCharacters++;
                    _lastReceiveTime = DateTime.Now;
                }
                Dispatcher.Invoke(() => UpdatePreview());
            }
        }

        private string? KeyToChar(Key key)
        {
            if (key >= Key.A && key <= Key.Z)
            {
                bool shift = Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift);
                return shift ? key.ToString().ToUpper() : key.ToString().ToLower();
            }
            if (key >= Key.D0 && key <= Key.D9)
                return key.ToString().Substring(1);
            if (key >= Key.NumPad0 && key <= Key.NumPad9)
                return key.ToString().Substring(6);
            return key switch
            {
                Key.Space => " ",
                Key.Enter => "\r\n",
                Key.Tab => "\t",
                Key.OemPlus => Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift) ? "+" : "=",
                Key.OemMinus => Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift) ? "_" : "-",
                Key.OemQuestion => Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift) ? "?" : "/",
                Key.OemPeriod => Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift) ? ">" : ".",
                Key.OemComma => Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift) ? "<" : ",",
                Key.OemSemicolon => Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift) ? ":" : ";",
                Key.OemQuotes => Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift) ? "\"" : "'",
                Key.OemOpenBrackets => Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift) ? "{" : "[",
                Key.OemCloseBrackets => Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift) ? "}" : "]",
                Key.OemPipe => Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift) ? "|" : "\\",
                Key.OemTilde => Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift) ? "~" : "`",
                _ => null
            };
        }

        private void StartListeningButton_Click(object sender, RoutedEventArgs e)
        {
            _isListening = true;
            _receivedText.Clear();
            _receivedChunks = 0;
            _totalCharacters = 0;
            _decodedFilePath = null;
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
            TryDecodeReceivedData();
        }

        private void ClearCacheButton_Click(object sender, RoutedEventArgs e)
        {
            _receivedText.Clear();
            _receivedChunks = 0;
            _totalCharacters = 0;
            _decodedFilePath = null;
            SaveFileButton.IsEnabled = false;
            StatusText.Text = _loc["filereceive.status_ready"];
            PreviewTextBox.Text = "";
            ChunksText.Text = string.Format(_loc["filereceive.chunks_fmt"], 0, 0);
            CharactersText.Text = string.Format(_loc["filereceive.characters_fmt"], 0);
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

        private void UpdatePreview() { PreviewTextBox.Text = _receivedText.ToString(); }

        private void UpdateProgress()
        {
            ChunksText.Text = string.Format(_loc["filereceive.chunks_fmt"], _receivedChunks, _totalExpectedChunks);
            CharactersText.Text = string.Format(_loc["filereceive.characters_fmt"], _totalCharacters);
            if (_totalExpectedChunks > 0)
            {
                ReceiveProgressBar.Value = (double)_receivedChunks / _totalExpectedChunks * 100;
                ProgressPercentText.Text = $"{(int)((double)_receivedChunks / _totalExpectedChunks * 100)}%";
            }
        }

        private void TryDecodeReceivedData()
        {
            if (_receivedText.Length == 0)
                return;
            try
            {
                string receivedText = _receivedText.ToString();
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
                string fileName = $"received_file_{timestamp}.bin";
                if (mode == 2) fileName = $"received_text_{timestamp}.txt";
                _decodedFilePath = Path.Combine(_saveDirectory!, fileName);
                File.WriteAllBytes(_decodedFilePath, decodedData);
                SaveFileButton.IsEnabled = true;
                StatusText.Text = _loc["filereceive.status_complete"];
                if (ShowNotificationsCheckBox.IsChecked == true)
                    ShowNotification(_loc["filereceive.decode_complete"], $"{_loc["filereceive.decode_success"]}: {fileName}", System.Windows.Forms.ToolTipIcon.Info);
            }
            catch (Exception)
            {
            }
        }

        private void ShowNotification(string title, string text, System.Windows.Forms.ToolTipIcon icon)
        {
            var notifyIcon = new System.Windows.Forms.NotifyIcon { Visible = true, BalloonTipTitle = title, BalloonTipText = text, BalloonTipIcon = icon };
            notifyIcon.ShowBalloonTip(3000);
            notifyIcon.Dispose();
        }
    }
}
