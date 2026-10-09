using System;
using System.Net.Http;

namespace QmTui.Utils;

/// <summary>
/// 统一全局网络传输处理器与客户端生命周期管理
/// </summary>
public static class HttpHelper
{
    /// <summary>
    /// 创建配置统一连接池生命周期、连接超时与自动解压缩支持的 SocketsHttpHandler。
    /// </summary>
    /// <param name="pooledLifetime">连接在连接池中的最大复用生命周期，默认 10 分钟。</param>
    /// <param name="connectTimeout">套接字建立连接的超时时间，默认 8 秒。</param>
    /// <returns>配置就绪的 <see cref="SocketsHttpHandler"/> 实例。</returns>
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
