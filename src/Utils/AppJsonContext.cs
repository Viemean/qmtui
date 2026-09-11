using System.Text.Json.Serialization;
using QmTui.Models;

namespace QmTui.Utils;

/// <summary>
/// Native AOT 强类型 JSON 序列化上下文（Roslyn 源生成器，零反射）
/// </summary>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(UserConfig))]
[JsonSerializable(typeof(DailyRecommendCache))]
[JsonSerializable(typeof(FavoriteCache))]
[JsonSerializable(typeof(List<LyricLine>))]
internal sealed partial class AppJsonContext : JsonSerializerContext
{
}
