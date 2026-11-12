using System;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;

namespace MDVTool;

public partial class App : Application
{
    public static AppSettings Settings { get; private set; } = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 更新器模式：由正在运行的旧版本拉起，只负责等它退出、覆盖文件、再启动新版
        if (UpdateInstaller.TryRunApplyMode(e.Args))
        {
            StartupUri = null;
            Shutdown();
            return;
        }

        DispatcherUnhandledException += OnUnhandled;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            WriteCrash(args.ExceptionObject as Exception);
        };

        Settings = AppSettings.Load();
        ThemeManager.Apply(Settings.DarkMode, Settings.Palette);
    }

    private void OnUnhandled(object? sender, DispatcherUnhandledExceptionEventArgs e)
    {
        WriteCrash(e.Exception);
        MessageBox.Show("程序遇到一个错误：\n\n" + e.Exception.Message, "MDVTool",
            MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }

    private static void WriteCrash(Exception? ex)
    {
        if (ex == null) return;
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MDVTool");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "crash.log"),
                $"\n==== {DateTime.Now:yyyy-MM-dd HH:mm:ss} ====\n{ex}\n", Encoding.UTF8);
        }
        catch
        {
            // 写日志本身失败就算了
        }
    }
}
