using System.Text;

namespace QmTui.Utils;

/// <summary>
/// 终端二维码通用字符块渲染器，将二维布尔矩阵（true=黑色模块，false=白色背景）转换为垂直合并的半块字符行
/// </summary>
public static class QrCodeRenderer
{
    /// <summary>
    /// 将 2D 矩阵转换为适合终端等宽字体的半块字符行列表
    /// </summary>
    public static List<string> RenderToBlockText(bool[,] grid)
    {
        int height = grid.GetLength(0);
        int width = grid.GetLength(1);

        var lines = new List<string>((height + 1) / 2);
        for (int y = 0; y < height; y += 2)
        {
            var sb = new StringBuilder(width);
            for (int x = 0; x < width; x++)
            {
                bool top = grid[y, x];
                bool bottom = y + 1 < height && grid[y + 1, x];
                sb.Append((top, bottom) switch
                {
                    (true, true) => '█',
                    (true, false) => '▀',
                    (false, true) => '▄',
                    _ => ' '
                });
            }
            lines.Add(sb.ToString());
        }

        return lines;
    }
}
