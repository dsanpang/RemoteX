using System;
using System.IO;
using Serilog;
using Serilog.Core;

namespace RemoteX;

/// <summary>
/// 静态日志门面，内部委托�?Serilog rolling-file sink�?
/// 保持与旧�?API (Info / Warn / Error / Dispose) 兼容�?
/// </summary>
internal static class AppLogger
{
    private static ILogger _log = Logger.None;

    public static void Initialize()
    {
        var logDir = AppPaths.LogsDir;
        Directory.CreateDirectory(logDir);

        _log = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.File(
                path: Path.Combine(logDir, "app-.log"),
                rollingInterval: RollingInterval.Day,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}",
                retainedFileCountLimit: 14,
                shared: true)
            .CreateLogger();

        _log.Information("logger initialized (Serilog {Version})", typeof(Log).Assembly.GetName().Version);
    }

    public static void Info(string message)  => _log.Information(message);
    public static void Warn(string message)  => _log.Warning(message);

    public static void Error(string message, Exception? ex = null)
    {
        if (ex is null) _log.Error(message);
        else            _log.Error(ex, message);
    }

    /// <summary>应用退出前调用，确保缓冲日志全部落盘�?/summary>
    public static void Dispose()
    {
        if (_log is IDisposable d) d.Dispose();
        _log = Logger.None;
    }
}
