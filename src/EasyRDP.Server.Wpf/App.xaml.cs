#nullable enable
using System;
using System.Configuration;
using System.Data;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using NLog;

namespace EasyRDP.Server.Wpf;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    /// <summary>
    /// 静态构造：在 WPF 初始化 DPI 前显式声明系统 DPI 感知（清单 dpiAware 的兜底）。
    /// 否则 Win7 高分屏下 GetSystemMetrics 返回逻辑像素，输入坐标与物理画面错位（水平偏移）。
    /// </summary>
    static App()
    {
        try
        {
            NativeDpi.SetProcessDpiAware();
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "SetProcessDPIAware failed（XP 无此 API，属预期）");
        }
    }

    /// <summary>DPI 感知原生调用（Vista+；XP 上入口不存在，忽略）。</summary>
    private static class NativeDpi
    {
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        public static extern bool SetProcessDPIAware();

        public static void SetProcessDpiAware()
        {
            SetProcessDPIAware();
        }
    }

    /// <summary>DPI 查询原生调用（GetDeviceCaps 在 XP 可用）。</summary>
    private static class NativeMethods
    {
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        public static extern IntPtr GetDC(IntPtr hWnd);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        public static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

        [System.Runtime.InteropServices.DllImport("gdi32.dll")]
        public static extern int GetDeviceCaps(IntPtr hdc, int nIndex);
    }

    /// <summary>
    /// 服务端进程级未处理异常捕获 — 主要是为了在 EncodeFrame 等原生调用
    /// 触发 AccessViolation 时能记录崩溃前最后的日志，便于定位根因。
    /// </summary>
    protected override void OnStartup(StartupEventArgs e)
    {
        // 捕获后台线程未处理异常（如 EncodeLoop 线程崩溃）
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        // 捕获 Task 未观察异常
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        // 捕获 UI 线程未处理异常
        DispatcherUnhandledException += OnDispatcherUnhandledException;

        Logger.Info("EasyRDP Server starting, processId={0}, bitness={1}",
            System.Diagnostics.Process.GetCurrentProcess().Id,
            IntPtr.Size == 8 ? "x64" : "x86");
        // 启动版本标识：程序集版本 + exe 构建时间 + 修复特征常量。
        // 部署后从日志首行即可确认二进制版本（排查"现象依旧=旧构建"问题）。
        Logger.Info("=== EasyRDP Server version: {0} flowControlFix={1} ===",
            EasyRDP.Core.Diagnostics.BuildInfo.Describe(),
            EasyRDP.Core.Diagnostics.BuildInfo.FlowControlFixVersion);
        // 系统 DPI 日志：GetDeviceCaps(LOGPIXELSX)。若服务端 win10 为 150% 缩放
        // 而截图/SetCursorPos 有逻辑/物理不一致，会造成输入坐标偏移（排查用）。
        try
        {
            IntPtr hdc = NativeMethods.GetDC(IntPtr.Zero);
            if (hdc != IntPtr.Zero)
            {
                int dpiX = NativeMethods.GetDeviceCaps(hdc, 88); // LOGPIXELSX
                int dpiY = NativeMethods.GetDeviceCaps(hdc, 90); // LOGPIXELSY
                NativeMethods.ReleaseDC(IntPtr.Zero, hdc);
                Logger.Info("System DPI: {0}x{1} (scale {2:F2}x{3:F2})",
                    dpiX, dpiY, dpiX / 96.0, dpiY / 96.0);
            }
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "GetDeviceCaps DPI query failed");
        }
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
