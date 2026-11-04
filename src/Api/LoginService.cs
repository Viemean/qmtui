using System.Text;
using System.Text.Json;
using System.Net;
using System.Text.RegularExpressions;
using QmTui.Models;
using QmTui.Utils;

namespace QmTui.Api;

public sealed partial class LoginService
{
    private static readonly HttpClientHandler s_handler = new()
    {
        UseCookies = false,
        AllowAutoRedirect = false
    };

    private static readonly HttpClient s_http = new(s_handler)
    {
        Timeout = TimeSpan.FromSeconds(40)
    };

    private static readonly string s_loginReferer = "https://xui.ptlogin2.qq.com/cgi-bin/xlogin?appid=716027609&daid=383&style=33&login_text=%E7%99%BB%E5%BD%95&hide_title_bar=1&hide_border=1&target=self&s_url=https%3A%2F%2Fgraph.qq.com%2Foauth2.0%2Flogin_jump&pt_3rd_aid=100497308";

    static LoginService()
    {
        s_http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/128.0.0.0 Safari/537.36");
        s_http.DefaultRequestHeaders.Referrer = new Uri(s_loginReferer);
    }
    public enum QrLoginType
    {
        Qq,
        WeChat,
        OfficialApp
    }

    public enum QrLoginEvent
    {
        Done,
        Waiting,
        Confirming,
        Expired,
        Refused,
        Error
    }

    public sealed record QrCodeResult(byte[] ImageBytes, string MimeType, List<string> AsciiLines, QrLoginType Type, string Identifier);

    public sealed record PollStatus(QrLoginEvent Event, int Code, string Message);

    /// <summary>
    /// 获取登录方式的可读显示名称。
    /// </summary>
    /// <param name="type">扫码登录方式枚举。</param>
    /// <returns>登录方式的中文标识名称。</returns>
    public static string GetLoginTypeName(QrLoginType type) => type switch
    {
        QrLoginType.Qq => "QQ",
        QrLoginType.WeChat => "微信",
        QrLoginType.OfficialApp => "移动端 APP",
        _ => "QQ"
    };

    /// <summary>
    /// 拉取默认 QQ 渠道登录二维码。
    /// </summary>
    /// <param name="ct">异步操作取消令牌。</param>
    /// <returns>二维码元数据模型；失败或取消时返回 <see langword="null"/>。</returns>
    public static Task<QrCodeResult?> FetchQrCodeAsync(CancellationToken ct = default) =>
        FetchQrCodeAsync(QrLoginType.Qq, ct);

    /// <summary>
    /// 拉取指定渠道的登录二维码图像与会话标识。
    /// </summary>
    /// <param name="type">登录渠道枚举类型。</param>
    /// <param name="ct">异步操作取消令牌。</param>
    /// <returns>二维码元数据模型；失败或取消时返回 <see langword="null"/>。</returns>
    public static async Task<QrCodeResult?> FetchQrCodeAsync(QrLoginType type, CancellationToken ct = default)
    {
        try
        {
            var qr = type switch
            {
                QrLoginType.WeChat => await FetchWeChatQrCodeAsync(ct).ConfigureAwait(false),
                QrLoginType.OfficialApp => await FetchOfficialAppQrCodeAsync(ct).ConfigureAwait(false),
                _ => await FetchQqQrCodeAsync(ct).ConfigureAwait(false)
            };
            await TrySaveQrCodeAsync(qr.ImageBytes, qr.MimeType, ct).ConfigureAwait(false);
            return qr;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception ex)
        {
            AppLogger.Error("LoginService", $"FetchQrCodeAsync ({type}) failed", ex);
            return null;
        }
    }

