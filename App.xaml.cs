using Hardcodet.Wpf.TaskbarNotification;
using System;
using System.Configuration;
using System.Data;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Forms;
using Wpf.Ui;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;


namespace superClipboard
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : System.Windows.Application
    {
        private TaskbarIcon _notifyIcon;
        private static Mutex _mutex;
        private string appName = "Super_Clipboard_权限申请";
        protected override void OnStartup(StartupEventArgs e)
        {
            GlobalData.key_settings.Load();
            GlobalData.key_settings.ReflashTheme();
            // 检查是否需要管理员权限
            if (NeedAdminPrivileges(e.Args))
            {
                UacHelper.RequireAdminOnStartAsync();
            }

                base.OnStartup(e);
        }

        private bool NeedAdminPrivileges(string[] args)
        {
            // 这里添加你的逻辑判断是否需要管理员权限
            return true; // 或根据条件返回
        }

  
        private void Application_Startup(object sender, StartupEventArgs e)
        {
            
            bool createdNew;

            // 尝试创建互斥体
            _mutex = new Mutex(true, appName, out createdNew);
            
            if (!createdNew)
            {
                var messageBox = new Wpf.Ui.Controls.MessageBox
                {
                    Title = "提示",                  // 窗口标题（继承自 Window）
                    Content = "您已经打开该应用程序，请在通知栏中寻找",        // 消息内容（可放置任何 UI 元素）

                    // 主按钮（通常是确认/是）

                    CloseButtonAppearance = ControlAppearance.Primary,
                    CloseButtonIcon = new SymbolIcon(SymbolRegular.Checkmark12),
                    CloseButtonText = "确认",
                    // 是否在标题栏显示标题
                    ShowTitle = true
                };
                messageBox.ShowDialogAsync(showAsDialog: true);
                System.Windows.Application.Current.Shutdown();
                return;
            }

            if (UacHelper.IsRunAsAdmin())
            {
                appName = "Super_Clipboard";
                _notifyIcon = new TaskbarIcon();
                var assembly = Assembly.GetExecutingAssembly();
                Stream stream = assembly.GetManifestResourceStream("superClipboard.Resources.favicon.ico");
                if (stream != null)
                {
                    _notifyIcon.Icon = new Icon(stream);
                }
                else
                {
                    // 处理资源未找到的情况（例如使用默认图标或抛出异常）
                    // 这里简单输出错误信息
                    System.Diagnostics.Debug.WriteLine("无法找到嵌入资源");
                    // 也可以使用备用图标
                }
                _notifyIcon.ToolTipText = "剪贴板应用";

                // 设置双击行为
                _notifyIcon.DoubleClickCommand = new RelayCommand(ShowMainWindow);
                // 可选：右键菜单（用 XAML 或代码定义）
                // ... 添加菜单项
                var contextMenu = new ContextMenu();

                // “打开”菜单项（功能与双击相同）
                var openMenuItem = new Wpf.Ui.Controls.MenuItem { Header = "打开" };
                openMenuItem.Click += (s, args) => ShowMainWindow();

                // “退出”菜单项
                var exitMenuItem = new Wpf.Ui.Controls.MenuItem { Header = "退出" };
                exitMenuItem.Click += (s, args) =>
                {
                    // 清理托盘图标资源（避免残留）
                    _notifyIcon.Dispose();
                    // 彻底退出应用
                    System.Windows.Application.Current.Shutdown();
                };

                contextMenu.Items.Add(openMenuItem);
                contextMenu.Items.Add(exitMenuItem);
                _notifyIcon.ContextMenu = contextMenu;

                // 启动时不显示主窗口
                MainWindow = new MainWindow();
                GlobalData._clipboardCore.StartMonitoring();
            }
            // 创建托盘图标（可以直接在 XAML 中定义，但为了灵活，用代码创建）
            
            // 不调用 Show()，窗口默认不显示
        }

        private void ShowMainWindow()
        {
            MainWindow ??= new MainWindow();
            if (MainWindow.WindowState == WindowState.Minimized)
            {
                MainWindow.WindowState = WindowState.Normal;
            }
            MainWindow.Show();
            MainWindow.Activate();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            GlobalData._clipboardCore.StopMonitoring(); // 停止剪贴板监控
            GlobalData._clipboardCore?.Dispose();
            _notifyIcon?.Dispose(); // 释放托盘图标资源
            base.OnExit(e);
        }
    }

    // 简单的 RelayCommand 实现（或使用已有 MVVM 框架）
    public class RelayCommand : System.Windows.Input.ICommand
    {
        private readonly Action _execute;
        public RelayCommand(Action execute)
        {
            _execute = execute;
        }

        public event System.EventHandler CanExecuteChanged;
        public bool CanExecute(object parameter) => true;
        public void Execute(object parameter) => _execute();
    }
    internal static class NativeMethods
    {
        public const int SW_RESTORE = 9;

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        public static extern bool SetForegroundWindow(IntPtr hWnd);
    }
}

