using ZXing;
using ZXing.QrCode;
using ZXing.QrCode.Internal;

namespace QmTui.Utils;

/// <summary>
/// 基于 ZXing.Net 的工业级标准 QR 码（ISO/IEC 18004）编码器
/// 支持 Native AOT，与 Melodist Mobile / TV ZXing 解码器 100% 协议兼容
/// </summary>
public static class QrCodeEncoder
{
    public enum EccLevel
    {
        L = 1,
        M = 0,
        Q = 3,
        H = 2
    }

    /// <summary>
    /// 将文本编码为终端半块字符行列表（上黑下黑/上黑下白/上白下黑/全白）
    /// </summary>
    public static List<string> EncodeToBlockText(string content, EccLevel ecc = EccLevel.M, int quietZone = 2)
    {
        var grid = GenerateMatrix(content, ecc, quietZone);
        return QrCodeRenderer.RenderToBlockText(grid);
    }

    /// <summary>
    /// 生成 QR 模块布尔矩阵（true = 黑色模块，false = 白色模块）
    /// </summary>
    public static bool[,] GenerateMatrix(string content, EccLevel ecc = EccLevel.M, int quietZone = 2)
    {
        ArgumentNullException.ThrowIfNull(content);

        var errorCorrectionLevel = ecc switch
        {
            EccLevel.L => ErrorCorrectionLevel.L,
            EccLevel.M => ErrorCorrectionLevel.M,
            EccLevel.Q => ErrorCorrectionLevel.Q,
            EccLevel.H => ErrorCorrectionLevel.H,
            _ => ErrorCorrectionLevel.M
        };

        var hints = new Dictionary<EncodeHintType, object>
        {
            [EncodeHintType.CHARACTER_SET] = "UTF-8",
            [EncodeHintType.ERROR_CORRECTION] = errorCorrectionLevel,
            [EncodeHintType.MARGIN] = quietZone
        };

        var writer = new QRCodeWriter();
        var bitMatrix = writer.encode(content, BarcodeFormat.QR_CODE, 0, 0, hints);

        int width = bitMatrix.Width;
        int height = bitMatrix.Height;
        var grid = new bool[height, width];

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                grid[y, x] = bitMatrix[x, y];
            }
        }

        return grid;
    }
}