    /// <summary>
    /// 轮询查询指定二维码的扫码状态与授权结果。
    /// </summary>
    /// <param name="qr">待轮询的二维码模型。</param>
    /// <param name="ct">异步操作取消令牌。</param>
    /// <returns>轮询状态模型，包含事件类型、状态码与描述提示。</returns>
    public static async Task<PollStatus> PollQrStatusAsync(QrCodeResult qr, CancellationToken ct = default)
    {
        try
        {
            return qr.Type switch
            {
                QrLoginType.WeChat => await PollWeChatQrStatusAsync(qr.Identifier, ct).ConfigureAwait(false),
                QrLoginType.OfficialApp => new PollStatus(QrLoginEvent.Error, -1, "移动端 APP 扫码状态由实时连接处理"),
                _ => await PollQqQrStatusAsync(qr.Identifier, ct).ConfigureAwait(false)
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return new PollStatus(QrLoginEvent.Error, -999, "操作已取消");
        }
        catch (Exception ex)
        {
            AppLogger.Error("LoginService", $"PollQrStatusAsync ({qr.Type}) failed", ex);
            return new PollStatus(QrLoginEvent.Error, -1, $"请求失败: {ex.Message}");
        }
    }

    private static async Task<QrCodeResult> FetchQqQrCodeAsync(CancellationToken ct)
    {
        var url = $"https://ssl.ptlogin2.qq.com/ptqrshow?appid=716027609&e=2&l=M&s=3&d=72&v=4&t={Random.Shared.NextDouble():F6}&daid=383&pt_3rd_aid=100497308";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Referrer = new Uri("https://xui.ptlogin2.qq.com/");
        using var resp = await s_http.SendAsync(req, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        var qrsig = GetSetCookieValue(resp, "qrsig");
        if (string.IsNullOrEmpty(qrsig)) throw new InvalidDataException("QQ 登录二维码缺少 qrsig");

        var bytes = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        return new QrCodeResult(bytes, "image/png", PngQrReader.DecodeToBlockText(bytes), QrLoginType.Qq, qrsig);
    }

    private static async Task<QrCodeResult> FetchWeChatQrCodeAsync(CancellationToken ct)
    {
        var redirectUri = Uri.EscapeDataString("https://y.qq.com/portal/wx_redirect.html?login_type=2&surl=https://y.qq.com/");
        var href = Uri.EscapeDataString("https://y.qq.com/mediastyle/music_v17/src/css/popup_wechat.css#wechat_redirect");
        var url = $"https://open.weixin.qq.com/connect/qrconnect?appid=wx48db31d50e334801&redirect_uri={redirectUri}&response_type=code&scope=snsapi_login&state=STATE&href={href}";
        using var pageResp = await s_http.GetAsync(url, ct).ConfigureAwait(false);
        pageResp.EnsureSuccessStatusCode();
        var page = await pageResp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var match = WeChatUuidRegex().Match(page);
        if (!match.Success) throw new InvalidDataException("微信登录二维码缺少 uuid");

        var uuid = match.Groups[1].Value;
        using var req = new HttpRequestMessage(HttpMethod.Get, $"https://open.weixin.qq.com/connect/qrcode/{uuid}");
        req.Headers.Referrer = new Uri("https://open.weixin.qq.com/connect/qrconnect");
        using var qrResp = await s_http.SendAsync(req, ct).ConfigureAwait(false);
        qrResp.EnsureSuccessStatusCode();
        var bytes = await qrResp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        return new QrCodeResult(bytes, "image/jpeg", PngQrReader.DecodeToBlockText(bytes), QrLoginType.WeChat, uuid);
    }

    private static async Task<QrCodeResult> FetchOfficialAppQrCodeAsync(CancellationToken ct)
    {
        const string payload = "{\"comm\":{\"ct\":11,\"cv\":14090008},\"req_0\":{\"module\":\"music.login.LoginServer\",\"method\":\"CreateQRCode\",\"param\":{\"tmeAppID\":\"qqmusic\",\"ct\":11,\"cv\":14090008}}}";
        using var req = new HttpRequestMessage(HttpMethod.Post, "https://u.y.qq.com/cgi-bin/musicu.fcg");
        req.Content = new StringContent(payload, Encoding.UTF8, "application/json");
        req.Headers.TryAddWithoutValidation("User-Agent", "QQMusic 14090008(android 14)");
        using var resp = await s_http.SendAsync(req, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        var data = doc.RootElement.GetProperty("req_0").GetProperty("data");
        var dataUrl = data.GetProperty("qrcode").GetString() ?? "";
        var identifier = data.GetProperty("qrcodeID").GetString() ?? "";
        var comma = dataUrl.IndexOf(',');
        if (comma < 0 || string.IsNullOrEmpty(identifier)) throw new InvalidDataException("登录二维码响应不完整");

        var bytes = Convert.FromBase64String(dataUrl[(comma + 1)..]);
        return new QrCodeResult(bytes, "image/png", PngQrReader.DecodeToBlockText(bytes), QrLoginType.OfficialApp, identifier);
    }

    private static async Task<PollStatus> PollQqQrStatusAsync(string qrsig, CancellationToken ct)
    {
        var token = HashPtqrToken(qrsig);
        var ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var url = $"https://ssl.ptlogin2.qq.com/ptqrlogin?u1=https%3A%2F%2Fgraph.qq.com%2Foauth2.0%2Flogin_jump&ptqrtoken={token}&ptredirect=0&h=1&t=1&g=1&from_ui=1&ptlang=2052&action=0-0-{ts}&js_ver=20102616&js_type=1&pt_uistyle=40&aid=716027609&daid=383&pt_3rd_aid=100497308&has_onekey=1";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Add("Cookie", $"qrsig={qrsig};");
        req.Headers.Referrer = new Uri("https://xui.ptlogin2.qq.com/");
        using var resp = await s_http.SendAsync(req, ct).ConfigureAwait(false);
        var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var matches = SingleQuoteRegex().Matches(text);
        if (matches.Count < 5) return new PollStatus(QrLoginEvent.Error, -1, "响应解析异常");

        var code = int.TryParse(matches[0].Groups[1].Value, out var parsed) ? parsed : -1;
        var message = matches[4].Groups[1].Value;
        if (code == 0)
        {
            var redirectUrl = matches[2].Groups[1].Value;
            var nick = matches.Count >= 6 ? matches[5].Groups[1].Value : "";
            await ExchangeCheckSigCookiesAsync(redirectUrl, qrsig, nick, ct).ConfigureAwait(false);
        }
        return new PollStatus(MapQrEvent(code), code, message);
    }

    private static async Task<PollStatus> PollWeChatQrStatusAsync(string uuid, CancellationToken ct)
    {
        var ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        using var req = new HttpRequestMessage(HttpMethod.Get, $"https://lp.open.weixin.qq.com/connect/l/qrconnect?uuid={Uri.EscapeDataString(uuid)}&_={ts}");
        req.Headers.Referrer = new Uri("https://open.weixin.qq.com/");
        using var resp = await s_http.SendAsync(req, ct).ConfigureAwait(false);
        var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var match = WeChatStatusRegex().Match(text);
        if (!match.Success) return new PollStatus(QrLoginEvent.Error, -1, "微信扫码状态解析异常");

        var code = int.Parse(match.Groups[1].Value);
        if (code == 405)
        {
            var credentialCode = match.Groups[2].Value;
            if (string.IsNullOrEmpty(credentialCode) || !await ExchangeWeChatCodeAsync(credentialCode, ct).ConfigureAwait(false))
            {
                return new PollStatus(QrLoginEvent.Error, -1, "微信授权凭证交换失败");
            }
        }
        return new PollStatus(MapQrEvent(code), code, GetQrStatusMessage(code));
    }

    private static QrLoginEvent MapQrEvent(int code) => code switch
    {
        0 or 405 => QrLoginEvent.Done,
        67 or 404 => QrLoginEvent.Confirming,
        65 or 402 => QrLoginEvent.Expired,
        68 or 403 => QrLoginEvent.Refused,
        66 or 408 => QrLoginEvent.Waiting,
        _ => QrLoginEvent.Error
    };

    private static string GetQrStatusMessage(int code) => MapQrEvent(code) switch
    {
        QrLoginEvent.Done => "登录成功",
        QrLoginEvent.Confirming => "已扫码，请在手机上确认授权...",
        QrLoginEvent.Expired => "二维码已失效",
        QrLoginEvent.Refused => "已取消登录",
        QrLoginEvent.Waiting => "等待手机扫码...",
        _ => "登录状态异常"
    };

    private static string GetSetCookieValue(HttpResponseMessage response, string name)
    {
        if (!response.Headers.TryGetValues("Set-Cookie", out var values)) return "";
        foreach (var value in values)
        {
            var prefix = name + "=";
            var start = value.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
            if (start < 0) continue;
            start += prefix.Length;
            var end = value.IndexOf(';', start);
            return end < 0 ? value[start..] : value[start..end];
        }
        return "";
    }

    private static async Task TrySaveQrCodeAsync(byte[] bytes, string mimeType, CancellationToken ct)
    {
        try
        {
            var extension = mimeType == "image/jpeg" ? "jpg" : "png";
            var targetPath = Path.Combine(Path.GetTempPath(), $"qmtui_login_qr.{extension}");
            await File.WriteAllBytesAsync(targetPath, bytes, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AppLogger.Debug("LoginService", $"Unable to save temporary QR image: {ex.Message}");
        }
    }



    /// <summary>
    /// 请求 check_sig 换取最终登录 Session Cookie
    /// </summary>
    private static async Task ExchangeCheckSigCookiesAsync(string redirectUrl, string qrsig, string nick, CancellationToken ct)
    {
        Dictionary<string, string> cookieDict = [];
        string uin = "";

        // 尝试从 redirectUrl 的 query 参数中直接提取 uin
        if (!string.IsNullOrEmpty(redirectUrl))
        {
            var uinMatch = UinQueryRegex().Match(redirectUrl);
            if (uinMatch.Success)
            {
                uin = uinMatch.Groups[1].Value.TrimStart('o');
                AppLogger.Info("LoginService", $"Extracted uin from redirectUrl query: {uin}");
            }
        }

        // 发起 check_sig 请求
        if (!string.IsNullOrEmpty(redirectUrl))
        {
            try
            {
                AppLogger.Info("LoginService", $"Requesting check_sig URL: {redirectUrl}");
                using var checkReq = new HttpRequestMessage(HttpMethod.Get, redirectUrl);
                checkReq.Headers.Add("Cookie", $"qrsig={qrsig};");

                using var checkResp = await s_http.SendAsync(checkReq, ct).ConfigureAwait(false);
                AppLogger.Info("LoginService", $"check_sig response status: {checkResp.StatusCode}");

                if (checkResp.Headers.TryGetValues("Set-Cookie", out var setCookies))
                {
                    foreach (var sc in setCookies)
                    {
                        if (sc.Contains("1970 00:00:00") || sc.Contains("Max-Age=0")) continue;

                        var parts = sc.Split(';')[0].Split('=', 2);
                        if (parts.Length == 2)
                        {
                            var k = parts[0].Trim();
                            var v = parts[1].Trim();
                            if (!string.IsNullOrEmpty(v))
                            {
                                cookieDict[k] = v;
                                if (k.Equals("uin", StringComparison.OrdinalIgnoreCase) && string.IsNullOrEmpty(uin))
                                {
                                    uin = v.TrimStart('o');
                                }
                            }
                        }
                    }
                }

                // 尝试跟进 302 跳转提取更多凭据
                if (checkResp.Headers.Location != null)
                {
                    var jumpUrl = checkResp.Headers.Location.ToString();
                    AppLogger.Info("LoginService", $"Following check_sig redirect: {jumpUrl}");
                    var cookieHeader = string.Join("; ", cookieDict.Select(kv => $"{kv.Key}={kv.Value}"));
                    using var jumpReq = new HttpRequestMessage(HttpMethod.Get, jumpUrl);
                    jumpReq.Headers.Add("Cookie", cookieHeader);
                    using var jumpResp = await s_http.SendAsync(jumpReq, ct).ConfigureAwait(false);
                    if (jumpResp.Headers.TryGetValues("Set-Cookie", out var jumpCookies))
                    {
                        foreach (var sc in jumpCookies)
                        {
                            if (sc.Contains("1970 00:00:00") || sc.Contains("Max-Age=0")) continue;
                            var parts = sc.Split(';')[0].Split('=', 2);
                            if (parts.Length == 2 && !string.IsNullOrEmpty(parts[1].Trim()))
                            {
                                cookieDict[parts[0].Trim()] = parts[1].Trim();
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                AppLogger.Error("LoginService", "Exception during check_sig exchange", ex);
            }
        }

        // 提取 skey / p_skey
        string skey = cookieDict.TryGetValue("skey", out var s) ? s : "";
        if (string.IsNullOrEmpty(skey) && cookieDict.TryGetValue("p_skey", out var ps))
        {
            skey = ps;
        }

        // 补充音乐平台必要 Cookie
        if (!string.IsNullOrEmpty(uin))
        {
            cookieDict["uin"] = uin;
            cookieDict["qqmusic_uin"] = uin;
        }
        if (!string.IsNullOrEmpty(skey))
        {
            cookieDict["qqmusic_key"] = skey;
        }

        AppLogger.Info("LoginService", "Finalizing Base Session");

        // 写入当前基础会话
        UserSession.Current.Uin = uin;
        UserSession.Current.Nick = string.IsNullOrWhiteSpace(nick) ? (string.IsNullOrEmpty(uin) ? "已登录用户" : uin) : nick;
        UserSession.Current.MusicKey = skey;
        UserSession.Current.Cookies = cookieDict;
        UserSession.Current.IsVip = false;
        UserSession.Current.Save();

        // 自动触发第二阶段：向平台申请 OAuth2 Code 并换取专属 musickey 完整凭据
        AppLogger.Info("LoginService", "Starting Phase 2: Automatically exchanging OAuth2 Code for full VIP musickey...");
        await ExchangeMusicKeyByOAuthAsync(cookieDict, ct).ConfigureAwait(false);
    }

    private static async Task<bool> ExchangeWeChatCodeAsync(string code, CancellationToken ct)
    {
        var escapedCode = JsonEncodedText.Encode(code).ToString();
        var payload = "{\"comm\":{\"ct\":11,\"cv\":14090008,\"v\":14090008,\"chid\":\"10003505\",\"tmeAppID\":\"qqmusic\",\"tmeLoginType\":1}," +
            "\"req_0\":{\"module\":\"music.login.LoginServer\",\"method\":\"Login\",\"param\":{\"code\":\"" + escapedCode + "\",\"strAppid\":\"wx48db31d50e334801\"}}}";
        return await ExchangeDirectCredentialAsync(payload, QrLoginType.WeChat, ct).ConfigureAwait(false);
    }

    private static async Task<bool> ExchangeDirectCredentialAsync(string payload, QrLoginType type, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "https://u.y.qq.com/cgi-bin/musicu.fcg");
        req.Content = new StringContent(payload, Encoding.UTF8, "application/json");
        req.Headers.TryAddWithoutValidation("User-Agent", "QQMusic 14090008(android 14)");
        using var resp = await s_http.SendAsync(req, ct).ConfigureAwait(false);
        var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("req_0", out var login) ||
            !login.TryGetProperty("code", out var codeElement) ||
            codeElement.GetInt32() != 0 ||
            !login.TryGetProperty("data", out var data))
        {
            AppLogger.Error("LoginService", $"{type} credential exchange failed");
            return false;
        }

        return await SaveCredentialAsync(data, type, ct).ConfigureAwait(false);
    }

    private static async Task<bool> SaveCredentialAsync(JsonElement data, QrLoginType type, CancellationToken ct)
    {
        var musicId = GetJsonString(data, "str_musicid");
        if (string.IsNullOrEmpty(musicId)) musicId = GetJsonString(data, "musicid");
        var musicKey = GetJsonString(data, "musickey");
        if (string.IsNullOrEmpty(musicId) || string.IsNullOrEmpty(musicKey)) return false;

        var rawLoginType = GetJsonString(data, "tmeLoginType");
        var isWeChat = type == QrLoginType.WeChat || rawLoginType == "1" || musicKey.StartsWith("W_X_");
        var actualLoginType = isWeChat ? "1" : "2";

        var cookies = new Dictionary<string, string>
        {
            ["musicid"] = musicId,
            ["uin"] = musicId,
            ["qqmusic_uin"] = musicId,
            ["qqmusic_key"] = musicKey,
            ["qm_keyst"] = musicKey,
            ["musickey"] = musicKey,
            ["qqmusic_version"] = "17",
            ["qqmusic_miniversion"] = "70",
            ["tmeLoginType"] = actualLoginType
        };

        var openid = GetJsonString(data, isWeChat ? "psrf_wxopenid" : "psrf_qqopenid");
        if (string.IsNullOrEmpty(openid)) openid = GetJsonString(data, "openid");
        if (!string.IsNullOrEmpty(openid))
        {
            cookies["openid"] = openid;
            cookies[isWeChat ? "psrf_wxopenid" : "psrf_qqopenid"] = openid;
        }

        var accessToken = GetJsonString(data, isWeChat ? "psrf_wx_access_token" : "psrf_qqaccess_token");
        if (string.IsNullOrEmpty(accessToken)) accessToken = GetJsonString(data, "access_token");
        if (!string.IsNullOrEmpty(accessToken))
        {
            cookies["access_token"] = accessToken;
            cookies[isWeChat ? "psrf_wx_access_token" : "psrf_qqaccess_token"] = accessToken;
        }

        var refreshToken = GetJsonString(data, "refresh_token");
        if (string.IsNullOrEmpty(refreshToken)) refreshToken = GetJsonString(data, isWeChat ? "psrf_wxrefresh_token" : "psrf_qqrefresh_token");
        if (!string.IsNullOrEmpty(refreshToken))
        {
            cookies["refresh_token"] = refreshToken;
            cookies[isWeChat ? "psrf_wxrefresh_token" : "psrf_qqrefresh_token"] = refreshToken;
        }

        AddCredentialCookie(data, cookies, "refresh_key", "refresh_key");

        var unionid = GetJsonString(data, isWeChat ? "psrf_wxunionid" : "psrf_qqunionid");
        if (string.IsNullOrEmpty(unionid)) unionid = GetJsonString(data, "unionid");
        if (!string.IsNullOrEmpty(unionid))
        {
            cookies["unionid"] = unionid;
            cookies[isWeChat ? "psrf_wxunionid" : "psrf_qqunionid"] = unionid;
        }

        UserSession.Current.Uin = musicId;
        UserSession.Current.Nick = GetJsonString(data, "nick");
        if (string.IsNullOrWhiteSpace(UserSession.Current.Nick)) UserSession.Current.Nick = $"用户_{musicId}";
        UserSession.Current.MusicKey = musicKey;
        UserSession.Current.Cookies = cookies;
        UserSession.Current.Save();
        await MusicApi.RefreshCurrentUserProfileAsync(ct).ConfigureAwait(false);
        AppLogger.Info("LoginService", $"{GetLoginTypeName(type)} login credentials stored successfully");
        return true;
    }

    private static void AddCredentialCookie(JsonElement data, Dictionary<string, string> cookies, string property, string cookieName)
    {
        var value = GetJsonString(data, property);
        if (!string.IsNullOrEmpty(value)) cookies[cookieName] = value;
    }

    private static string GetJsonString(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value)) return "";
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? "",
            JsonValueKind.Number => value.GetRawText(),
            _ => ""
        };
    }

    /// <summary>
    /// 确保存在有效的 musickey，若缺失则自动通过 OAuth2/RefreshToken 续期
    /// </summary>
    /// <param name="ct">取消令牌。</param>
    /// <returns>若成功确保或刷新 musickey 有效则为 true；否则为 false。</returns>
    public static Task<bool> EnsureMusicKeyAsync(CancellationToken ct = default) =>
        EnsureMusicKeyAsync(false, ct);

    private static readonly SemaphoreSlim s_refreshMutex = new(1, 1);

    /// <summary>
    /// 确保存在有效的 musickey，若缺失或强制刷新则自动执行 OAuth2/RefreshToken 续期
    /// </summary>
    /// <param name="forceRefresh">是否强制刷新票据。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>若成功确保或刷新 musickey 有效则为 true；否则为 false。</returns>
    public static async Task<bool> EnsureMusicKeyAsync(bool forceRefresh, CancellationToken ct = default)
    {
        if (!forceRefresh && UserSession.Current.Cookies.TryGetValue("qm_keyst", out var mk) && !string.IsNullOrEmpty(mk))
        {
            return true;
        }

        await s_refreshMutex.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!forceRefresh && UserSession.Current.Cookies.TryGetValue("qm_keyst", out var keyAfterLock) && !string.IsNullOrEmpty(keyAfterLock))
            {
                return true;
            }

            var cookies = UserSession.Current.Cookies;
            var currentKey = UserSession.Current.MusicKey;
            if (string.IsNullOrEmpty(currentKey) && cookies.TryGetValue("qm_keyst", out var mk1)) currentKey = mk1;
            if (string.IsNullOrEmpty(currentKey) && cookies.TryGetValue("musickey", out var mk2)) currentKey = mk2;

            var currentUin = UserSession.Current.Uin;
            if (string.IsNullOrEmpty(currentUin) && cookies.TryGetValue("musicid", out var mid1)) currentUin = mid1;
            if (string.IsNullOrEmpty(currentUin) && cookies.TryGetValue("uin", out var mid2)) currentUin = mid2;

            var isWeChat = (cookies.TryGetValue("tmeLoginType", out var lt) && lt == "1") ||
                           (!string.IsNullOrEmpty(currentKey) && currentKey.StartsWith("W_X_"));

            var refreshKey = cookies.TryGetValue("refresh_key", out var rk) ? rk : "";
            var rawLoginType = cookies.TryGetValue("tmeLoginType", out var rlt) && int.TryParse(rlt, out var lti) ? lti : (isWeChat ? 1 : 2);

            var refreshToken = cookies.TryGetValue("refresh_token", out var rt) && !string.IsNullOrEmpty(rt) ? rt
                : (isWeChat
                    ? (cookies.TryGetValue("psrf_wxrefresh_token", out var wxrt) ? wxrt : "")
                    : (cookies.TryGetValue("psrf_qqrefresh_token", out var qqrt) ? qqrt : ""));

            var openid = isWeChat
                ? (cookies.TryGetValue("openid", out var wxop) && !string.IsNullOrEmpty(wxop) ? wxop : (cookies.TryGetValue("psrf_wxopenid", out var pop) ? pop : ""))
                : (cookies.TryGetValue("psrf_qqopenid", out var qqop) && !string.IsNullOrEmpty(qqop) ? qqop : (cookies.TryGetValue("openid", out var op) ? op : ""));

            var accessToken = isWeChat
                ? (cookies.TryGetValue("access_token", out var wxat) && !string.IsNullOrEmpty(wxat) ? wxat : (cookies.TryGetValue("psrf_wx_access_token", out var pat) ? pat : ""))
                : (cookies.TryGetValue("psrf_qqaccess_token", out var qqat) && !string.IsNullOrEmpty(qqat) ? qqat : (cookies.TryGetValue("access_token", out var at) ? at : ""));

            // 1. 统一账户网关续签通道 (覆盖官方 App 扫码、微信扫码与 QQ 互联扫码)
            if (!string.IsNullOrEmpty(refreshToken) && !string.IsNullOrEmpty(currentUin))
            {
                AppLogger.Info("LoginService", "EnsureMusicKeyAsync: Attempting token refresh via music.login.LoginServer (Channel 1)...");
                if (await RefreshOfficialAppTokenAsync(currentUin, refreshToken, refreshKey, rawLoginType, openid, accessToken, ct).ConfigureAwait(false))
                {
                    return true;
                }
            }

            // 2. 微信登录专有换票重试
            if (isWeChat && !string.IsNullOrEmpty(refreshToken) && !string.IsNullOrEmpty(currentUin))
            {
                AppLogger.Info("LoginService", "EnsureMusicKeyAsync: Attempting token refresh via WeChat LoginServer (Channel 2)...");
                if (await RefreshWeChatLoginTokenAsync(currentUin, currentKey ?? "", refreshToken, openid, ct).ConfigureAwait(false))
                {
                    return true;
                }
            }

            // 3. QQ 互联 OAuth 降级换票兜底 (非微信账号且具备 openid 与 accessToken)
            if (!isWeChat && !string.IsNullOrEmpty(openid) && !string.IsNullOrEmpty(accessToken))
            {
                var qqRefreshToken = cookies.TryGetValue("psrf_qqrefresh_token", out var qrt) && !string.IsNullOrEmpty(qrt) ? qrt
                    : (cookies.TryGetValue("refresh_token", out var grt) ? grt : "");
                AppLogger.Info("LoginService", "EnsureMusicKeyAsync: Attempting token refresh via QQConnectLogin.LoginServer (Channel 3)...");
                if (await RefreshQQLoginTokenAsync(openid, accessToken, qqRefreshToken, currentUin, currentKey ?? "", ct).ConfigureAwait(false))
                {
                    return true;
                }
            }

            return false;
        }
        finally
        {
            s_refreshMutex.Release();
        }
    }

    /// <summary>
    /// 使用当前已有的 musickey 与 uin 申请 90 天的凭据续期
    /// </summary>
    /// <param name="uin">用户账号标识。</param>
    /// <param name="musickey">音乐会话凭据密钥。</param>
    /// <param name="ct">异步操作取消令牌。</param>
    /// <returns>若凭据续期成功返回 <see langword="true"/>；否则返回 <see langword="false"/>。</returns>
    public static async Task<bool> RefreshByExistingMusicKeyAsync(string uin, string musickey, CancellationToken ct = default)
    {
        try
        {
            var cleanUin = uin.TrimStart('o');
            var payload = $"{{\"comm\":{{\"ct\":11,\"cv\":14090008,\"v\":14090008,\"platform\":\"android\"}},\"login\":{{\"module\":\"QQConnectLogin.LoginServer\",\"method\":\"QQLogin\",\"param\":{{\"musicid\":\"{cleanUin}\",\"musickey\":\"{musickey}\",\"expired_in\":7776000}}}}}}";

            using var req = new HttpRequestMessage(HttpMethod.Post, "https://u.y.qq.com/cgi-bin/musicu.fcg");
            req.Content = new StringContent(payload, Encoding.UTF8, "application/json");
            req.Headers.TryAddWithoutValidation("User-Agent", "QQMusic 14090008(android 14)");

            var cookieHeader = UserSession.Current.GetCookieHeader();
            if (!string.IsNullOrEmpty(cookieHeader))
            {
                req.Headers.TryAddWithoutValidation("Cookie", cookieHeader);
            }

            using var resp = await s_http.SendAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return false;

            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("login", out var loginObj) &&
                loginObj.TryGetProperty("code", out var codeProp) && codeProp.GetInt32() == 0 &&
                loginObj.TryGetProperty("data", out var loginData))
            {
                return await ApplyLoginDataAsync(loginData, UserSession.Current.Cookies, ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            AppLogger.Debug("LoginService", $"RefreshByExistingMusicKey exception: {ex.Message}");
        }
        return false;
    }

    /// <summary>
    /// 通过 QQConnectLogin.LoginServer 使用 access_token 与 refresh_token 续期 musickey
    /// </summary>
    /// <param name="openid">QQ 互联 openid。</param>
    /// <param name="accessToken">QQ 互联 access_token。</param>
    /// <param name="refreshToken">QQ 互联 refresh_token。</param>
    /// <param name="musicId">用户标识。</param>
    /// <param name="musicKey">当前生效密钥。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>若续期成功则为 true；否则为 false。</returns>
    public static async Task<bool> RefreshQQLoginTokenAsync(
        string openid,
        string accessToken,
        string refreshToken = "",
        string musicId = "",
        string musicKey = "",
        CancellationToken ct = default)
    {
        try
        {
            var musicLoginUrl = "https://u.y.qq.com/cgi-bin/musicu.fcg";
            var cleanMusicId = (string.IsNullOrEmpty(musicId) ? UserSession.Current.Uin : musicId).TrimStart('o');
            var currentKey = string.IsNullOrEmpty(musicKey) ? UserSession.Current.MusicKey : musicKey;
            var currentRefreshToken = !string.IsNullOrEmpty(refreshToken) ? refreshToken
                : (UserSession.Current.Cookies.TryGetValue("refresh_token", out var rt) ? rt
                : (UserSession.Current.Cookies.TryGetValue("psrf_qqrefresh_token", out var qrt) ? qrt : ""));

            var sb = new StringBuilder(512);
            sb.Append("{\"comm\":{\"ct\":19,\"cv\":1,\"tmeLoginType\":\"1\"},\"login\":{\"module\":\"QQConnectLogin.LoginServer\",\"method\":\"QQLogin\",\"param\":{");
            sb.Append("\"openid\":\"").Append(JsonEncodedText.Encode(openid)).Append("\",");
            sb.Append("\"access_token\":\"").Append(JsonEncodedText.Encode(accessToken)).Append("\",");
            if (!string.IsNullOrEmpty(currentRefreshToken))
            {
                sb.Append("\"refresh_token\":\"").Append(JsonEncodedText.Encode(currentRefreshToken)).Append("\",");
            }
            if (!string.IsNullOrEmpty(cleanMusicId))
            {
                if (long.TryParse(cleanMusicId, out var numUin) && numUin > 0L)
                {
                    sb.Append("\"musicid\":").Append(numUin).Append(',');
                }
                else
                {
                    sb.Append("\"musicid\":\"").Append(JsonEncodedText.Encode(cleanMusicId)).Append("\",");
                }
            }
            if (!string.IsNullOrEmpty(currentKey))
            {
                sb.Append("\"musickey\":\"").Append(JsonEncodedText.Encode(currentKey)).Append("\",");
            }
            sb.Append("\"onlyNeedAccessToken\":0,");
            sb.Append("\"forceRefreshToken\":").Append(string.IsNullOrEmpty(currentRefreshToken) ? '1' : '0').Append(',');
            sb.Append("\"appid\":100497308}}}");
            var payload = sb.ToString();

            using var loginReq = new HttpRequestMessage(HttpMethod.Post, musicLoginUrl);
            loginReq.Content = new StringContent(payload, Encoding.UTF8, "application/json");
            loginReq.Headers.TryAddWithoutValidation("Origin", "https://y.qq.com");
            var cookieHeader = UserSession.Current.GetCookieHeader();
            if (!string.IsNullOrEmpty(cookieHeader))
            {
                loginReq.Headers.TryAddWithoutValidation("Cookie", cookieHeader);
            }
            loginReq.Headers.Referrer = new Uri("https://y.qq.com/");

            using var loginResp = await s_http.SendAsync(loginReq, ct).ConfigureAwait(false);
            var loginRespJson = await loginResp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            using var doc = JsonDocument.Parse(loginRespJson);
            var root = doc.RootElement;
            if (root.TryGetProperty("login", out var loginObj) &&
                loginObj.TryGetProperty("code", out var codeProp) && codeProp.GetInt32() == 0 &&
                loginObj.TryGetProperty("data", out var loginData))
            {
                return await ApplyLoginDataAsync(loginData, UserSession.Current.Cookies, ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            AppLogger.Warn("LoginService", $"RefreshQQLoginTokenAsync exception: {ex.Message}");
        }
        return false;
    }

    /// <summary>
    /// 使用微信登录下发的 refresh_token 与 openid 刷新 musickey 凭据。
    /// </summary>
    public static async Task<bool> RefreshWeChatLoginTokenAsync(
        string musicId,
        string musicKey,
        string refreshToken,
        string openid = "",
        CancellationToken ct = default)
    {
        try
        {
            var cleanUin = musicId.TrimStart('o');
            long.TryParse(cleanUin, out var numericUin);

            var sb = new StringBuilder(512);
            sb.Append("{\"comm\":{\"ct\":11,\"cv\":14090008,\"v\":14090008,\"chid\":\"10003505\",\"tmeAppID\":\"qqmusic\",\"tmeLoginType\":1},");
            sb.Append("\"req_0\":{\"module\":\"music.login.LoginServer\",\"method\":\"Login\",\"param\":{");
            sb.Append("\"openid\":\"").Append(JsonEncodedText.Encode(openid)).Append("\",");
            sb.Append("\"refresh_token\":\"").Append(JsonEncodedText.Encode(refreshToken)).Append("\",");
            sb.Append("\"str_musicid\":\"").Append(JsonEncodedText.Encode(musicId)).Append("\",");
            if (numericUin > 0L)
            {
                sb.Append("\"musicid\":").Append(numericUin).Append(',');
            }
            sb.Append("\"musickey\":\"").Append(JsonEncodedText.Encode(musicKey)).Append("\",");
            sb.Append("\"unionid\":\"\",\"refresh_key\":\"\",\"loginMode\":2}}}");
            var payload = sb.ToString();

            using var req = new HttpRequestMessage(HttpMethod.Post, "https://u.y.qq.com/cgi-bin/musicu.fcg");
            req.Content = new StringContent(payload, Encoding.UTF8, "application/json");
            req.Headers.TryAddWithoutValidation("User-Agent", "QQMusic 14090008(android 14)");

            using var resp = await s_http.SendAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return false;
            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("req_0", out var req0) ||
                !req0.TryGetProperty("code", out var codeElem) ||
                codeElem.GetInt32() != 0 ||
                !req0.TryGetProperty("data", out var data))
            {
                AppLogger.Warn("LoginService", "RefreshWeChatLoginToken failed");
                return false;
            }

            var newMusicKey = GetJsonString(data, "musickey");
            if (string.IsNullOrEmpty(newMusicKey)) return false;

            var cookies = UserSession.Current.Cookies;
            cookies["musickey"] = newMusicKey;
            cookies["qm_keyst"] = newMusicKey;
            cookies["qqmusic_key"] = newMusicKey;
            cookies["tmeLoginType"] = "1";

            var newRefreshToken = GetJsonString(data, "refresh_token");
            if (string.IsNullOrEmpty(newRefreshToken)) newRefreshToken = GetJsonString(data, "psrf_wxrefresh_token");
            if (string.IsNullOrEmpty(newRefreshToken)) newRefreshToken = refreshToken;
            if (!string.IsNullOrEmpty(newRefreshToken))
            {
                cookies["refresh_token"] = newRefreshToken;
                cookies["psrf_wxrefresh_token"] = newRefreshToken;
            }

            var retOpenid = GetJsonString(data, "openid");
            if (string.IsNullOrEmpty(retOpenid)) retOpenid = openid;
            if (!string.IsNullOrEmpty(retOpenid))
            {
                cookies["openid"] = retOpenid;
                cookies["psrf_wxopenid"] = retOpenid;
            }

            var retAccessToken = GetJsonString(data, "access_token");
            if (string.IsNullOrEmpty(retAccessToken)) retAccessToken = GetJsonString(data, "psrf_wx_access_token");
            if (!string.IsNullOrEmpty(retAccessToken))
            {
                cookies["access_token"] = retAccessToken;
                cookies["psrf_wx_access_token"] = retAccessToken;
            }

            UserSession.Current.MusicKey = newMusicKey;
            UserSession.Current.Save();
            await MusicApi.RefreshCurrentUserProfileAsync(ct).ConfigureAwait(false);
            AppLogger.Info("LoginService", "Refreshed WeChat credentials stored successfully");
            return true;
        }
        catch (Exception ex)
        {
            AppLogger.Warn("LoginService", $"RefreshWeChatLoginToken exception: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 使用移动端官方 App 登录下发的 refresh_token 与 refresh_key 刷新 musickey 凭据。
    /// </summary>
    /// <param name="musicId">用户识别标识（musicid 或 uin）。</param>
    /// <param name="refreshToken">官方登录下发的长效刷新令牌。</param>
    /// <param name="refreshKey">官方登录下发的刷新密钥。</param>
    /// <param name="loginType">登录类型（1 为微信，2 为 QQ）。</param>
    /// <param name="openid">第三方 openid 标识。</param>
    /// <param name="accessToken">第三方 access_token 标识。</param>
    /// <param name="ct">异步操作取消令牌。</param>
    /// <returns>若刷新成功并更新凭据返回 <see langword="true"/>；否则返回 <see langword="false"/>。</returns>
    public static async Task<bool> RefreshOfficialAppTokenAsync(
        string musicId,
        string refreshToken,
        string refreshKey = "",
        int loginType = 2,
        string openid = "",
        string accessToken = "",
        CancellationToken ct = default)
    {
        try
        {
            var cleanId = musicId.TrimStart('o');
            if (!long.TryParse(cleanId, out var numericUin)) return false;

            var cookies = UserSession.Current.Cookies;
            var currentMusicKey = UserSession.Current.MusicKey;
            if (string.IsNullOrEmpty(currentMusicKey) && cookies.TryGetValue("qm_keyst", out var mk)) currentMusicKey = mk;
            if (string.IsNullOrEmpty(currentMusicKey) && cookies.TryGetValue("musickey", out var mk2)) currentMusicKey = mk2;

            var isWeChat = (cookies.TryGetValue("tmeLoginType", out var lt) && lt == "1") ||
                           (!string.IsNullOrEmpty(currentMusicKey) && currentMusicKey.StartsWith("W_X_")) ||
                           loginType == 1;
            var actualLoginType = isWeChat ? 1 : 2;

            var targetOpenid = openid;
            if (string.IsNullOrEmpty(targetOpenid))
            {
                targetOpenid = isWeChat
                    ? (cookies.TryGetValue("openid", out var wxop) && !string.IsNullOrEmpty(wxop) ? wxop : (cookies.TryGetValue("psrf_wxopenid", out var pop) ? pop : ""))
                    : (cookies.TryGetValue("psrf_qqopenid", out var qqop) && !string.IsNullOrEmpty(qqop) ? qqop : (cookies.TryGetValue("openid", out var op) ? op : ""));
            }

            var targetAccessToken = accessToken;
            if (string.IsNullOrEmpty(targetAccessToken))
            {
                targetAccessToken = isWeChat
                    ? (cookies.TryGetValue("access_token", out var wxat) && !string.IsNullOrEmpty(wxat) ? wxat : (cookies.TryGetValue("psrf_wx_access_token", out var pat) ? pat : ""))
                    : (cookies.TryGetValue("psrf_qqaccess_token", out var qqat) && !string.IsNullOrEmpty(qqat) ? qqat : (cookies.TryGetValue("access_token", out var at) ? at : ""));
            }

            var sb = new StringBuilder(512);
            sb.Append("{\"comm\":{\"ct\":11,\"cv\":14090008,\"v\":14090008,\"chid\":\"10003505\",\"tmeAppID\":\"qqmusic\",\"tmeLoginType\":")
              .Append(actualLoginType).Append("},\"req_0\":{\"module\":\"music.login.LoginServer\",\"method\":\"Login\",\"param\":{")
              .Append("\"musicid\":").Append(numericUin)
              .Append(",\"str_musicid\":\"").Append(JsonEncodedText.Encode(musicId)).Append('"')
              .Append(",\"refresh_token\":\"").Append(JsonEncodedText.Encode(refreshToken)).Append('"');

            if (!string.IsNullOrEmpty(refreshKey))
            {
                sb.Append(",\"refresh_key\":\"").Append(JsonEncodedText.Encode(refreshKey)).Append('"');
            }
            if (!string.IsNullOrEmpty(currentMusicKey))
            {
                sb.Append(",\"musickey\":\"").Append(JsonEncodedText.Encode(currentMusicKey)).Append('"');
            }
            if (!string.IsNullOrEmpty(targetOpenid))
            {
                sb.Append(",\"openid\":\"").Append(JsonEncodedText.Encode(targetOpenid)).Append('"');
            }
            if (!string.IsNullOrEmpty(targetAccessToken))
            {
                sb.Append(",\"access_token\":\"").Append(JsonEncodedText.Encode(targetAccessToken)).Append('"');
            }
            sb.Append(",\"loginMode\":2}}}");
            var payload = sb.ToString();

            using var req = new HttpRequestMessage(HttpMethod.Post, "https://u.y.qq.com/cgi-bin/musicu.fcg");
            req.Content = new StringContent(payload, Encoding.UTF8, "application/json");
            req.Headers.TryAddWithoutValidation("User-Agent", "QQMusic 14090008(android 14)");

            using var resp = await s_http.SendAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return false;
            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("req_0", out var req0) ||
                !req0.TryGetProperty("code", out var codeElem) ||
                codeElem.GetInt32() != 0 ||
                !req0.TryGetProperty("data", out var data))
            {
                AppLogger.Warn("LoginService", "RefreshOfficialAppToken failed");
                return false;
            }

            var newMusicKey = GetJsonString(data, "musickey");
            if (string.IsNullOrEmpty(newMusicKey)) return false;

            var cookieDict = UserSession.Current.Cookies;
            cookieDict["musickey"] = newMusicKey;
            cookieDict["qm_keyst"] = newMusicKey;
            cookieDict["qqmusic_key"] = newMusicKey;
            cookieDict["tmeLoginType"] = actualLoginType.ToString();

            var newRefreshToken = GetJsonString(data, "refresh_token");
            if (string.IsNullOrEmpty(newRefreshToken) && actualLoginType == 1) newRefreshToken = GetJsonString(data, "psrf_wxrefresh_token");
            if (string.IsNullOrEmpty(newRefreshToken) && actualLoginType == 2) newRefreshToken = GetJsonString(data, "psrf_qqrefresh_token");
            if (string.IsNullOrEmpty(newRefreshToken)) newRefreshToken = refreshToken;

            if (!string.IsNullOrEmpty(newRefreshToken))
            {
                cookieDict["refresh_token"] = newRefreshToken;
                if (actualLoginType == 1) cookieDict["psrf_wxrefresh_token"] = newRefreshToken;
                if (actualLoginType == 2) cookieDict["psrf_qqrefresh_token"] = newRefreshToken;
            }

            AddCredentialCookie(data, cookieDict, "refresh_key", "refresh_key");

            var retOpenid = GetJsonString(data, "openid");
            if (string.IsNullOrEmpty(retOpenid) && actualLoginType == 1) retOpenid = GetJsonString(data, "psrf_wxopenid");
            if (string.IsNullOrEmpty(retOpenid) && actualLoginType == 2) retOpenid = GetJsonString(data, "psrf_qqopenid");
            if (string.IsNullOrEmpty(retOpenid)) retOpenid = targetOpenid;
            if (!string.IsNullOrEmpty(retOpenid))
            {
                cookieDict["openid"] = retOpenid;
                if (actualLoginType == 1) cookieDict["psrf_wxopenid"] = retOpenid;
                if (actualLoginType == 2) cookieDict["psrf_qqopenid"] = retOpenid;
            }

            var retAccessToken = GetJsonString(data, "access_token");
            if (string.IsNullOrEmpty(retAccessToken) && actualLoginType == 1) retAccessToken = GetJsonString(data, "psrf_wx_access_token");
            if (string.IsNullOrEmpty(retAccessToken) && actualLoginType == 2) retAccessToken = GetJsonString(data, "psrf_qqaccess_token");
            if (string.IsNullOrEmpty(retAccessToken)) retAccessToken = targetAccessToken;
            if (!string.IsNullOrEmpty(retAccessToken))
            {
                cookieDict["access_token"] = retAccessToken;
                if (actualLoginType == 1) cookieDict["psrf_wx_access_token"] = retAccessToken;
                if (actualLoginType == 2) cookieDict["psrf_qqaccess_token"] = retAccessToken;
            }

            UserSession.Current.MusicKey = newMusicKey;
            UserSession.Current.Save();
            await MusicApi.RefreshCurrentUserProfileAsync(ct).ConfigureAwait(false);
            AppLogger.Info("LoginService", "Refreshed OfficialApp credentials stored successfully");
            return true;
        }
        catch (Exception ex)
        {
            AppLogger.Warn("LoginService", $"RefreshOfficialAppToken exception: {ex.Message}");
            return false;
        }
    }

    private static async Task<bool> ApplyLoginDataAsync(JsonElement loginData, Dictionary<string, string> cookieDict, CancellationToken ct)
    {
        string musicid = "";
        if (loginData.TryGetProperty("str_musicid", out var smid))
        {
            musicid = smid.GetString() ?? "";
        }
        else if (loginData.TryGetProperty("musicid", out var mid))
        {
            musicid = mid.ValueKind == JsonValueKind.Number
                ? mid.GetInt64().ToString()
                : (mid.GetString() ?? "");
        }

        var musickey = GetJsonString(loginData, "musickey");
        var openid = GetJsonString(loginData, "openid");
        if (string.IsNullOrEmpty(openid)) openid = GetJsonString(loginData, "psrf_qqopenid");
        var accessToken = GetJsonString(loginData, "access_token");
        if (string.IsNullOrEmpty(accessToken)) accessToken = GetJsonString(loginData, "psrf_qqaccess_token");
        var refreshToken = GetJsonString(loginData, "refresh_token");
        if (string.IsNullOrEmpty(refreshToken)) refreshToken = GetJsonString(loginData, "psrf_qqrefresh_token");
        var unionid = GetJsonString(loginData, "unionid");
        if (string.IsNullOrEmpty(unionid)) unionid = GetJsonString(loginData, "psrf_qqunionid");

        if (string.IsNullOrEmpty(musickey))
        {
            return false;
        }

        if (!string.IsNullOrEmpty(musicid))
        {
            cookieDict["musicid"] = musicid;
            cookieDict["uin"] = musicid;
            cookieDict["qqmusic_uin"] = musicid;
        }
        cookieDict["qqmusic_key"] = musickey;
        cookieDict["qm_keyst"] = musickey;
        cookieDict["musickey"] = musickey;
        cookieDict["qqmusic_version"] = "17";
        cookieDict["qqmusic_miniversion"] = "70";
        cookieDict["tmeLoginType"] = "2";
        if (!string.IsNullOrEmpty(openid))
        {
            cookieDict["openid"] = openid;
            cookieDict["psrf_qqopenid"] = openid;
        }
        if (!string.IsNullOrEmpty(accessToken))
        {
            cookieDict["access_token"] = accessToken;
            cookieDict["psrf_qqaccess_token"] = accessToken;
        }
        if (!string.IsNullOrEmpty(refreshToken))
        {
            cookieDict["refresh_token"] = refreshToken;
            cookieDict["psrf_qqrefresh_token"] = refreshToken;
        }
        if (!string.IsNullOrEmpty(unionid))
        {
            cookieDict["unionid"] = unionid;
            cookieDict["psrf_qqunionid"] = unionid;
        }

        UserSession.Current.Uin = string.IsNullOrEmpty(musicid) ? UserSession.Current.Uin : musicid;
        UserSession.Current.MusicKey = musickey;
        UserSession.Current.Cookies = cookieDict;
        UserSession.Current.Save();
        await MusicApi.RefreshCurrentUserProfileAsync(ct).ConfigureAwait(false);
        AppLogger.Info("LoginService", "Renewed QQ Music credentials stored successfully");
        return true;
    }

    /// <summary>
    /// 第二阶段：自动通过 OAuth2 换取专属 musickey 完整 VIP Cookie
    /// </summary>
    /// <param name="cookieDict">包含 p_skey 等认证信息的 Cookie 字典。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>若成功通过 OAuth2 换票则为 true；否则为 false。</returns>
    public static async Task<bool> ExchangeMusicKeyByOAuthAsync(Dictionary<string, string> cookieDict, CancellationToken ct = default)
    {
        try
        {
            if (!cookieDict.TryGetValue("p_skey", out var p_skey) || string.IsNullOrEmpty(p_skey))
            {
                AppLogger.Error("LoginService", "ExchangeMusicKeyByOAuthAsync: p_skey not found in cookieDict");
                return false;
            }

            var gtk = GetACSRFToken(p_skey);
            AppLogger.Info("LoginService", $"Calculated g_tk: {gtk} for p_skey");

            // POST https://graph.qq.com/oauth2.0/authorize
            var authUrl = "https://graph.qq.com/oauth2.0/authorize";
            using var authReq = new HttpRequestMessage(HttpMethod.Post, authUrl);

            var cookieHeader = string.Join("; ", cookieDict.Select(kv => $"{kv.Key}={kv.Value}"));
            authReq.Headers.Add("Cookie", cookieHeader);
            authReq.Headers.Referrer = new Uri("https://graph.qq.com/oauth2.0/show?which=Login&display=pc&response_type=code&client_id=100497308&redirect_uri=https%3A%2F%2Fy.qq.com%2Fwk_v17%2Fcommon_login.html%3Ftype%3DQQ%26%26redirect%3D&state=y_new.top.pop.logout&display=pc&scope=get_user_info");

            var formData = new Dictionary<string, string>
            {
                ["response_type"] = "code",
                ["client_id"] = "100497308",
                ["redirect_uri"] = "https://y.qq.com/wk_v17/common_login.html?type=QQ&&redirect=",
                ["scope"] = "get_user_info",
                ["state"] = "y_new.top.pop.logout",
                ["switch"] = "",
                ["from_ptlogin"] = "1",
                ["src"] = "1",
                ["update_auth"] = "1",
                ["openapi"] = "8090_1010_1030_1050",
                ["g_tk"] = gtk.ToString()
            };
            authReq.Content = new FormUrlEncodedContent(formData);

            using var authResp = await s_http.SendAsync(authReq, ct).ConfigureAwait(false);
            AppLogger.Info("LoginService", $"OAuth authorize response status: {authResp.StatusCode}");

            string code = "";
            if (authResp.Headers.Location != null)
            {
                var loc = authResp.Headers.Location.ToString();
                AppLogger.Info("LoginService", $"OAuth authorize redirect location: {loc}");
                var codeMatch = CodeQueryRegex().Match(loc);
                if (codeMatch.Success)
                {
                    code = codeMatch.Groups[1].Value;
                }
            }
            else
            {
                var body = await authResp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                AppLogger.Debug("LoginService", $"OAuth authorize body: {(body.Length > 200 ? body[..200] : body)}");
                var codeMatch = CodeQueryRegex().Match(body);
                if (codeMatch.Success)
                {
                    code = codeMatch.Groups[1].Value;
                }
            }

            if (string.IsNullOrEmpty(code))
            {
                AppLogger.Warn("LoginService", "Failed to obtain OAuth code from authorize endpoint (p_skey session expired)");
                return false;
            }

            AppLogger.Info("LoginService", $"Successfully captured OAuth code: {code}");

            // 调用 u.y.qq.com 接口换取专属 musickey
            var musicLoginUrl = "https://u.y.qq.com/cgi-bin/musicu.fcg";
            var payload = $"{{\"comm\":{{\"ct\":19,\"cv\":1,\"tmeLoginType\":\"1\"}},\"login\":{{\"module\":\"QQConnectLogin.LoginServer\",\"method\":\"QQLogin\",\"param\":{{\"onlyNeedAccessToken\":0,\"forceRefreshToken\":0,\"appid\":100497308,\"code\":\"{code}\"}}}}}}";

            using var loginReq = new HttpRequestMessage(HttpMethod.Post, musicLoginUrl);
            loginReq.Content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json");
            using var loginResp = await s_http.SendAsync(loginReq, ct).ConfigureAwait(false);
            var loginRespJson = await loginResp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            using var doc = System.Text.Json.JsonDocument.Parse(loginRespJson);
            var root = doc.RootElement;
            if (root.TryGetProperty("login", out var loginObj) &&
                loginObj.TryGetProperty("data", out var loginData))
            {
                string musicid = "";
                if (loginData.TryGetProperty("str_musicid", out var smid))
                {
                    musicid = smid.GetString() ?? "";
                }
                else if (loginData.TryGetProperty("musicid", out var mid))
                {
                    musicid = mid.ValueKind == System.Text.Json.JsonValueKind.Number
                        ? mid.GetInt64().ToString()
                        : (mid.GetString() ?? "");
                }

                var musickey = loginData.TryGetProperty("musickey", out var mk) && mk.ValueKind == System.Text.Json.JsonValueKind.String
                    ? mk.GetString() ?? "" : "";
                var openid = loginData.TryGetProperty("openid", out var op) && op.ValueKind == System.Text.Json.JsonValueKind.String
                    ? op.GetString() ?? "" : "";
                if (string.IsNullOrEmpty(openid)) openid = GetJsonString(loginData, "psrf_qqopenid");
                var accessToken = loginData.TryGetProperty("access_token", out var at) && at.ValueKind == System.Text.Json.JsonValueKind.String
                    ? at.GetString() ?? "" : "";
                if (string.IsNullOrEmpty(accessToken)) accessToken = GetJsonString(loginData, "psrf_qqaccess_token");
                var refreshToken = loginData.TryGetProperty("refresh_token", out var rt) && rt.ValueKind == System.Text.Json.JsonValueKind.String
                    ? rt.GetString() ?? "" : "";
                if (string.IsNullOrEmpty(refreshToken)) refreshToken = GetJsonString(loginData, "psrf_qqrefresh_token");
                var unionid = loginData.TryGetProperty("unionid", out var un) && un.ValueKind == System.Text.Json.JsonValueKind.String
                    ? un.GetString() ?? "" : "";
                if (string.IsNullOrEmpty(unionid)) unionid = GetJsonString(loginData, "psrf_qqunionid");

                if (!string.IsNullOrEmpty(musickey))
                {
                    if (!string.IsNullOrEmpty(musicid))
                    {
                        cookieDict["musicid"] = musicid;
                        cookieDict["uin"] = musicid;
                        cookieDict["qqmusic_uin"] = musicid;
                    }
                    cookieDict["qqmusic_key"] = musickey;
                    cookieDict["qm_keyst"] = musickey;
                    cookieDict["musickey"] = musickey;
                    cookieDict["qqmusic_version"] = "17";
                    cookieDict["qqmusic_miniversion"] = "70";
                    cookieDict["tmeLoginType"] = "2";
                    if (!string.IsNullOrEmpty(openid))
                    {
                        cookieDict["openid"] = openid;
                        cookieDict["psrf_qqopenid"] = openid;
                    }
                    if (!string.IsNullOrEmpty(accessToken))
                    {
                        cookieDict["access_token"] = accessToken;
                        cookieDict["psrf_qqaccess_token"] = accessToken;
                    }
                    if (!string.IsNullOrEmpty(refreshToken))
                    {
                        cookieDict["refresh_token"] = refreshToken;
                        cookieDict["psrf_qqrefresh_token"] = refreshToken;
                    }
                    if (!string.IsNullOrEmpty(unionid))
                    {
                        cookieDict["unionid"] = unionid;
                        cookieDict["psrf_qqunionid"] = unionid;
                    }

                    UserSession.Current.Uin = string.IsNullOrEmpty(musicid) ? UserSession.Current.Uin : musicid;
                    UserSession.Current.MusicKey = musickey;
                    UserSession.Current.Cookies = cookieDict;
                    UserSession.Current.Save();
                    await MusicApi.RefreshCurrentUserProfileAsync(ct).ConfigureAwait(false);
                    AppLogger.Info("LoginService", "QQ Music credentials stored successfully");
                    return true;
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("LoginService", "Exception in ExchangeMusicKeyByOAuthAsync", ex);
        }
        return false;
    }

    /// <summary>
    /// 计算 QQ 网页端防跨站请求伪造令牌（g_tk / bkn）。
    /// </summary>
    /// <param name="p_skey">用户会话密钥。</param>
    /// <returns>计算得出的数值型 CSRF 令牌。</returns>
    public static int GetACSRFToken(string p_skey)
    {
        var hash = 5381;
        for (int i = 0; i < p_skey.Length; i++)
        {
            hash += (hash << 5) + (int)p_skey[i];
        }
        return hash & 0x7fffffff;
    }

    /// <summary>
    /// 手动导入 Cookie 字符串并持久化保存。
    /// </summary>
    /// <param name="rawCookie">分号分隔的原始 HTTP Cookie 字符串。</param>
    /// <returns>解析并保存成功返回 <see langword="true"/>；若缺少关键登录标识返回 <see langword="false"/>。</returns>
    public static bool ImportCookieString(string rawCookie)
    {
        if (string.IsNullOrWhiteSpace(rawCookie)) return false;

        AppLogger.Info("LoginService", $"Importing raw cookie (length: {rawCookie.Length})");
        Dictionary<string, string> dict = [];
        var pairs = rawCookie.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var p in pairs)
        {
            var idx = p.IndexOf('=');
            if (idx > 0)
            {
                var k = p[..idx].Trim();
                var v = p[(idx + 1)..].Trim();
                dict[k] = v;
            }
        }

        string uin = "";
        if (dict.TryGetValue("uin", out var u)) uin = u.TrimStart('o');
        else if (dict.TryGetValue("qqmusic_uin", out var qu)) uin = qu.TrimStart('o');
        else if (dict.TryGetValue("musicid", out var mu)) uin = mu;

        string key = "";
        if (dict.TryGetValue("qqmusic_key", out var qk)) key = qk;
        else if (dict.TryGetValue("skey", out var sk)) key = sk;
        else if (dict.TryGetValue("qm_keyst", out var qmk)) key = qmk;

        if (string.IsNullOrEmpty(uin) && string.IsNullOrEmpty(key))
        {
            AppLogger.Error("LoginService", "ImportCookieString failed: neither uin nor key found in cookie string");
            return false;
        }

        UserSession.Current.Uin = uin;
        UserSession.Current.Nick = $"用户_{uin}";
        UserSession.Current.MusicKey = key;
        UserSession.Current.Cookies = dict;
        UserSession.Current.Save();

        AppLogger.Info("LoginService", $"Cookie imported successfully: Uin={uin}");
        return true;
    }

    /// <summary>
    /// 清除当前登录凭证并退出账号。
    /// </summary>
    public static void Logout()
    {
        AppLogger.Info("LoginService", "User requested logout");
        UserSession.Current.Clear();
    }

    /// <summary>
    /// 计算 ptqr 扫码轮询接口校验哈希值。
    /// </summary>
    /// <param name="qrsig">Cookie 中返回的二维码签名字符串。</param>
    /// <returns>计算得出的数值型校验哈希。</returns>
    public static int HashPtqrToken(string qrsig)
    {
        int e = 0;
        for (int i = 0; i < qrsig.Length; i++)
        {
            e += (e << 5) + (int)qrsig[i];
        }
        return 2147483647 & e;
    }

    [GeneratedRegex(@"'([^']*)'")]
    private static partial Regex SingleQuoteRegex();

    [GeneratedRegex("uuid=([^\\\"]+)")]
    private static partial Regex WeChatUuidRegex();

    [GeneratedRegex(@"window\.wx_errcode=(\d+);window\.wx_code='([^']*)'")]
    private static partial Regex WeChatStatusRegex();
    [GeneratedRegex(@"[?&]uin=([^&]+)")]
    private static partial Regex UinQueryRegex();

    [GeneratedRegex(@"[?&]code=([^&#\s""']+)")]
    private static partial Regex CodeQueryRegex();
}
