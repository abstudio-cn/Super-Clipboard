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
            // 检查是否需要管理员权限
            if (NeedAdminPrivileges(e.Args))
            {
                UacHelper.RequireAdminOnStart();
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
            if (UacHelper.IsRunAsAdmin()) {
                appName = "Super_Clipboard";
            }
            // 尝试创建互斥体
            _mutex = new Mutex(true, appName, out createdNew);
            
            if (!createdNew)
            {
                var result = System.Windows.MessageBox.Show(  "您已经打开该应用程序，请在任务栏中寻找",  "提示",  MessageBoxButton.OK,  MessageBoxImage.Information);
                // 已有实例运行，激活该实例并退出当前启动
                System.Windows.Application.Current.Shutdown();
                return;
            }

            // 创建托盘图标（可以直接在 XAML 中定义，但为了灵活，用代码创建）
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
            var openMenuItem = new MenuItem { Header = "打开" };
            openMenuItem.Click += (s, args) => ShowMainWindow();

            // “退出”菜单项
            var exitMenuItem = new MenuItem { Header = "退出" };
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

