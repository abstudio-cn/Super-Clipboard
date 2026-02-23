using superClipboard;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;

namespace superClipboard
{
    public static class GlobalData
    {
        public static readonly ClipboardCore _clipboardCore = new();
        public static bool _isMonitoring = false;
        public static ClipboardHistoryManager HistoryManager { get; } = new ClipboardHistoryManager();
    }

    public class ClipboardData
    {
        public DataType Type { get; set; }
        public string TextContent { get; set; }
        public BitmapImage ImageContent { get; set; }
        public List<string> FilePaths { get; set; }
        public DateTime Timestamp { get; set; }
    }

    public enum DataType
    {
        Text,
        Image,
        Files,
        Other
    }
}
