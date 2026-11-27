using System.Text;
using System.Text.Json;
using QmTui.Models;
using QmTui.Utils;

namespace QmTui.Api;

public sealed partial class MusicApi
{
    /// <summary>
    /// 刷新并更新当前登录用户的个人资料与 VIP 身份信息。
    /// </summary>
    /// <param name="ct">异步操作取消令牌。</param>
    /// <returns>刷新成功返回 <see langword="true"/>；未登录或请求失败返回 <see langword="false"/>。</returns>
    public static async Task<bool> RefreshCurrentUserProfileAsync(CancellationToken ct = default)
    {
        return await RefreshCurrentUserProfileInternalAsync(canRetryWithRenew: true, ct).ConfigureAwait(false);
    }

    private static async Task<bool> RefreshCurrentUserProfileInternalAsync(bool canRetryWithRenew, CancellationToken ct)
    {
        if (!UserSession.Current.IsLoggedIn || string.IsNullOrWhiteSpace(UserSession.Current.MusicKey))
        {
            return false;
        }

        var uin = UserSession.Current.Uin;
        var escapedUin = JsonEncodedText.Encode(UserSession.Current.Uin).ToString();
        var escapedKey = JsonEncodedText.Encode(UserSession.Current.MusicKey).ToString();
        var loginType = GetCurrentLoginType();
        var payload = "{\"comm\":{\"ct\":11,\"cv\":14090008,\"v\":14090008,\"chid\":\"10003505\",\"tmeAppID\":\"qqmusic\",\"tmeLoginType\":" + loginType +
            ",\"qq\":\"" + escapedUin + "\",\"authst\":\"" + escapedKey + "\"}," +
            "\"profile\":{\"module\":\"music.UnifiedHomepage.UnifiedHomepageSrv\",\"method\":\"GetHomepageHeader\",\"param\":{\"uin\":\"" + escapedUin + "\",\"IsQueryTabDetail\":1}}," +
            "\"vip\":{\"module\":\"VipLogin.VipLoginInter\",\"method\":\"vip_login_base\",\"param\":{}}}";

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, "https://u.y.qq.com/cgi-bin/musicu.fcg");
            req.Content = new StringContent(payload, Encoding.UTF8, "application/json");
            req.Headers.TryAddWithoutValidation("User-Agent", "QQMusic 14090008(android 14)");
            var cookieHeader = UserSession.Current.GetCookieHeader();
            if (!string.IsNullOrEmpty(cookieHeader)) req.Headers.TryAddWithoutValidation("Cookie", cookieHeader);

