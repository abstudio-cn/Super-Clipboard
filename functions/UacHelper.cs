using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Principal;
using System.Windows;

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
    private static string GetExecutablePath()
    {
        // 获取入口程序集的位置（可能是 .dll 或 .exe）
        string location = Assembly.GetEntryAssembly().Location;
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
    public static void RestartAsAdmin()
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
            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"无法以管理员权限重启: {ex.Message}\n路径: {startInfo.FileName} {startInfo.Arguments}");
        }
    }

    // 在程序启动时调用此方法，如果需要管理员权限则自动重启
    public static void RequireAdminOnStart()
    {
        if (!IsRunAsAdmin())
        {
            var result = MessageBox.Show(
                "本程序需要管理员权限才能正常运行。\n是否立即以管理员身份重新启动？",
                "需要管理员权限",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (result == MessageBoxResult.Yes)
            {
                RestartAsAdmin();
            }
            else
            {
                Application.Current.Shutdown();
            }
        }
    }
}