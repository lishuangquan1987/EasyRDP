using System;
using System.Threading.Tasks;
using System.Windows;
using NLog;

namespace EasyRDP.Client.Wpf;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    /// <summary>
    /// 客户端进程级未处理异常捕获 — 在原生调用（H264 解码、网络收发）触发异常时
    /// 记录崩溃前最后的日志，便于定位根因。
    /// </summary>
    protected override void OnStartup(StartupEventArgs e)
    {
        // 捕获后台线程未处理异常（如解码线程、网络接收线程崩溃）
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        // 捕获 Task 未观察异常
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        // 捕获 UI 线程未处理异常
        DispatcherUnhandledException += OnDispatcherUnhandledException;

        Logger.Info("EasyRDP Client starting, processId={0}, bitness={1}",
            System.Diagnostics.Process.GetCurrentProcess().Id,
            IntPtr.Size == 8 ? "x64" : "x86");
        base.OnStartup(e);
    }

    /// <summary>AppDomain 未处理异常 — 包括 AccessViolation（需配合 legacyCorruptedStateExceptionsPolicy）。</summary>
    private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        try
        {
            var ex = e.ExceptionObject as Exception;
            Logger.Fatal("AppDomain.UnhandledException: IsTerminating={0}\n{1}",
                e.IsTerminating, ex?.ToString() ?? e.ExceptionObject?.ToString() ?? "(null)");
            LogManager.Flush();
        }
        catch { }
    }

    /// <summary>Task 未观察异常。</summary>
    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        try
        {
            Logger.Fatal("TaskScheduler.UnobservedTaskException:\n{0}", e.Exception);
            LogManager.Flush();
        }
        catch { }
    }

    /// <summary>UI 线程未处理异常。</summary>
    private void OnDispatcherUnhandledException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
    {
        try
        {
            Logger.Fatal("Dispatcher.UnhandledException:\n{0}", e.Exception);
            LogManager.Flush();
        }
        catch { }
    }
}
