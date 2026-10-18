using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace LivePhoto.App;

public partial class App : Application
{
    /// <summary>命令行带入的文件夹（支持把文件夹拖到 exe 图标上打开）。</summary>
    public static string? StartupFolder { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        if (e.Args.Length > 0)
        {
            string p = e.Args[0];
            if (Directory.Exists(p))
            {
                StartupFolder = p;
            }
            else if (File.Exists(p))
            {
                StartupFolder = Path.GetDirectoryName(p);
            }
        }

        // 未处理异常不得静默吞掉 —— 否则会表现为"进程在跑但窗口不出来"
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
            {
                File.WriteAllText(Path.Combine(Path.GetTempPath(), "livephoto_crash.txt"), ex.ToString());
            }
        };

        base.OnStartup(e);
    }

    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "livephoto_ui_error.txt"), e.Exception.ToString());
        MessageBox.Show(
            "发生未处理异常：\n\n" + e.Exception.Message + "\n\n详情已写入 %TEMP%\\livephoto_ui_error.txt",
            "LivePhoto 修复器", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
