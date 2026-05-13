using superClipboard;
using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Media.Imaging;
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
            
            // 加载收藏项
            LoadFavorites();
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
        /// 计算图片的部分哈希值（截取前后2KB数据）
        /// </summary>
        private string GetImagePartialHash(BitmapImage image)
        {
            if (image == null) return string.Empty;

            try
            {
                // 将BitmapImage编码为PNG字节数组
                byte[] imageBytes;
                using (var memoryStream = new MemoryStream())
                {
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(image));
                    encoder.Save(memoryStream);
                    imageBytes = memoryStream.ToArray();
                }

                // 如果图片数据小于4KB，直接使用全部数据进行哈希
                if (imageBytes.Length <= 4096)
                {
                    using (var sha256 = SHA256.Create())
                    {
                        byte[] hashBytes = sha256.ComputeHash(imageBytes);
                        return Convert.ToBase64String(hashBytes);
                    }
                }

                // 截取前后2KB左右的数据
                int sampleSize = 2048; // 2KB
                int totalSize = imageBytes.Length;
                
                // 计算实际采样大小（避免超出范围）
                int frontSize = Math.Min(sampleSize, totalSize);
                int rearSize = Math.Min(sampleSize, totalSize - frontSize);
                
                // 创建采样数据：前2KB + 后2KB
                byte[] sampleData = new byte[frontSize + rearSize];
                
                // 复制前部数据
                Array.Copy(imageBytes, 0, sampleData, 0, frontSize);
                
                // 复制后部数据（如果后部有足够数据）
                if (rearSize > 0)
                {
                    Array.Copy(imageBytes, totalSize - rearSize, sampleData, frontSize, rearSize);
                }

                // 计算哈希
                using (var sha256 = SHA256.Create())
                {
                    byte[] hashBytes = sha256.ComputeHash(sampleData);
                    return Convert.ToBase64String(hashBytes);
                }
            }
            catch
            {
                // 如果计算失败，返回空字符串（视为不重复）
                return string.Empty;
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
                    // 使用图片部分哈希进行比较
                    if (a.ImageContent == null || b.ImageContent == null)
                        return false;
                    
                    string hashA = GetImagePartialHash(a.ImageContent);
                    string hashB = GetImagePartialHash(b.ImageContent);
                    
                    // 如果任一哈希计算失败（返回空字符串），则不认为是重复
                    if (string.IsNullOrEmpty(hashA) || string.IsNullOrEmpty(hashB))
                        return false;
                    
                    return hashA == hashB;

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
        
        /// <summary>
        /// 加载收藏项到历史记录
        /// </summary>
        private void LoadFavorites()
        {
            try
            {
                using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\clipboardManager"))
                {
                    if (key == null) return;
                    
                    foreach (string valueName in key.GetValueNames())
                    {
                        try
                        {
                            string serializedData = key.GetValue(valueName) as string;
                            if (!string.IsNullOrEmpty(serializedData))
                            {
                                var data = DeserializeClipboardData(serializedData);
                                if (data != null)
                                {
                                    data.IsFavorite = true;
                                    data.Id = valueName;
                                    
                                    // 添加到历史记录（如果不存在）
                                    bool exists = false;
                                    foreach (var item in HistoryItems)
                                    {
                                        if (item.Id == data.Id)
                                        {
                                            item.IsFavorite = true;
                                            exists = true;
                                            break;
                                        }
                                    }
                                    
                                    if (!exists)
                                    {
                                        HistoryItems.Insert(0, data);
                                    }
                                }
                            }
                        }
                        catch
                        {
                            // 忽略无法解析的数据
                        }
                    }
                }
            }
            catch
            {
                // 注册表访问失败
            }
        }
        
        /// <summary>
        /// 切换收藏状态
        /// </summary>
        public void ToggleFavorite(ClipboardData data)
        {
            if (data == null) return;
            
            // 确保数据有ID
            if (string.IsNullOrEmpty(data.Id))
            {
                data.Id = GenerateDataId(data);
            }
            
            if (data.IsFavorite)
            {
                // 取消收藏
                RemoveFavoriteFromRegistry(data.Id);
                data.IsFavorite = false;
            }
            else
            {
                // 添加收藏
                SaveFavoriteToRegistry(data);
                data.IsFavorite = true;
            }
        }
        
        /// <summary>
        /// 保存收藏到注册表
        /// </summary>
        private void SaveFavoriteToRegistry(ClipboardData data)
        {
            try
            {
                using (var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\clipboardManager"))
                {
                    if (key == null) return;
                    
                    string serializedData = SerializeClipboardData(data);
                    key.SetValue(data.Id, serializedData);
                }
            }
            catch
            {
                // 保存失败
            }
        }
        
        /// <summary>
        /// 从注册表中删除收藏
        /// </summary>
        private void RemoveFavoriteFromRegistry(string id)
        {
            try
            {
                using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\clipboardManager", true))
                {
                    if (key == null) return;
                    
                    key.DeleteValue(id, false);
                }
            }
            catch
            {
                // 删除失败
            }
        }
        
        /// <summary>
        /// 生成ClipboardData的唯一ID
        /// </summary>
        private string GenerateDataId(ClipboardData data)
        {
            // 使用时间戳和内容哈希生成唯一ID
            var sb = new System.Text.StringBuilder();
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
                    contentHash = ComputeHash(string.Join("|", data.FilePaths ?? new System.Collections.Generic.List<string>()));
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
                byte[] hashBytes = sha256.ComputeHash(System.Text.Encoding.UTF8.GetBytes(input));
                return Convert.ToBase64String(hashBytes).Replace("=", "").Replace("/", "_").Replace("+", "-");
            }
        }
        
        /// <summary>
        /// 序列化ClipboardData为字符串
        /// </summary>
        private string SerializeClipboardData(ClipboardData data)
        {
            // 简单序列化：类型|时间戳|内容
            var sb = new System.Text.StringBuilder();
            sb.Append((int)data.Type);
            sb.Append("|");
            sb.Append(data.Timestamp.ToString("O"));
            sb.Append("|");
            
            switch (data.Type)
            {
                case DataType.Text:
                    sb.Append(EscapeForSerialization(data.TextContent ?? ""));
                    break;
                case DataType.Image:
                    // 图片无法完全序列化，只存储元数据
                    sb.Append("[Image]");
                    break;
                case DataType.Files:
                    sb.Append(EscapeForSerialization(string.Join(";;", data.FilePaths ?? new System.Collections.Generic.List<string>())));
                    break;
                default:
                    sb.Append("[Unknown]");
                    break;
            }
            
            return sb.ToString();
        }
        
        /// <summary>
        /// 从字符串反序列化ClipboardData
        /// </summary>
        private ClipboardData DeserializeClipboardData(string serializedData)
        {
            try
            {
                string[] parts = serializedData.Split('|');
                if (parts.Length < 3) return null;
                
                var data = new ClipboardData
                {
                    Type = (DataType)int.Parse(parts[0]),
                    Timestamp = DateTime.Parse(parts[1]),
                    Id = "",
                    IsFavorite = true
                };
                
                string content = UnescapeFromSerialization(parts[2]);
                
                switch (data.Type)
                {
                    case DataType.Text:
                        data.TextContent = content;
                        break;
                    case DataType.Files:
                        data.FilePaths = new System.Collections.Generic.List<string>(content.Split(new[] { ";;" }, System.StringSplitOptions.RemoveEmptyEntries));
                        break;
                    // 图片和其他类型无法完全恢复，只存储元数据
                }
                
                return data;
            }
            catch
            {
                return null;
            }
        }
        
        private string EscapeForSerialization(string input)
        {
            return input.Replace("|", "\\p").Replace("\\", "\\\\");
        }
        
        private string UnescapeFromSerialization(string input)
        {
            return input.Replace("\\\\", "\\").Replace("\\p", "|");
        }
    }
}