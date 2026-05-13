using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Principal;
using System.Windows;
using Wpf.Ui.Controls;

public static class UacHelper
{

    // 检查当前是否以管理员身份运行
    public static bool IsRunAsAdmin()
    {
        var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    // 获取正确的可执行文件路径（用于以管理员权限重启）
    public static string GetExecutablePath()
    {
        // 优先使用 Environment.ProcessPath（.NET Core 3.0+），
        // 在单文件/自包含发布中也能正确返回 exe 路径
        string location = Environment.ProcessPath
            ?? Process.GetCurrentProcess().MainModule?.FileName
            ?? Assembly.GetEntryAssembly().Location;

        if (string.IsNullOrEmpty(location))
            return GetDotnetPath();

        string extension = Path.GetExtension(location).ToLowerInvariant();

        // 如果是 .exe，直接返回（独立部署或单文件发布）
        if (extension == ".exe")
            return location;

        // 如果是 .dll，说明是框架依赖的部署
        if (extension == ".dll")
        {
            // 尝试查找同名的 .exe 文件（例如独立发布时生成的 .exe）
            string possibleExe = Path.ChangeExtension(location, ".exe");
            if (File.Exists(possibleExe))
                return possibleExe;

            // 如果不存在 .exe，则需要通过 dotnet.exe 启动
            return GetDotnetPath();
        }

        // 其他情况（理论上不会发生），直接返回原路径
        return location;
    }

    // 获取 dotnet.exe 的完整路径
    private static string GetDotnetPath()
    {
        // 优先使用 DOTNET_ROOT 环境变量
        string dotnetRoot = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        if (!string.IsNullOrEmpty(dotnetRoot))
        {
            string dotnetExe = Path.Combine(dotnetRoot, "dotnet.exe");
            if (File.Exists(dotnetExe))
                return dotnetExe;
        }

        // 在系统 PATH 中查找 dotnet.exe
        string pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrEmpty(pathEnv))
        {
            foreach (string path in pathEnv.Split(Path.PathSeparator))
            {
                string fullPath = Path.Combine(path, "dotnet.exe");
                if (File.Exists(fullPath))
                    return fullPath;
            }
        }

        // 如果找不到，回退到 "dotnet"（让系统在 PATH 中查找）
        return "dotnet";
    }

    // 以管理员身份重新启动应用程序
    public static async Task RestartAsAdminAsync()
    {
        string executablePath = GetExecutablePath();
        string entryDll = Assembly.GetEntryAssembly().Location; // 原始的 DLL 路径

        ProcessStartInfo startInfo = new ProcessStartInfo
        {
            UseShellExecute = true, // 必须为 true 才能使用 runas
            Verb = "runas"          // 请求管理员权限
        };

        // 判断是否需要通过 dotnet 启动
        if (Path.GetFileName(executablePath).Equals("dotnet", StringComparison.OrdinalIgnoreCase) ||
            Path.GetFileName(executablePath).Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase))
        {
            startInfo.FileName = executablePath;
            startInfo.Arguments = $"\"{entryDll}\""; // 将 DLL 作为参数传递给 dotnet
        }
        else
        {
            startInfo.FileName = executablePath; // 直接运行 .exe
        }

        try
        {
            Process.Start(startInfo);
        }
        catch (Exception)
        {
            // 静默失败
        }
    }

    // 在程序启动时调用此方法，如果需要管理员权限则自动重启
    public static async Task RequireAdminOnStartAsync()
    {
        if (!IsRunAsAdmin())
        {
            try
            {
                var loc = superClipboard.LocalizationService.Instance;
                var messageBox = new Wpf.Ui.Controls.MessageBox
                {
                    Title = loc["admin.title"],
                    Content = loc["admin.content"],

                    PrimaryButtonText = loc["common.yes"],
                    PrimaryButtonIcon = new SymbolIcon(SymbolRegular.Checkmark24),
                    PrimaryButtonAppearance = ControlAppearance.Primary,
                    IsPrimaryButtonEnabled = true,

                    CloseButtonAppearance = ControlAppearance.Secondary,
                    CloseButtonIcon = new SymbolIcon(SymbolRegular.Dismiss24),
                    CloseButtonText = loc["common.no"],

                    ShowTitle = true
                };
                Wpf.Ui.Controls.MessageBoxResult result = await messageBox.ShowDialogAsync(showAsDialog: true);

                if (result == Wpf.Ui.Controls.MessageBoxResult.Primary)
                {
                    await RestartAsAdminAsync();
                    // 当前非管理员进程启动新管理员进程后退出
                    Application.Current.Dispatcher.Invoke(() => Application.Current.Shutdown());
                }
                else
                {
                    Application.Current.Dispatcher.Invoke(() => Application.Current.Shutdown());
                }
            }
            catch (Exception)
            {
                Application.Current.Dispatcher.Invoke(() => Application.Current.Shutdown());
            }
        }
    }
}