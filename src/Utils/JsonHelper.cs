using System.Text.Encodings.Web;

namespace QmTui.Utils;

/// <summary>
/// JSON 文本安全格式化与转义辅助类
/// </summary>
public static class JsonHelper
{
    private static readonly JavaScriptEncoder s_encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping;

    /// <summary>
    /// 对字符串进行符合 JSON 规范的安全转义（仅转义引号、反斜杠及控制字符，不转义 HTML 实体）
    /// </summary>
    /// <param name="value">待转义的原始字符串</param>
    /// <returns>转义后的安全 JSON 字符串；若为 null 或空则返回空字符串</returns>
    public static string EscapeString(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        return s_encoder.Encode(value);
    }
}
