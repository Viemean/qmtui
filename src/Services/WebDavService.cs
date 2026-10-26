using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using QmTui.Api;
using QmTui.Models;
using QmTui.Utils;

namespace QmTui.Services;

/// <summary>
/// WebDAV 远程私有云音乐服务：
/// 1. 基于原生 HttpClient 发送 PROPFIND，通过 XDocument 解析目录树；
/// 2. 支持自签名证书与 Basic Auth 凭据认证；
/// 3. 本地边播边存隔离，防止 NAS 密码在 GStreamer/D-Bus 广播中泄露；
/// 4. 目录树与平铺曲库轮转。
/// </summary>
public static partial class WebDavService
{
    private static readonly string s_configDir = AppPathHelper.ConfigDir;
    private static readonly string s_configFile = Path.Combine(s_configDir, "webdav.json");
    private static readonly string s_cacheDir = CacheManager.WebDavDir;

    private static readonly HashSet<string> s_supportedAudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".flac", ".mp3", ".m4a", ".wav", ".ogg", ".aac", ".opus", ".ape"
    };

    private static readonly Lock s_lock = new();
    private static WebDavConfig s_config = new();
    private static bool s_loaded;

    private static readonly ConcurrentDictionary<string, HttpClient> s_clientCache = new();
    private static readonly ConcurrentDictionary<string, Task<string?>> s_inFlightDownloads = new(StringComparer.OrdinalIgnoreCase);

    static WebDavService()
    {
        try
        {
            if (!Directory.Exists(s_configDir))
            {
                Directory.CreateDirectory(s_configDir);
            }
            if (!Directory.Exists(s_cacheDir))
            {
                Directory.CreateDirectory(s_cacheDir);
            }
        }
        catch (Exception ex)
        {
            AppLogger.Debug("WebDavService", $"EnsureConfigDir failed: {ex.Message}");
        }
    }

    /// <summary>
    /// 加载本地 WebDAV 服务器配置，若不存在则初始化空配置列表
    /// </summary>
    public static void LoadConfig()
    {
        lock (s_lock)
        {
            if (s_loaded) return;
            s_loaded = true;

            if (File.Exists(s_configFile))
            {
                try
                {
                    var json = File.ReadAllText(s_configFile, Encoding.UTF8);
                    var cfg = JsonSerializer.Deserialize(json, WebDavJsonContext.Default.WebDavConfig);
                    if (cfg != null)
                    {
                        s_config = cfg;
                        s_config.Servers ??= [];
                        return;
                    }
                }
                catch (Exception ex)
                {
                    AppLogger.Warn("WebDavService", $"Failed to load webdav.json: {ex.Message}");
                }
            }

            s_config = new WebDavConfig
            {
                Servers = []
            };
        }
    }

    /// <summary>
    /// 持久化保存 WebDAV 配置至本地 JSON 文件
    /// </summary>
    public static void SaveConfig()
    {
        lock (s_lock)
        {
            try
            {
                var json = JsonSerializer.Serialize(s_config, WebDavJsonContext.Default.WebDavConfig);
                File.WriteAllText(s_configFile, json, Encoding.UTF8);
            }
            catch (Exception ex)
            {
                AppLogger.Error("WebDavService", $"Failed to save webdav.json: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// 获取当前已配置的 WebDAV 服务器列表副本
    /// </summary>
    /// <returns>已保存的 WebDAV 服务器列表副本。</returns>
    public static List<WebDavServer> GetServers()
    {
        LoadConfig();
        lock (s_lock)
        {
            return new List<WebDavServer>(s_config.Servers);
        }
    }

    /// <summary>
    /// 获取当前激活选中的 WebDAV 服务器
    /// </summary>
    /// <returns>当前激活的 WebDAV 服务器实例；若未配置任何服务器返回 <see langword="null"/>。</returns>
    public static WebDavServer? GetActiveServer()
    {
        LoadConfig();
        lock (s_lock)
        {
            if (s_config.Servers.Count == 0) return null;
            if (string.IsNullOrEmpty(s_config.ActiveServerId))
            {
                return s_config.Servers[0];
            }
            return s_config.Servers.Find(s => s.Id == s_config.ActiveServerId) ?? s_config.Servers[0];
        }
    }

    /// <summary>
    /// 切换当前激活的 WebDAV 服务器标识
    /// </summary>
    /// <param name="serverId">目标服务器唯一标识符。</param>
    public static void SetActiveServer(string serverId)
    {
        LoadConfig();
        lock (s_lock)
        {
            s_config.ActiveServerId = serverId;
            SaveConfig();
        }
    }

    /// <summary>
    /// 新增或更新 WebDAV 服务器配置并重置其客户端连接缓存
    /// </summary>
    /// <param name="server">待保存的 WebDAV 服务器配置实例。</param>
    public static void SaveServer(WebDavServer server)
    {
        LoadConfig();
        lock (s_lock)
        {
            int idx = s_config.Servers.FindIndex(s => s.Id == server.Id);
            if (idx >= 0)
            {
                s_config.Servers[idx] = server;
            }
            else
            {
                s_config.Servers.Add(server);
                if (string.IsNullOrEmpty(s_config.ActiveServerId))
                {
                    s_config.ActiveServerId = server.Id;
                }
            }
            // 清理对应 Client 缓存
            s_clientCache.TryRemove(server.Id, out var oldClient);
            oldClient?.Dispose();
            SaveConfig();
        }
    }

    /// <summary>
    /// 根据标识符移除指定的 WebDAV 服务器并清理相关资源
    /// </summary>
    /// <param name="serverId">待移除的 WebDAV 服务器标识符。</param>
    public static void RemoveServer(string serverId)
    {
        LoadConfig();
        lock (s_lock)
        {
            s_config.Servers.RemoveAll(s => s.Id == serverId);
            if (s_config.ActiveServerId == serverId)
            {
                s_config.ActiveServerId = s_config.Servers.Count > 0 ? s_config.Servers[0].Id : null;
            }
            s_clientCache.TryRemove(serverId, out var oldClient);
            oldClient?.Dispose();
            SaveConfig();
        }
    }

    private static HttpClient GetHttpClient(WebDavServer server)
    {
        return s_clientCache.GetOrAdd(server.Id, _ =>
        {
            var handler = new SocketsHttpHandler
            {
                PooledConnectionLifetime = TimeSpan.FromMinutes(10),
                ConnectTimeout = TimeSpan.FromSeconds(10),
                AutomaticDecompression = DecompressionMethods.All
            };

            if (server.TrustSelfSigned)
            {
                handler.SslOptions.RemoteCertificateValidationCallback = (_, _, _, _) => true;
            }

            var client = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(30)
            };

            if (!string.IsNullOrEmpty(server.Username) || !string.IsNullOrEmpty(server.Password))
            {
                var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{server.Username}:{server.Password}"));
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", credentials);
            }

            client.DefaultRequestHeaders.Add("User-Agent", "QQMusicTui/1.0 WebDAV Client");
            return client;
        });
    }

    /// <summary>
    /// 测试指定 WebDAV 服务器的连通性与认证凭据有效性
    /// </summary>
    /// <param name="server">待测试的 WebDAV 服务器配置实例。</param>
    /// <returns>包含成功状态与提示信息的元组（Success 为 true 表示连接成功）。</returns>
    public static async Task<(bool Success, string Message)> TestConnectionAsync(WebDavServer server)
    {
        try
        {
            var client = GetHttpClient(server);
            var uri = BuildFullUri(server, server.RootPath);

            using var req = new HttpRequestMessage(new HttpMethod("PROPFIND"), uri);
            req.Headers.Add("Depth", "0");
            using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);

            if (resp.IsSuccessStatusCode || resp.StatusCode == HttpStatusCode.MultiStatus)
            {
                return (true, "连接成功，WebDAV 权限正常");
            }
            if (resp.StatusCode == HttpStatusCode.Unauthorized)
            {
                return (false, "认证失败：用户名或密码错误 (401)");
            }
            if (resp.StatusCode == HttpStatusCode.Forbidden)
            {
                return (false, "访问受限：无权限访问该路径 (403)");
            }
            if (resp.StatusCode == HttpStatusCode.NotFound)
            {
                return (false, "路径不存在：请检查根路径配置 (404)");
            }

            return (false, $"HTTP 错误状态码: {(int)resp.StatusCode} {resp.ReasonPhrase}");
        }
        catch (Exception ex)
        {
            return (false, $"连接异常: {ex.Message}");
        }
    }

    /// <summary>
    /// 发送 PROPFIND 请求获取指定远端 WebDAV 目录下的文件与子目录列表
    /// </summary>
    /// <param name="server">目标 WebDAV 服务器配置实例。</param>
    /// <param name="relativeHref">相对根路径的目录相对地址。</param>
    /// <returns>目录下的条目列表；请求失败或解析异常返回空列表。</returns>
    public static async Task<List<WebDavItem>> ListDirectoryAsync(WebDavServer server, string relativeHref)
    {
        List<WebDavItem> items = [];
        try
        {
            var client = GetHttpClient(server);
            var uri = BuildFullUri(server, relativeHref);

            using var req = new HttpRequestMessage(new HttpMethod("PROPFIND"), uri);
            req.Headers.Add("Depth", "1");

            const string propfindBody = """
            <?xml version="1.0" encoding="utf-8" ?>
            <D:propfind xmlns:D="DAV:">
              <D:prop>
                <D:displayname/>
                <D:resourcetype/>
                <D:getcontentlength/>
                <D:getlastmodified/>
              </D:prop>
            </D:propfind>
            """;
            req.Content = new StringContent(propfindBody, Encoding.UTF8, "application/xml");

            using var resp = await client.SendAsync(req);
            if (!resp.IsSuccessStatusCode && resp.StatusCode != HttpStatusCode.MultiStatus)
            {
                AppLogger.Warn("WebDavService", $"PROPFIND failed: {(int)resp.StatusCode} {resp.ReasonPhrase}");
                return items;
            }

            var xmlContent = await resp.Content.ReadAsStringAsync();
            var doc = XDocument.Parse(xmlContent);
            XNamespace d = "DAV:";

            var reqPath = Uri.UnescapeDataString(uri.AbsolutePath).TrimEnd('/');

            foreach (var respElem in doc.Descendants(d + "response"))
            {
                var hrefElem = respElem.Element(d + "href");
                if (hrefElem == null) continue;

                var rawHref = hrefElem.Value.Trim();
                Uri itemUri;
                if (Uri.TryCreate(rawHref, UriKind.Absolute, out var parsedAbs))
                {
                    itemUri = parsedAbs;
                }
                else
                {
                    itemUri = new Uri(uri, rawHref);
                }

                var itemPath = Uri.UnescapeDataString(itemUri.AbsolutePath).TrimEnd('/');

                if (string.Equals(itemPath, reqPath, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var propElem = respElem.Element(d + "propstat")?.Element(d + "prop");
                bool isDir = propElem?.Element(d + "resourcetype")?.Element(d + "collection") != null;

                var displayName = propElem?.Element(d + "displayname")?.Value?.Trim();
                if (string.IsNullOrEmpty(displayName))
                {
                    displayName = Path.GetFileName(itemPath);
                }
                if (string.IsNullOrEmpty(displayName))
                {
                    displayName = itemPath;
                }

                // 过滤隐藏文件与无关文件
                if (displayName.StartsWith('.') || displayName.StartsWith('@') || displayName.StartsWith('#'))
                {
                    continue;
                }

                long len = 0;
                var lenStr = propElem?.Element(d + "getcontentlength")?.Value;
                if (!string.IsNullOrEmpty(lenStr) && long.TryParse(lenStr, out var parsedLen))
                {
                    len = parsedLen;
                }

                DateTime? modDate = null;
                var modStr = propElem?.Element(d + "getlastmodified")?.Value;
                if (!string.IsNullOrEmpty(modStr) && DateTime.TryParse(modStr, out var dt))
                {
                    modDate = dt;
                }

                var storeHref = Uri.UnescapeDataString(itemUri.AbsolutePath);
                if (isDir && !storeHref.EndsWith('/'))
                {
                    storeHref += "/";
                }

                if (isDir)
                {
                    items.Add(new WebDavItem
                    {
                        Name = displayName,
                        Href = storeHref,
                        IsDirectory = true,
                        ContentLength = len,
                        LastModified = modDate
                    });
                }
                else
                {
                    var ext = Path.GetExtension(displayName);
                    if (s_supportedAudioExtensions.Contains(ext))
                    {
                        items.Add(new WebDavItem
                        {
                            Name = displayName,
                            Href = storeHref,
                            IsDirectory = false,
                            ContentLength = len,
                            LastModified = modDate
                        });
                    }
                }
            }

            // 文件夹排在前面，其余按文件名正序排列
            items.Sort((a, b) =>
            {
                if (a.IsDirectory != b.IsDirectory)
                {
                    return a.IsDirectory ? -1 : 1;
                }
                return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            });
        }
        catch (Exception ex)
        {
            AppLogger.Error("WebDavService", $"ListDirectoryAsync exception: {ex.Message}");
        }

        return items;
    }

    /// <summary>
    /// 递归扫描指定 WebDAV 目录下的所有音频资源条目并增量同步至服务器缓存
    /// </summary>
    /// <param name="server">目标 WebDAV 服务器配置实例。</param>
    /// <param name="folderHref">相对根路径的起始扫描目录。</param>
    /// <param name="progress">扫描进度状态回调委托；可为 null。</param>
    /// <returns>扫描完成后的有效音频文件条目总数。</returns>
    public static async Task<int> ScanFolderRecursiveAsync(WebDavServer server, string folderHref, Action<string>? progress = null)
    {
        List<WebDavItem> discovered = [];
        Queue<string> queue = new();
        queue.Enqueue(folderHref);

        progress?.Invoke("正在深度检索 WebDAV 目录树...");

        int newBatchCount = 0;

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            var list = await ListDirectoryAsync(server, current);
            List<WebDavItem> folderNewItems = [];

            foreach (var item in list)
            {
                if (item.IsDirectory)
                {
                    queue.Enqueue(item.Href);
                }
                else
                {
                    discovered.Add(item);
                    folderNewItems.Add(item);
                }
            }

            // 增量分批写入缓存并存盘，支持文件更新与变动检测
            if (folderNewItems.Count > 0)
            {
                lock (s_lock)
                {
                    server.CachedSongs ??= [];
                    foreach (var item in folderNewItems)
                    {
                        var existing = server.CachedSongs.Find(s => string.Equals(s.Href, item.Href, StringComparison.OrdinalIgnoreCase));
                        if (existing == null)
                        {
                            var parsed = InferTitleArtist(item.Name);
                            server.CachedSongs.Add(new WebDavSongCache
                            {
                                ServerId = server.Id,
                                Href = item.Href,
                                Title = parsed.Title,
                                Artist = parsed.Artist,
                                Album = "WebDAV 专辑",
                                Duration = 0,
                                Quality = InferQualityBadge(item.Name, null),
                                FileSize = item.ContentLength,
                                LastModified = item.LastModified
                            });
                            newBatchCount++;
                        }
                        else
                        {
                            bool sizeChanged = item.ContentLength > 0 && existing.FileSize != item.ContentLength;
                            bool timeChanged = item.LastModified.HasValue && existing.LastModified.HasValue && item.LastModified != existing.LastModified;
                            if (sizeChanged || timeChanged)
                            {
                                existing.FileSize = item.ContentLength;
                                existing.LastModified = item.LastModified;
                                if (!string.IsNullOrEmpty(existing.LocalCachedPath) && File.Exists(existing.LocalCachedPath))
                                {
                                    try { File.Delete(existing.LocalCachedPath); } catch { }
                                    existing.LocalCachedPath = null;
                                }
                                newBatchCount++;
                            }
                        }
                    }

                    if (newBatchCount >= 10)
                    {
                        SaveConfig();
                        newBatchCount = 0;
                    }
                }
            }

            int cachedCount = server.CachedSongs?.Count ?? discovered.Count;
            progress?.Invoke($"检索中... 发现 {discovered.Count} 首音频 (已录入曲库 {cachedCount} 首)");
        }

        lock (s_lock)
        {
            server.CachedSongs ??= [];
            foreach (var item in discovered)
            {
                var existing = server.CachedSongs.Find(s => string.Equals(s.Href, item.Href, StringComparison.OrdinalIgnoreCase));
                if (existing == null)
                {
                    var parsed = InferTitleArtist(item.Name);
                    server.CachedSongs.Add(new WebDavSongCache
                    {
                        ServerId = server.Id,
                        Href = item.Href,
                        Title = parsed.Title,
                        Artist = parsed.Artist,
                        Album = "WebDAV 专辑",
                        Duration = 0,
                        Quality = InferQualityBadge(item.Name, null),
                        FileSize = item.ContentLength,
                        LastModified = item.LastModified
                    });
                }
                else
                {
                    bool sizeChanged = item.ContentLength > 0 && existing.FileSize != item.ContentLength;
                    bool timeChanged = item.LastModified.HasValue && existing.LastModified.HasValue && item.LastModified != existing.LastModified;
                    if (sizeChanged || timeChanged)
                    {
                        existing.FileSize = item.ContentLength;
                        existing.LastModified = item.LastModified;
                        if (!string.IsNullOrEmpty(existing.LocalCachedPath) && File.Exists(existing.LocalCachedPath))
                        {
                            try { File.Delete(existing.LocalCachedPath); } catch { }
                            existing.LocalCachedPath = null;
                        }
                    }
                }
            }

            if (!server.ImportedPaths.Contains(folderHref))
            {
                server.ImportedPaths.Add(folderHref);
            }
            SaveConfig();
        }

        return server.CachedSongs?.Count ?? discovered.Count;
    }

    /// <summary>
    /// 打开远端 WebDAV 音频流（带 Range 请求头转发与 Basic Auth 凭据，用于服务端透明流式代理）
    /// </summary>
    /// <param name="server">目标 WebDAV 服务器配置实例。</param>
    /// <param name="relativeHref">WebDAV 远端音频资源的相对路径。</param>
    /// <param name="rangeHeader">HTTP Range 请求头值（如 bytes=0-1048575）；可为 null。</param>
    /// <param name="ct">用于取消异步操作的取消令牌。</param>
    /// <returns>打开的远程 WebDAV 音频文件 HTTP 响应报文。</returns>
    public static async Task<HttpResponseMessage> OpenAudioStreamAsync(WebDavServer server, string relativeHref, string? rangeHeader = null, CancellationToken ct = default)
    {
        var client = GetHttpClient(server);
        var uri = BuildFullUri(server, relativeHref);
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        if (!string.IsNullOrWhiteSpace(rangeHeader))
        {
            request.Headers.TryAddWithoutValidation("Range", rangeHeader);
        }
        return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
    }
}
