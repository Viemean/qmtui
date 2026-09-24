using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using QmTui.Api;
using QmTui.Models;
using QmTui.Services;
using QmTui.Utils;

namespace QmTui.UI;

public static partial class TerminalImageHelper
{
    private const long MaxValidPngCacheBytes = 4000 * 1024; // 4MB
    public const int CurrentCoverVersion = 5;
    public const int TargetCoverDimension = 1200;

    private static readonly string s_versionFile = Path.Combine(CacheManager.CoversDir, "cover_versions.json");
    private static readonly ConcurrentDictionary<string, int> s_coverVersions = LoadCoverVersions();
    private static readonly Lock s_versionLock = new();
    private static int s_versionDirty;

    private static ConcurrentDictionary<string, int> LoadCoverVersions()
    {
        try
        {
            if (File.Exists(s_versionFile))
            {
                var json = File.ReadAllText(s_versionFile);
                var dict = JsonSerializer.Deserialize(json, AppJsonContext.Default.DictionaryStringInt32);
                if (dict != null)
                {
                    return new ConcurrentDictionary<string, int>(dict, StringComparer.OrdinalIgnoreCase);
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.Debug("TerminalImage", $"LoadCoverVersions failed: {ex.Message}");
        }
        return new ConcurrentDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    }

    public static int GetCoverVersion(string key) =>
        s_coverVersions.TryGetValue(key, out var ver) ? ver : 0;

    public static void RecordCoverVersion(string key, int version)
    {
        s_coverVersions[key] = version;
        ScheduleSaveCoverVersions();
    }

    private static void ScheduleSaveCoverVersions()
    {
        if (Interlocked.Exchange(ref s_versionDirty, 1) == 0)
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(2000).ConfigureAwait(false);
                Interlocked.Exchange(ref s_versionDirty, 0);
                SaveCoverVersions();
            });
        }
    }

    private static void SaveCoverVersions()
    {
        lock (s_versionLock)
        {
            try
            {
                var dict = new Dictionary<string, int>(s_coverVersions, StringComparer.OrdinalIgnoreCase);
                var json = JsonSerializer.Serialize(dict, AppJsonContext.Default.DictionaryStringInt32);
                var tmp = s_versionFile + ".tmp";
                File.WriteAllText(tmp, json);
                File.Move(tmp, s_versionFile, overwrite: true);
            }
            catch (Exception ex)
            {
                AppLogger.Debug("TerminalImage", $"SaveCoverVersions failed: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// 读取 PNG 头部前 24 字节获取图像像素宽高（零堆分配）
    /// </summary>
    public static (int width, int height)? GetPngDimensions(string? path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
        try
        {
            using var fs = File.OpenRead(path);
            if (fs.Length < 24) return null;
            Span<byte> buffer = stackalloc byte[24];
            if (fs.Read(buffer) < 24) return null;

            // PNG 魔法头: 89 50 4E 47 0D 0A 1A 0A
            if (buffer[0] != 0x89 || buffer[1] != 0x50 || buffer[2] != 0x4E || buffer[3] != 0x47 ||
                buffer[4] != 0x0D || buffer[5] != 0x0A || buffer[6] != 0x1A || buffer[7] != 0x0A)
            {
                return null;
            }

            int width = BinaryPrimitives.ReadInt32BigEndian(buffer[16..20]);
            int height = BinaryPrimitives.ReadInt32BigEndian(buffer[20..24]);
            return width > 0 && height > 0 ? (width, height) : null;
        }
        catch
        {
            return null;
        }
    }

    /// 校验 PNG 文件头魔数与 IEND 尾部，确保文件完整有效
    /// </summary>
    public static bool IsValidPngFile(string? path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return false;
        try
        {
            var info = new FileInfo(path);
            if (info.Length < 100) return false;

            using var fs = File.OpenRead(path);
            byte[] header = new byte[8];
            if (fs.Read(header, 0, 8) < 8) return false;

            // PNG 魔法头: 89 50 4E 47 0D 0A 1A 0A
            if (header[0] != 0x89 || header[1] != 0x50 || header[2] != 0x4E || header[3] != 0x47 ||
                header[4] != 0x0D || header[5] != 0x0A || header[6] != 0x1A || header[7] != 0x0A)
            {
                return false;
            }

            // 检查末尾 12 字节是否包含 IEND
            fs.Seek(-12, SeekOrigin.End);
            byte[] tail = new byte[12];
            if (fs.Read(tail, 0, 12) < 12) return false;
            var tailAscii = Encoding.ASCII.GetString(tail);
            return tailAscii.Contains("IEND");
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 校验 JPEG 文件头 SOI 与尾部 EOI
    /// </summary>
    public static bool IsValidJpgFile(string? path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return false;
        try
        {
            var info = new FileInfo(path);
            if (info.Length < 100) return false;

            using var fs = File.OpenRead(path);
            byte[] header = new byte[2];
            if (fs.Read(header, 0, 2) < 2) return false;
            if (header[0] != 0xFF || header[1] != 0xD8) return false;

            fs.Seek(-2, SeekOrigin.End);
            byte[] tail = new byte[2];
            if (fs.Read(tail, 0, 2) < 2) return false;
            return tail[0] == 0xFF && tail[1] == 0xD9;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 校验 WebP 文件头 RIFF 与 WEBP 标识
    /// </summary>
    public static bool IsValidWebpFile(string? path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return false;
        try
        {
            var info = new FileInfo(path);
            if (info.Length < 16) return false;

            using var fs = File.OpenRead(path);
            byte[] header = new byte[12];
            if (fs.Read(header, 0, 12) < 12) return false;
            return header[0] == 'R' && header[1] == 'I' && header[2] == 'F' && header[3] == 'F' &&
                   header[8] == 'W' && header[9] == 'E' && header[10] == 'B' && header[11] == 'P';
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 流式获取远程图像并原子落盘，避免大尺寸封面分配到大对象堆 (LOH)
    /// </summary>
    private static async Task<bool> DownloadImageStreamToFileAsync(string url, string destinationFile, CancellationToken cancellationToken = default)
    {
        var tempFile = destinationFile + ".tmp." + Guid.NewGuid().ToString("N");
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Add("User-Agent", "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
            req.Headers.Add("Referer", "https://y.qq.com/");

            using var resp = await s_httpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return false;

            await using (var fs = new FileStream(tempFile, FileMode.Create, FileAccess.Write, FileShare.None, 8192, useAsync: true))
            {
                await resp.Content.CopyToAsync(fs, cancellationToken).ConfigureAwait(false);
            }

            var fi = new FileInfo(tempFile);
            if (fi.Length > 2048)
            {
                File.Move(tempFile, destinationFile, overwrite: true);
                return true;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AppLogger.Debug("TerminalImage", $"DownloadImageStreamToFileAsync failed for {url}: {ex.Message}");
        }
        finally
        {
            if (File.Exists(tempFile))
            {
                try { File.Delete(tempFile); } catch { }
            }
        }
        return false;
    }

    /// <summary>
    /// 获取封面本地缓存路径，如未缓存或损坏则自愈重新拉取并转为 Kitty 协议兼容的 PNG 格式
    /// </summary>
    public static async Task<string?> EnsureAlbumCoverAsync(string albumMid, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(albumMid) || cancellationToken.IsCancellationRequested) return null;

        var pngFile = Path.Combine(s_cacheDir, $"{albumMid}.png");
        bool isOutdated = false;
        if (File.Exists(pngFile))
        {
            var fi = new FileInfo(pngFile);
            if (fi.Length <= MaxValidPngCacheBytes && IsValidPngFile(pngFile))
            {
                var dims = GetPngDimensions(pngFile);
                int ver = GetCoverVersion(albumMid);
                isOutdated = dims.HasValue &&
                             (dims.Value.width < TargetCoverDimension || dims.Value.height < TargetCoverDimension) &&
                             ver < CurrentCoverVersion;

                if (!isOutdated)
                {
                    if (dims.HasValue && dims.Value.width >= TargetCoverDimension && ver < CurrentCoverVersion)
                    {
                        RecordCoverVersion(albumMid, CurrentCoverVersion);
                    }
                    CacheManager.RecordAccess($"covers/{Path.GetFileName(pngFile)}", fi.Length);
                    return pngFile;
                }

                AppLogger.Info("TerminalImage", $"Cover {albumMid}.png ({dims?.width}x{dims?.height}, v{ver}) is outdated, upgrading to {TargetCoverDimension}px...");
            }
            else
            {
                try { File.Delete(pngFile); } catch {}
            }
        }

        var localFile = Path.Combine(s_cacheDir, $"{albumMid}.jpg");
        if (File.Exists(localFile) && !IsValidJpgFile(localFile))
        {
            try { File.Delete(localFile); } catch {}
        }

        if (isOutdated && File.Exists(localFile))
        {
            var highResUrl = $"https://y.qq.com/music/photo_new/T002R1200x1200M000{albumMid}.jpg?max_age=2592000";
            var tempHighRes = localFile + ".highres.tmp";
            if (await DownloadImageStreamToFileAsync(highResUrl, tempHighRes, cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    File.Move(tempHighRes, localFile, overwrite: true);
                }
                catch {}
            }
            try { if (File.Exists(tempHighRes)) File.Delete(tempHighRes); } catch {}
        }
        else if (!File.Exists(localFile) || new FileInfo(localFile).Length == 0)
        {
            var rawMid = albumMid.Contains('_') ? albumMid.Split('_')[0] : albumMid;
            string[] resolutionUrls =
            [
                $"https://y.qq.com/music/photo_new/T002R1200x1200M000{albumMid}.jpg?max_age=2592000",
                $"https://y.qq.com/music/photo_new/T002R800x800M000{albumMid}.jpg?max_age=2592000",
                $"https://y.qq.com/music/photo_new/T002R800x800M000{rawMid}_1.jpg?max_age=2592000",
                $"https://y.qq.com/music/photo_new/T002R800x800M000{rawMid}_2.jpg?max_age=2592000",
                $"https://y.gtimg.cn/music/photo_new/T002R800x800M000{albumMid}.jpg?max_age=2592000",
                $"https://y.gtimg.cn/music/photo_new/T002R800x800M000{rawMid}_1.jpg?max_age=2592000",
                $"https://y.qq.com/music/photo_new/T002R500x500M000{albumMid}.jpg?max_age=2592000",
                $"https://y.qq.com/music/photo_new/T002R300x300M000{albumMid}.jpg?max_age=2592000"
            ];

            foreach (var url in resolutionUrls)
            {
                if (cancellationToken.IsCancellationRequested) return null;
                if (await DownloadImageStreamToFileAsync(url, localFile, cancellationToken).ConfigureAwait(false))
                {
                    break;
                }
            }
        }

        if (!File.Exists(localFile) || new FileInfo(localFile).Length == 0 || cancellationToken.IsCancellationRequested)
        {
            if (isOutdated && File.Exists(pngFile))
            {
                RecordCoverVersion(albumMid, CurrentCoverVersion);
                return pngFile;
            }
            return null;
        }

        if (!IsImageSupported)
        {
            return localFile;
        }

        // 应用平滑 6px 圆角遮罩处理（无外扩阴影）
        var processed = await ApplyRoundedCornersAsync(localFile, pngFile, cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrEmpty(processed) && File.Exists(processed))
        {
            RecordCoverVersion(albumMid, CurrentCoverVersion);
            CacheManager.RecordAccess($"covers/{Path.GetFileName(processed)}", new FileInfo(processed).Length);
            CacheManager.EnforceLimitAsync();
        }
        else if (isOutdated && File.Exists(pngFile))
        {
            RecordCoverVersion(albumMid, CurrentCoverVersion);
            return pngFile;
        }
        return processed ?? localFile;
    }

    /// <summary>
    /// 处理本地图片（内嵌封面或本地 cover.jpg），添加平滑 6px 抗锯齿圆角并转为 Kitty 协议兼容 PNG
    /// </summary>
    public static async Task<string?> EnsureLocalImageProcessedAsync(string localRawImagePath, string cacheKey, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(localRawImagePath) || !File.Exists(localRawImagePath) || cancellationToken.IsCancellationRequested) return null;

        var pngFile = Path.Combine(s_cacheDir, $"local_{cacheKey}.png");
        if (File.Exists(pngFile))
        {
            var fi = new FileInfo(pngFile);
            if (fi.Length > MaxValidPngCacheBytes || !IsValidPngFile(pngFile))
            {
                try { File.Delete(pngFile); } catch {}
            }
            else
            {
                return pngFile;
            }
        }

        var processed = await ApplyRoundedCornersAsync(localRawImagePath, pngFile, cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrEmpty(processed) && File.Exists(processed))
        {
            CacheManager.RecordAccess($"covers/{Path.GetFileName(processed)}", new FileInfo(processed).Length);
            CacheManager.EnforceLimitAsync();
            return processed;
        }

        // 若圆角生成失败且输入为临时文件，复制为持久化文件以防外层删除临时文件导致无法被 Web 读取
        if (localRawImagePath.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
        {
            var persistentFallback = Path.Combine(s_cacheDir, $"local_{cacheKey}.raw");
            try
            {
                File.Copy(localRawImagePath, persistentFallback, overwrite: true);
                return persistentFallback;
            }
            catch (Exception ex)
            {
                AppLogger.Debug("TerminalImage", $"Copy persistentFallback failed: {ex.Message}");
                return null;
            }
        }

        return localRawImagePath;
    }

    /// <summary>
    /// 获取歌手写真本地缓存路径，如未缓存则异步拉取并转为 Kitty 协议兼容的 6px 圆角 PNG 格式
    /// </summary>
    public static async Task<string?> EnsureSingerCoverAsync(string singerMid, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(singerMid) || cancellationToken.IsCancellationRequested) return null;

        var pngFile = Path.Combine(s_cacheDir, $"singer_{singerMid}.png");
        if (File.Exists(pngFile))
        {
            var fi = new FileInfo(pngFile);
            if (fi.Length <= MaxValidPngCacheBytes && IsValidPngFile(pngFile))
            {
                CacheManager.RecordAccess($"covers/{Path.GetFileName(pngFile)}", fi.Length);
                return pngFile;
            }
            try { File.Delete(pngFile); } catch {}
        }

        var localFile = Path.Combine(s_cacheDir, $"singer_{singerMid}.jpg");
        if (File.Exists(localFile) && !IsValidJpgFile(localFile))
        {
            try { File.Delete(localFile); } catch {}
        }
        if (!File.Exists(localFile) || new FileInfo(localFile).Length == 0)
        {
            string[] resolutionUrls =
            [
                $"https://y.qq.com/music/photo_new/T001R500x500M000{singerMid}.jpg?max_age=2592000",
                $"https://y.qq.com/music/photo_new/T001R300x300M000{singerMid}.jpg?max_age=2592000"
            ];

            foreach (var url in resolutionUrls)
            {
                if (cancellationToken.IsCancellationRequested) return null;
                if (await DownloadImageStreamToFileAsync(url, localFile, cancellationToken).ConfigureAwait(false))
                {
                    break;
                }
            }
        }

        if (!File.Exists(localFile) || new FileInfo(localFile).Length == 0 || cancellationToken.IsCancellationRequested)
        {
            return null;
        }

        if (!IsImageSupported)
        {
            return localFile;
        }

        var processed = await ApplyRoundedCornersAsync(localFile, pngFile, cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrEmpty(processed) && File.Exists(processed))
        {
            CacheManager.RecordAccess($"covers/{Path.GetFileName(processed)}", new FileInfo(processed).Length);
            CacheManager.EnforceLimitAsync();
        }
        return processed ?? localFile;
    }

    /// <summary>
    /// 获取单曲封面，优先拉取高分辨率版本
    /// </summary>
    public static async Task<string?> EnsureSingleCoverAsync(string songMid, string vsMid, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(songMid) || string.IsNullOrWhiteSpace(vsMid) || cancellationToken.IsCancellationRequested) return null;

        var pngFile = Path.Combine(s_cacheDir, $"single_{songMid}.png");
        var coverKey = $"single_{songMid}";
        bool isOutdated = false;
        if (File.Exists(pngFile))
        {
            var fi = new FileInfo(pngFile);
            if (fi.Length <= MaxValidPngCacheBytes && IsValidPngFile(pngFile))
            {
                var dims = GetPngDimensions(pngFile);
                int ver = GetCoverVersion(coverKey);
                isOutdated = dims.HasValue &&
                             (dims.Value.width < TargetCoverDimension || dims.Value.height < TargetCoverDimension) &&
                             ver < CurrentCoverVersion;

                if (!isOutdated)
                {
                    if (dims.HasValue && dims.Value.width >= TargetCoverDimension && ver < CurrentCoverVersion)
                    {
                        RecordCoverVersion(coverKey, CurrentCoverVersion);
                    }
                    CacheManager.RecordAccess($"covers/{Path.GetFileName(pngFile)}", fi.Length);
                    return pngFile;
                }

                AppLogger.Info("TerminalImage", $"Single cover {coverKey}.png ({dims?.width}x{dims?.height}, v{ver}) is outdated, upgrading to {TargetCoverDimension}px...");
            }
            else
            {
                try { File.Delete(pngFile); } catch {}
            }
        }

        var localFile = Path.Combine(s_cacheDir, $"single_{songMid}.jpg");
        if (File.Exists(localFile) && !IsValidJpgFile(localFile))
        {
            try { File.Delete(localFile); } catch {}
        }

        if (isOutdated && File.Exists(localFile))
        {
            string[] upgradeUrls =
            [
                $"https://y.qq.com/music/photo_new/T062M000{vsMid}.jpg?max_age=2592000",
                $"https://y.qq.com/music/photo_new/T062R1200x1200M000{vsMid}.jpg?max_age=2592000"
            ];
            var tempHighRes = localFile + ".highres.tmp";
            foreach (var url in upgradeUrls)
            {
                if (cancellationToken.IsCancellationRequested) break;
                if (await DownloadImageStreamToFileAsync(url, tempHighRes, cancellationToken).ConfigureAwait(false))
                {
                    try { File.Move(tempHighRes, localFile, overwrite: true); } catch {}
                    break;
                }
            }
            try { if (File.Exists(tempHighRes)) File.Delete(tempHighRes); } catch {}
        }
        else if (!File.Exists(localFile) || new FileInfo(localFile).Length == 0)
        {
            string[] resolutionUrls =
            [
                $"https://y.qq.com/music/photo_new/T062M000{vsMid}.jpg?max_age=2592000",          // 原画档案档
                $"https://y.qq.com/music/photo_new/T062R1200x1200M000{vsMid}.jpg?max_age=2592000",    // 1200x1200 超高清大图
                $"https://y.qq.com/music/photo_new/T062R800x800M000{vsMid}.jpg?max_age=2592000",      // 800x800 高清档
                $"https://y.gtimg.cn/music/photo_new/T062R1200x1200M000{vsMid}.jpg?max_age=2592000",  // 备用 CDN
                $"https://y.qq.com/music/photo_new/T062R500x500M000{vsMid}.jpg?max_age=2592000"
            ];

            foreach (var url in resolutionUrls)
            {
                if (cancellationToken.IsCancellationRequested) return null;
                if (await DownloadImageStreamToFileAsync(url, localFile, cancellationToken).ConfigureAwait(false))
                {
                    break;
                }
            }
        }

        if (!File.Exists(localFile) || new FileInfo(localFile).Length == 0 || cancellationToken.IsCancellationRequested)
        {
            if (isOutdated && File.Exists(pngFile))
            {
                RecordCoverVersion(coverKey, CurrentCoverVersion);
                return pngFile;
            }
            return null;
        }

        if (!IsImageSupported)
        {
            return localFile;
        }

        var processed = await ApplyRoundedCornersAsync(localFile, pngFile, cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrEmpty(processed) && File.Exists(processed))
        {
            RecordCoverVersion(coverKey, CurrentCoverVersion);
        }
        else if (isOutdated && File.Exists(pngFile))
        {
            RecordCoverVersion(coverKey, CurrentCoverVersion);
            return pngFile;
        }
        return processed ?? localFile;
    }

    /// <summary>
    /// 获取 HTTP/HTTPS 直链封面（包含 Connect 协议下 App 提供的代理封面），并持久化到本地缓存与圆角处理
    /// </summary>
    public static async Task<string?> EnsureHttpCoverAsync(string url, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(url) || cancellationToken.IsCancellationRequested) return null;

        var urlHash = Convert.ToHexString(System.Security.Cryptography.MD5.HashData(Encoding.UTF8.GetBytes(url))).ToLowerInvariant();
        var pngFile = Path.Combine(s_cacheDir, $"http_{urlHash}.png");
        if (File.Exists(pngFile))
        {
            var fi = new FileInfo(pngFile);
            if (fi.Length <= MaxValidPngCacheBytes && IsValidPngFile(pngFile))
            {
                CacheManager.RecordAccess($"covers/{Path.GetFileName(pngFile)}", fi.Length);
                return pngFile;
            }
            try { File.Delete(pngFile); } catch { }
        }

        var localFile = Path.Combine(s_cacheDir, $"http_{urlHash}.raw");
        if (File.Exists(localFile) && (!IsValidJpgFile(localFile) && !IsValidPngFile(localFile) && !IsValidWebpFile(localFile)))
        {
            try { File.Delete(localFile); } catch { }
        }

        if (!File.Exists(localFile) || new FileInfo(localFile).Length == 0)
        {
            if (cancellationToken.IsCancellationRequested) return null;
            await DownloadImageStreamToFileAsync(url, localFile, cancellationToken).ConfigureAwait(false);
        }

        if (!File.Exists(localFile) || new FileInfo(localFile).Length == 0 || cancellationToken.IsCancellationRequested)
        {
            return null;
        }

        if (!IsImageSupported)
        {
            return localFile;
        }

        var processed = await ApplyRoundedCornersAsync(localFile, pngFile, cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrEmpty(processed) && File.Exists(processed))
        {
            CacheManager.RecordAccess($"covers/{Path.GetFileName(processed)}", new FileInfo(processed).Length);
            CacheManager.EnforceLimitAsync();
        }
        return processed ?? localFile;
    }

    /// <summary>
    /// 获取歌曲播放时对应的超高清封面（智能自愈：优先专辑1200，单曲智能调用T062原画/1200，本地音频提取嵌入封面）
    /// </summary>
    public static async Task<string?> EnsureSongCoverAsync(Song? song, CancellationToken cancellationToken = default)
    {
        if (song == null || cancellationToken.IsCancellationRequested) return null;

        // 优先检查是否有 HTTP / HTTPS 直链封面（包含 App 提供的 http://<ip>:8766/cover/local?... 代理地址或三方直链）
        if (!string.IsNullOrEmpty(song.CoverUrl) &&
            (song.CoverUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
             song.CoverUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
        {
            var httpCover = await EnsureHttpCoverAsync(song.CoverUrl, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(httpCover))
            {
                return httpCover;
            }
        }

        if (song.IsWebDav)
        {
            var server = QmTui.Services.WebDavService.GetActiveServer();
            if (server != null)
            {
                var wdCover = await QmTui.Services.WebDavService.EnsureCoverAsync(server, song, cancellationToken).ConfigureAwait(false);
                if (!string.IsNullOrEmpty(wdCover))
                {
                    return wdCover;
                }
            }
        }

        if (song.IsLocal)
        {
            var filePath = song.LocalFilePath;
            if (!string.IsNullOrEmpty(filePath) && File.Exists(filePath))
            {
                return await QmTui.Services.LocalMusicService.EnsureCoverAsync(song with { LocalFilePath = filePath }, cancellationToken).ConfigureAwait(false);
            }
            return null;
        }

        if (cancellationToken.IsCancellationRequested) return null;

        // 1. 若拥有 AlbumMid，优先获取专辑 1200 超高清封面
        if (!string.IsNullOrWhiteSpace(song.AlbumMid))
        {
            var albumCover = await EnsureAlbumCoverAsync(song.AlbumMid, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(albumCover))
            {
                return albumCover;
            }
        }

        if (cancellationToken.IsCancellationRequested) return null;

        // 2. 若无 AlbumMid 或拉取不到（单曲），自动解析单曲专属视觉 MID 并拉取 T062 原画大图
        if (!string.IsNullOrWhiteSpace(song.Mid))
        {
            var vsMid = await MusicApi.GetSongVisualMidAsync(song.Mid, cancellationToken).ConfigureAwait(false);
            if (cancellationToken.IsCancellationRequested) return null;
            if (!string.IsNullOrWhiteSpace(vsMid))
            {
                var singleCover = await EnsureSingleCoverAsync(song.Mid, vsMid, cancellationToken).ConfigureAwait(false);
                if (!string.IsNullOrEmpty(singleCover))
                {
                    return singleCover;
                }
            }
        }

        return null;
    }

}
