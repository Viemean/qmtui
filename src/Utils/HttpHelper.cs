using System;
using System.Net.Http;

namespace QmTui.Utils;

/// <summary>
/// 统一全局网络传输处理器与客户端生命周期管理
/// </summary>
public static class HttpHelper
{
    /// <summary>
    /// 创建配置统一连接池生命周期、连接超时与自动解压缩支持的 SocketsHttpHandler
    /// </summary>
    public static SocketsHttpHandler CreateDefaultHandler(TimeSpan? pooledLifetime = null, TimeSpan? connectTimeout = null)
    {
        return new SocketsHttpHandler
        {
            PooledConnectionLifetime = pooledLifetime ?? TimeSpan.FromMinutes(10),
            ConnectTimeout = connectTimeout ?? TimeSpan.FromSeconds(8),
            AutomaticDecompression = System.Net.DecompressionMethods.All
        };
    }

    /// <summary>
    /// 默认全局共享轻量级 HTTP 客户端（15 秒超时，用于封面下载、轻量请求）
    /// </summary>
    public static readonly HttpClient SharedClient = new(CreateDefaultHandler())
    {
        Timeout = TimeSpan.FromSeconds(15)
    };
}
