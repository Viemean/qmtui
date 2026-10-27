using System.Text;

namespace QmTui.Utils;

/// <summary>
/// 应用程序日志服务。生产环境下默认不进行磁盘 I/O 刷盘，仅在调试模式或严重异常时写盘。
/// </summary>
public static class AppLogger
{
    private static string? s_logFilePath;
    private static readonly object s_lock = new();
    private static bool s_debugEnabled;

    /// <summary>
    /// 当前日志输出文件的绝对路径
    /// </summary>
    public static string LogFilePath => s_logFilePath ??= ResolveLogFilePath();

    /// <summary>
    /// 初始化日志服务，设定调试模式并轮转超限历史日志文件
    /// </summary>
    /// <param name="debugEnabled">是否启用详细调试日志落盘，默认为 false。</param>
    public static void Init(bool debugEnabled = false)
    {
        s_debugEnabled = debugEnabled;

        try
        {
            lock (s_lock)
            {
                var filePath = LogFilePath;
                var logDir = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(logDir) && !Directory.Exists(logDir))
                {
                    Directory.CreateDirectory(logDir);
                }

                try
                {
                    if (File.Exists(filePath) && new FileInfo(filePath).Length > 5 * 1024 * 1024)
                    {
                        var backupPath = filePath + ".old";
                        File.Move(filePath, backupPath, overwrite: true);
                    }
                }
                catch
                {
                }

                AppendLog(filePath, $"=== qmtui Session Started at {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} (PID: {Environment.ProcessId}, Debug: {s_debugEnabled}) ===\n");
            }
        }
        catch
        {
        }
    }

    /// <summary>
    /// 记录普通运行信息日志（仅在调试模式开启时写入文件）
    /// </summary>
    /// <param name="module">所属模块名称。</param>
    /// <param name="message">日志消息内容。</param>
    public static void Info(string module, string message)
    {
        if (s_debugEnabled) Log("INFO", module, message);
    }

    /// <summary>
    /// 记录警告级别日志
    /// </summary>
    /// <param name="module">所属模块名称。</param>
    /// <param name="message">警告消息内容。</param>
    public static void Warn(string module, string message)
    {
        Log("WARN", module, message);
    }

    /// <summary>
    /// 强制写入日志文件
    /// </summary>
    /// <param name="module">记录日志的所属模块名称。</param>
    /// <param name="message">待写入日志文件的详细消息内容。</param>
    public static void Force(string module, string message)
    {
        Log("FORCE", module, message);
    }

    /// <summary>
    /// 记录详细调试信息日志（仅在调试模式开启时写入文件）
    /// </summary>
    /// <param name="module">所属模块名称。</param>
    /// <param name="message">调试消息内容。</param>
    public static void Debug(string module, string message)
    {
        if (s_debugEnabled) Log("DEBUG", module, message);
    }

    /// <summary>
    /// 记录错误级别日志并输出异常堆栈
    /// </summary>
    /// <param name="module">所属模块名称。</param>
    /// <param name="message">错误消息内容。</param>
    /// <param name="ex">关联的异常对象；若无则为 null。</param>
    public static void Error(string module, string message, Exception? ex = null)
    {
        var fullMsg = ex != null ? $"{message} | Exception: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}" : message;
        Log("ERROR", module, fullMsg);
    }

    /// <summary>
    /// 记录致命错误日志并输出异常堆栈
    /// </summary>
    /// <param name="module">所属模块名称。</param>
    /// <param name="message">致命故障消息内容。</param>
    /// <param name="ex">关联的异常对象；若无则为 null。</param>
    public static void Fatal(string module, string message, Exception? ex = null)
    {
        var fullMsg = ex != null ? $"{message} | Exception: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}" : message;
        Log("FATAL", module, fullMsg);
    }

    private static void Log(string level, string module, string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss.fff}] [{level}] [{module}] {message}\n";
        var filePath = LogFilePath;
        if (!string.IsNullOrEmpty(filePath))
        {
            AppendLog(filePath, line);
        }
    }

    private static void AppendLog(string filePath, string text)
    {
        try
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            using var fs = new FileStream(filePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            fs.Write(bytes);
        }
        catch
        {
        }
    }

    private static string ResolveLogFilePath()
    {
        var envPath = Environment.GetEnvironmentVariable("QMTUI_LOG_PATH");
        if (!string.IsNullOrWhiteSpace(envPath))
        {
            return envPath;
        }

        try
        {
            return Path.Combine(AppPathHelper.LogDir, "qmtui.log");
        }
        catch
        {
            return Path.Combine(Path.GetTempPath(), "qmtui.log");
        }
    }
}
