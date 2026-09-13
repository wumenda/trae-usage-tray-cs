using System.Diagnostics;

namespace TraeUsageTray;

internal static class Program
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "TRAE Usage";

    [STAThread]
    private static void Main()
    {
        // 单实例互斥：重复启动时提示并自动打开已运行实例的面板
        using var mutex = new Mutex(true, @"Local\TraeUsageTray", out var createdNew);
        if (!createdNew)
        {
            try
            {
                Process.Start(new ProcessStartInfo("http://localhost:9876/") { UseShellExecute = true });
            }
            catch { }
            MessageBox.Show("TRAE Usage 已在运行，已为你打开用量面板。", "用量监控",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        Application.EnableVisualStyles();
        Application.SetHighDpiMode(HighDpiMode.SystemAware);
        Application.SetCompatibleTextRenderingDefault(false);

        // 首次运行/配置损坏时生成默认配置，绝不崩溃
        AppConfig.LoadOrCreate();

        using var app = new TrayApp();
        Application.Run(new ApplicationContext());
    }

    // ---- 开机自启（HKCU Run 键，无需管理员） ----
    public static bool AutoStartEnabled()
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKeyPath);
        return key?.GetValue(RunValueName) is string p
               && p.Contains(Environment.ProcessPath ?? "\0", StringComparison.OrdinalIgnoreCase);
    }

    public static void SetAutoStart(bool on)
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKeyPath);
        if (key is null) return;
        if (on && Environment.ProcessPath is { } exe)
            key.SetValue(RunValueName, $"\"{exe}\"");
        else
            key.DeleteValue(RunValueName, throwOnMissingValue: false);
    }
}