            using var resp = await s_httpClient.SendAsync(req, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            int rootCode = root.TryGetProperty("code", out var rc) && rc.ValueKind == JsonValueKind.Number ? rc.GetInt32() : 0;
            int profileCode = root.TryGetProperty("profile", out var pElem) && pElem.TryGetProperty("code", out var pc) && pc.ValueKind == JsonValueKind.Number ? pc.GetInt32() : 0;
            int vipCode = root.TryGetProperty("vip", out var vElem) && vElem.TryGetProperty("code", out var vc) && vc.ValueKind == JsonValueKind.Number ? vc.GetInt32() : 0;

            var isCodeExpired = s_expiredAuthCodes.Contains(rootCode) || s_expiredAuthCodes.Contains(profileCode) || s_expiredAuthCodes.Contains(vipCode);
            var hasProfileData = TryGetResponseData(root, "profile", out var profileData);
            var hasVipData = TryGetResponseData(root, "vip", out var vipData);

            var isGuestVipResponse = false;
            if (hasVipData && vipData.TryGetProperty("identity", out var checkId))
            {
                var level = ReadInt(checkId, "level");
                var musicLevel = vipData.TryGetProperty("userinfo", out var uInfo) ? ReadInt(uInfo, "music_level") : 0;
                var overdate = ReadString(checkId, "overdate");
                var hugeEnd = ReadString(checkId, "HugeVipEnd");
                isGuestVipResponse = level == 0 && musicLevel == 0 && string.IsNullOrWhiteSpace(overdate) && string.IsNullOrWhiteSpace(hugeEnd);
            }

            var isSilentDowngrade = UserSession.Current.IsVip && isGuestVipResponse;

            if (canRetryWithRenew && (isCodeExpired || isSilentDowngrade || !hasProfileData || !hasVipData))
            {
                AppLogger.Info("MusicApi", $"Detected expired or downgraded credentials (code={vipCode}, silentDowngrade={isSilentDowngrade}), attempting auto refresh...");
                var renewed = await LoginService.EnsureMusicKeyAsync(forceRefresh: true, ct).ConfigureAwait(false);
                if (renewed)
                {
                    return await RefreshCurrentUserProfileInternalAsync(canRetryWithRenew: false, ct).ConfigureAwait(false);
                }
            }

            var changed = false;
            if (hasProfileData &&
                profileData.TryGetProperty("Info", out var info) &&
                info.TryGetProperty("BaseInfo", out var baseInfo))
            {
                changed |= SetString(baseInfo, "Name", value => UserSession.Current.Nick = value);
                changed |= SetString(baseInfo, "EncryptedUin", value => UserSession.Current.EncryptedUin = value);
                changed |= SetString(baseInfo, "Avatar", value => UserSession.Current.AvatarUrl = value);
            }

            if (hasVipData)
            {
                if (vipData.TryGetProperty("identity", out var identity))
                {
                    var isHugeVip = ReadInt(identity, "HugeVip") > 0;
                    var isSvip = isHugeVip || ReadInt(vipData, "svip") > 0;
                    var isVip = isSvip || ReadInt(identity, "vip") > 0;
                    var level = ReadInt(identity, "level");
                    var overdate = ReadString(identity, "overdate");
                    var hugeEnd = ReadString(identity, "HugeVipEnd");
                    var musicLevel = vipData.TryGetProperty("userinfo", out var userInfo) ? ReadInt(userInfo, "music_level") : 0;

                    var isGuestResult = !isVip && level == 0 && musicLevel == 0 && string.IsNullOrWhiteSpace(overdate) && string.IsNullOrWhiteSpace(hugeEnd);

                    if (!isGuestResult || !UserSession.Current.IsVip)
                    {
                        UserSession.Current.IsSvip = isSvip;
                        UserSession.Current.IsVip = isVip;
                        UserSession.Current.VipLevel = level;
                        UserSession.Current.VipExpireAt = !string.IsNullOrWhiteSpace(hugeEnd) ? hugeEnd : overdate;
                        changed = true;
                    }
                }
                if (vipData.TryGetProperty("userinfo", out var userInfoNode))
                {
                    var mLevel = ReadInt(userInfoNode, "music_level");
                    if (mLevel > 0 || !UserSession.Current.IsVip)
                    {
                        UserSession.Current.MusicLevel = mLevel;
                        changed = true;
                    }
                }
            }

            if (changed)
            {
                UserSession.Current.Save();
                AppLogger.Info("MusicApi", $"Account profile refreshed: {UserSession.Current.Nick}, VIP={UserSession.Current.IsVip}, VIP level={UserSession.Current.VipLevel}, music level={UserSession.Current.MusicLevel}");
            }
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception ex)
        {
            AppLogger.Warn("MusicApi", $"Failed to refresh account profile: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 按昵称搜索 QQ 音乐用户。
    /// </summary>
    /// <param name="query">用户昵称关键词。</param>
    /// <param name="page">分页页码，默认 1。</param>
    /// <param name="pageSize">单页条数，默认 30。</param>
    /// <param name="ct">异步操作取消令牌。</param>
    /// <returns>匹配的用户列表；无结果或请求失败返回空列表。</returns>
    public static async Task<List<UserSummary>> SearchUsersAsync(string query, int page = 1, int pageSize = 30, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query)) return [];

        try
        {
            var escapedQuery = JsonEncodedText.Encode(query).ToString();
            var payload = $$"""
            {
              "music.search.SearchCgiService": {
                "module": "music.search.SearchCgiService",
                "method": "DoSearchForQQMusicDesktop",
                "param": {
                  "query": "{{escapedQuery}}",
                  "page_num": {{page}},
                  "num_per_page": {{pageSize}},
                  "search_type": 8
                }
              }
            }
            """;

            var json = await PostAg1Async(payload, ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("music.search.SearchCgiService", out var svc) &&
                svc.TryGetProperty("data", out var data) &&
                data.TryGetProperty("body", out var body) &&
                body.TryGetProperty("user", out var userObj) &&
                userObj.TryGetProperty("list", out var userArray) &&
                userArray.ValueKind == JsonValueKind.Array)
            {
                var list = new List<UserSummary>(userArray.GetArrayLength());
                foreach (var item in userArray.EnumerateArray())
                {
                    var name = ReadString(item, "title");
                    var encryptUin = ReadString(item, "encrypt_uin");
                    var uin = ReadString(item, "uin");
                    if (string.IsNullOrWhiteSpace(name) ||
                        (string.IsNullOrWhiteSpace(encryptUin) && string.IsNullOrWhiteSpace(uin)))
                    {
                        continue;
                    }

                    list.Add(new UserSummary(
                        Name: name,
                        EncryptUin: encryptUin,
                        Uin: uin,
                        FansNum: ReadInt(item, "fans_num"),
                        PlaylistCount: ReadInt(item, "diss_num"),
                        IsMyself: ReadBool(item, "is_myself"),
                        Identify: ReadString(item, "identify_title")
                    ));
                }
                return list;
            }

            return [];
        }
        catch (Exception ex)
        {
            AppLogger.Error("MusicApi", $"SearchUsersAsync error for '{query}'", ex);
            return [];
        }
    }

    private static int GetCurrentLoginType()
    {
        if (UserSession.Current.Cookies.TryGetValue("tmeLoginType", out var loginType) && int.TryParse(loginType, out var parsed))
        {
            return parsed;
        }
        return UserSession.Current.MusicKey.StartsWith("W_X", StringComparison.Ordinal) ? 1 : 2;
    }

    private static bool TryGetResponseData(JsonElement root, string key, out JsonElement data)
    {
        data = default;
        return root.TryGetProperty(key, out var response) &&
               (!response.TryGetProperty("code", out var code) || code.ValueKind != JsonValueKind.Number || code.GetInt32() == 0) &&
               response.TryGetProperty("data", out data);
    }

    private static bool SetString(JsonElement source, string property, Action<string> setter)
    {
        var value = ReadString(source, property);
        if (string.IsNullOrWhiteSpace(value)) return false;
        setter(value);
        return true;
    }

    private static string ReadString(JsonElement source, string property)
    {
        if (!source.TryGetProperty(property, out var value)) return "";
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? "",
            JsonValueKind.Number => value.GetRawText(),
            _ => ""
        };
    }

    private static int ReadInt(JsonElement source, string property)
    {
        if (!source.TryGetProperty(property, out var value)) return 0;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)) return number;
        return value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out number) ? number : 0;
    }

    private static bool ReadBool(JsonElement source, string property)
    {
        if (!source.TryGetProperty(property, out var value)) return false;
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.Number => value.TryGetInt32(out var number) && number != 0,
            JsonValueKind.String => value.GetString() is { } text &&
                (text == "1" || string.Equals(text, "true", StringComparison.OrdinalIgnoreCase)),
            _ => false
        };
    }
}
