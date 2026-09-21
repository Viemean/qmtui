using System.IO;
using System.Runtime.InteropServices;
using Terminal.Gui.App;
using Terminal.Gui.Views;
using QmTui.Player;
using QmTui.UI;

namespace QmTui;

public static partial class Program
{
    public static void Main(string[] args)
    {
        if (args.Contains("--version") || args.Contains("-v"))
        {
            Console.WriteLine("qmtui 0.3.7");
            return;
        }

        if (args.Contains("--help") || args.Contains("-h"))
        {
            Console.WriteLine("qmtui - Linux Terminal QQ Music Player");
            Console.WriteLine("Usage: qmtui [options]");
            Console.WriteLine();
            Console.WriteLine("Options:");
            Console.WriteLine("  -v, --version        Show version information and exit");
            Console.WriteLine("  -h, --help           Show this help message and exit");
            Console.WriteLine("  -d, --debug          Enable verbose debug logging");
            Console.WriteLine("  -r, --recognize <f>  Recognize song from audio file");
            Console.WriteLine("  --web                Run standalone Web player server");
            Console.WriteLine("  -p, --web-port <p>   Specify Web player port (default: 9999)");
            Console.WriteLine("  --no-audio           Disable local GStreamer playback");
            Console.WriteLine("  --no-notify          Disable desktop song switch notifications");
            return;
        }

        if (args.Length >= 2 && (args[0] == "--recognize" || args[0] == "-r"))
        {
            var wavPath = args[1];
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var res = QmTui.Services.AudioRecognitionService.RecognizeAndMatchAsync(wavPath).GetAwaiter().GetResult();
            sw.Stop();
            Console.WriteLine($"[Recognize Benchmark] Success={res.Success}, Source='{res.Source}', Title='{res.Title}', Artist='{res.Artist}', Album='{res.Album}', TotalElapsed={sw.ElapsedMilliseconds}ms, Err='{res.ErrorMessage}'");
            if (res.MatchedSong != null)
            {
                Console.WriteLine($"[QQ Music Matched] '{res.MatchedSong.Title}' - '{res.MatchedSong.Artist}' (Album: {res.MatchedSong.Album})");
            }
            return;
        }

        bool isDebug = args.Contains("--debug") || args.Contains("-d") ||
                       Environment.GetEnvironmentVariable("QQMUSIC_DEBUG") == "1";
        QmTui.Utils.AppLogger.Init(isDebug);
        QmTui.Utils.AppLogger.Info("Program", $"Starting qmtui. DebugMode: {isDebug}");

        // 加载用户全局偏好配置，并处理命令行参数覆盖
        QmTui.Models.UserConfig.Load();
        if (args.Contains("--no-notify"))
        {
            QmTui.Models.UserConfig.Current.EnableSongSwitchNotification = false;
            QmTui.Utils.AppLogger.Info("Program", "Song switch notifications disabled by --no-notify CLI flag.");
        }

        if (OperatingSystem.IsLinux() && QmTui.Models.UserConfig.Current.EnableSongSwitchNotification)
        {
            QmTui.Services.DesktopNotificationService.Instance.Initialize();
        }

        AppDomain.CurrentDomain.UnhandledException += (s, e) =>
        {
            QmTui.Utils.AppLogger.Fatal("Crash", $"Unhandled AppDomain exception: {e.ExceptionObject}");
        };
        TaskScheduler.UnobservedTaskException += (s, e) =>
        {
            QmTui.Utils.AppLogger.Error("Crash", $"Unobserved task exception: {e.Exception}");
            e.SetObserved();
        };

        // 注册 POSIX 信号处理，避免因终端切后台或写管道失败导致异常退出
        PosixSignalRegistration? sighupReg = null;
        PosixSignalRegistration? sigtermReg = null;
        PosixSignalRegistration? sigintReg = null;

        if (OperatingSystem.IsLinux())
        {
            try
            {
                // 1. 忽略 SIGPIPE（向断开的管道写入数据时避免进程被内核终止）
                signal(SIGPIPE, SIG_IGN);
                QmTui.Utils.AppLogger.Info("Signal", "Ignored SIGPIPE via libc signal(13, SIG_IGN)");

                // 2. 捕获 SIGHUP（SSH 会话断开时退出应用）
                sighupReg = PosixSignalRegistration.Create(PosixSignal.SIGHUP, ctx =>
                {
                    QmTui.Utils.AppLogger.Warn("Signal", "SIGHUP received (SSH session disconnected). Stopping application.");
                    try
                    {
                        Application.Invoke(() => Application.RequestStop());
                    }
                    catch
                    {
                        Environment.Exit(0);
                    }
                });

                // 3. 响应 SIGTERM（系统关机或 kill 退出）
                sigtermReg = PosixSignalRegistration.Create(PosixSignal.SIGTERM, ctx =>
                {
                    QmTui.Utils.AppLogger.Warn("Signal", "SIGTERM received. Requesting shutdown.");
                    Application.Invoke(() => Application.RequestStop());
                });

                // 4. 响应 SIGINT (Ctrl+C)
                sigintReg = PosixSignalRegistration.Create(PosixSignal.SIGINT, ctx =>
                {
                    QmTui.Utils.AppLogger.Warn("Signal", "SIGINT received. Requesting stop.");
                    Application.Invoke(() => Application.RequestStop());
                });
            }
            catch (Exception ex)
            {
                QmTui.Utils.AppLogger.Error("Signal", "Failed to register POSIX signal handlers", ex);
            }
        }

        bool useWebMode = args.Contains("--web") || Environment.GetEnvironmentVariable("QQMUSIC_WEB") == "1";
        int webPort = 9999;
        for (int i = 0; i < args.Length; i++)
        {
            if ((args[i] == "--web-port" || args[i] == "-p") && i + 1 < args.Length && int.TryParse(args[i + 1], out var p))
            {
                webPort = p;
            }
        }
        if (int.TryParse(Environment.GetEnvironmentVariable("QQMUSIC_WEB_PORT"), out var envPort))
        {
            webPort = envPort;
        }

        bool initialAudioEnabled = !args.Contains("--no-audio") &&
                                   !args.Contains("--web-no-audio") &&
                                   Environment.GetEnvironmentVariable("QQMUSIC_NO_AUDIO") != "1";

        IPlayer player;
        if (useWebMode)
        {
            QmTui.Utils.AppLogger.Info("Program", $"Starting with WebPlayer on port {webPort}, audioEnabled={initialAudioEnabled}");
            var webPlayer = new WebPlayer(webPort, initialAudioEnabled);
            webPlayer.Initialize();
            player = webPlayer;
        }
        else
        {
            var gstPlayer = new GstPlayer();
            gstPlayer.Initialize();
            if (!gstPlayer.IsAvailable)
            {
                QmTui.Utils.AppLogger.Warn("Program", "GStreamer is not available on this system (e.g. Android PRoot). Falling back to WebPlayer.");
                gstPlayer.Dispose();
                var webPlayer = new WebPlayer(webPort, initialAudioEnabled);
                webPlayer.Initialize();
                player = webPlayer;
                useWebMode = true;
            }
            else
            {
                player = gstPlayer;
            }
        }

        try
        {
            using (player)
            {
                Application.Init();
                if (Application.Driver != null)
                {
                    Application.Driver.Clipboard = new QmTui.Services.LinuxSystemClipboard();
                    Application.Driver.Force16Colors = false;
                }
                try
                {
                    Console.Out.Write("\x1b[?1004h");
                    Console.Out.Flush();
                }
                catch { }

                MikuTheme.Apply();

                var mainWindow = new MainWindow(player, useWebMode);

                try
                {
                    Application.Run(mainWindow);
                }
                catch (IOException ioEx)
                {
                    QmTui.Utils.AppLogger.Fatal("Fatal", "Terminal I/O stream broken or disconnected", ioEx);
                }
                catch (Exception runEx)
                {
                    QmTui.Utils.AppLogger.Fatal("Fatal", "Terminal.Gui Application.Run encountered an exception", runEx);
                }
                finally
                {
                    try
                    {
                        Console.Out.Write("\x1b[?1004l");
                        Console.Out.Flush();
                    }
                    catch { }

                    try
                    {
                        mainWindow.Dispose();
                    }
                    catch { }

                    try
                    {
                        player.Dispose();
                    }
                    catch { }

                    try
                    {
                        Application.Shutdown();
                    }
                    catch
                    {
                    }
                }
            }
        }
        finally
        {
            sighupReg?.Dispose();
            sigtermReg?.Dispose();
            sigintReg?.Dispose();
            QmTui.Utils.AppLogger.Info("Program", "qmtui Session ended.");
        }
    }

    private const string LibC = "libc.so.6";
    private const int SIGPIPE = 13;
    private static readonly nint SIG_IGN = 1;

    [LibraryImport(LibC, EntryPoint = "signal")]
    private static partial nint signal(int signum, nint handler);
}