using System.Text;

namespace QmTui.Utils;

/// <summary>
/// 应用程序日志服务。生产环境下默认不进行磁盘 I/O 刷盘，仅在调试模式或严重异常时写盘。
/// </summary>
public static class AppLogger
{
    private static readonly string s_logFilePath = "/tmp/qqmusic_debug.log";
    private static readonly object s_lock = new();
    private static bool s_debugEnabled;

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
                // 若日志文件超过 5MB，安全轮转备份一次，防止无限膨胀
                if (File.Exists(s_logFilePath) && new FileInfo(s_logFilePath).Length > 5 * 1024 * 1024)
                {
                    var backupPath = s_logFilePath + ".old";
                    File.Move(s_logFilePath, backupPath, overwrite: true);
                }

                File.AppendAllText(s_logFilePath, $"=== qmtui Session Started at {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} (Debug: {s_debugEnabled}) ===\n", Encoding.UTF8);
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
        try
        {
            lock (s_lock)
            {
                File.AppendAllText(s_logFilePath, line, Encoding.UTF8);
            }
        }
        catch
        {
        }
    }
}
