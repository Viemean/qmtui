using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using QmTui.Api;
using QmTui.Models;
using QmTui.Services;
using QmTui.Utils;

namespace QmTui.UI;

public static partial class TerminalImageHelper
{
    private const int MaxCoverDimension = 1200;

    private static unsafe class WebPNative
    {
        private static delegate* unmanaged[Cdecl]<byte*, nuint, int*, int*, byte*> s_decodeRgba;
        private static delegate* unmanaged[Cdecl]<void*, void> s_webpFree;
        private static bool s_initialized;
        private static readonly Lock s_lock = new();

        private static bool EnsureLoaded()
        {
            if (s_initialized) return s_decodeRgba != null;
            lock (s_lock)
            {
                if (s_initialized) return s_decodeRgba != null;
                s_initialized = true;

                string[] candidates = OperatingSystem.IsLinux()
                    ? ["libwebp.so.7", "libwebp.so", "libwebpdecoder.so.3", "libwebpdecoder.so"]
                    : OperatingSystem.IsMacOS()
                        ? ["libwebp.7.dylib", "libwebp.dylib"]
                        : ["webp.dll", "libwebp.dll"];

                foreach (var candidate in candidates)
                {
                    if (NativeLibrary.TryLoad(candidate, out var handle))
                    {
                        if (NativeLibrary.TryGetExport(handle, "WebPDecodeRGBA", out var decodePtr) &&
                            NativeLibrary.TryGetExport(handle, "WebPFree", out var freePtr))
                        {
                            s_decodeRgba = (delegate* unmanaged[Cdecl]<byte*, nuint, int*, int*, byte*>)decodePtr;
                            s_webpFree = (delegate* unmanaged[Cdecl]<void*, void>)freePtr;
                            return true;
                        }
                    }
                }

                AppLogger.Debug("TerminalImageHelper", "Native libwebp library not available on system.");
                return false;
            }
        }

        public static byte[]? DecodeRgba(byte[] data, out int width, out int height)
        {
            width = 0;
            height = 0;
            if (data == null || data.Length < 12 || !EnsureLoaded()) return null;

            try
            {
                fixed (byte* pData = data)
                {
                    int w = 0, h = 0;
                    byte* raw = s_decodeRgba(pData, (nuint)data.Length, &w, &h);
                    if (raw == null || w <= 0 || h <= 0) return null;

                    try
                    {
                        width = w;
                        height = h;
                        var result = new byte[w * h * 4];
                        new ReadOnlySpan<byte>(raw, result.Length).CopyTo(result);
                        return result;
                    }
                    finally
                    {
                        s_webpFree(raw);
                    }
                }
            }
            catch (Exception ex)
            {
                AppLogger.Debug("TerminalImageHelper", $"WebPDecodeRGBA error: {ex.Message}");
                return null;
            }
        }
    }

    internal static (int width, int height, byte[] pixelData)? DecodeImageRgba(byte[] fileBytes, bool isWebp)
    {
        if (isWebp)
        {
            var webpDecoded = WebPNative.DecodeRgba(fileBytes, out int w, out int h);
            if (webpDecoded != null)
            {
                return (w, h, webpDecoded);
            }
            return null;
        }

        try
        {
            var image = StbImageSharp.ImageResult.FromMemory(fileBytes, StbImageSharp.ColorComponents.RedGreenBlueAlpha);
            if (image != null && image.Width > 0 && image.Height > 0 && image.Data != null)
            {
                return (image.Width, image.Height, image.Data);
            }
        }
        catch
        {
            // fallback to WebP native decode
        }

        var fallbackDecoded = WebPNative.DecodeRgba(fileBytes, out int fallbackW, out int fallbackH);
        if (fallbackDecoded != null)
        {
            return (fallbackW, fallbackH, fallbackDecoded);
        }

        return null;
    }

    private static async Task<string?> ApplyRoundedCornersAsync(string sourceFile, string targetPng, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(sourceFile) || new FileInfo(sourceFile).Length == 0 || cancellationToken.IsCancellationRequested) return null;

        var tmpPng = targetPng + $".tmp.{Guid.NewGuid():N}.png";
        try
        {
            byte[] fileBytes = await File.ReadAllBytesAsync(sourceFile, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            bool isWebp = IsValidWebpFile(sourceFile);
            var decoded = DecodeImageRgba(fileBytes, isWebp);
            if (decoded == null)
            {
                return null;
            }
            cancellationToken.ThrowIfCancellationRequested();

            int width = decoded.Value.width;
            int height = decoded.Value.height;
            byte[] pixelData = decoded.Value.pixelData;

            // 若图像尺寸超过 1200 像素（如单曲原画母图），使用双线性插值算法等比缩放至 1200 像素内；
            // 官方 1200x1200 专辑封面直接保留原生分辨率，消除额外重采样开销并保证 2K/4K 屏幕清晰呈现
            if (width > MaxCoverDimension || height > MaxCoverDimension)
            {
                float scale = Math.Min((float)MaxCoverDimension / width, (float)MaxCoverDimension / height);
                int scaledW = Math.Max(1, (int)MathF.Round(width * scale));
                int scaledH = Math.Max(1, (int)MathF.Round(height * scale));
                pixelData = ResizeBilinear(pixelData, width, height, scaledW, scaledH);
                width = scaledW;
                height = scaledH;
            }
            cancellationToken.ThrowIfCancellationRequested();

            // 施加平滑抗锯齿微圆角裁切（保底 7px 微圆角）
            ApplyAntialiasedRoundedCorners(pixelData, width, height);

            await using (var outStream = File.Create(tmpPng))
            {
                var writer = new StbImageWriteSharp.ImageWriter();
                writer.WritePng(pixelData, width, height, StbImageWriteSharp.ColorComponents.RedGreenBlueAlpha, outStream);
            }

            if (IsValidPngFile(tmpPng))
            {
                try
                {
                    File.Move(tmpPng, targetPng, true);
                    return targetPng;
                }
                catch
                {
                    return tmpPng;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 切歌导致取消，直接释放内存
        }
        catch (Exception ex)
        {
            AppLogger.Warn("TerminalImageHelper", $"ApplyRoundedCorners failed: {ex.Message}");
        }
        finally
        {
            try { if (File.Exists(tmpPng)) File.Delete(tmpPng); } catch {}
            MemoryManager.ScheduleTrim(1500);
        }

        return null;
    }

    /// <summary>
    /// 对 4 通道 RGBA 像素数组执行高质量双线性插值等比缩放
    /// </summary>
    private static byte[] ResizeBilinear(byte[] src, int srcW, int srcH, int dstW, int dstH)
    {
        if (srcW == dstW && srcH == dstH) return src;

        byte[] dst = new byte[dstW * dstH * 4];
        float xRatio = (float)(srcW - 1) / Math.Max(1, dstW - 1);
        float yRatio = (float)(srcH - 1) / Math.Max(1, dstH - 1);

        for (int y = 0; y < dstH; y++)
        {
            float srcY = y * yRatio;
            int y1 = (int)srcY;
            int y2 = Math.Min(y1 + 1, srcH - 1);
            float yLerp = srcY - y1;

            int dstRowOffset = y * dstW * 4;
            int srcRow1Offset = y1 * srcW * 4;
            int srcRow2Offset = y2 * srcW * 4;

            for (int x = 0; x < dstW; x++)
            {
                float srcX = x * xRatio;
                int x1 = (int)srcX;
                int x2 = Math.Min(x1 + 1, srcW - 1);
                float xLerp = srcX - x1;

                int p11 = srcRow1Offset + x1 * 4;
                int p12 = srcRow1Offset + x2 * 4;
                int p21 = srcRow2Offset + x1 * 4;
                int p22 = srcRow2Offset + x2 * 4;

                int dstOffset = dstRowOffset + x * 4;

                for (int c = 0; c < 4; c++)
                {
                    float top = src[p11 + c] + (src[p12 + c] - src[p11 + c]) * xLerp;
                    float bottom = src[p21 + c] + (src[p22 + c] - src[p21 + c]) * xLerp;
                    float val = top + (bottom - top) * yLerp;
                    dst[dstOffset + c] = (byte)Math.Clamp((int)MathF.Round(val), 0, 255);
                }
            }
        }

        return dst;
    }

    /// <summary>
    /// 对 4 通道 RGBA 像素执行多级金字塔 2x2 面积平均抗锯齿降采样，彻底消灭欠采样高频混叠与线稿断线锯齿
    /// </summary>
    private static (int width, int height, byte[] pixelData) DownsamplePyramidAreaAverage(byte[] src, int srcW, int srcH, int targetMaxDimension)
    {
        if (srcW <= targetMaxDimension && srcH <= targetMaxDimension)
        {
            return (srcW, srcH, src);
        }

        int curW = srcW;
        int curH = srcH;
        byte[] curData = src;
        bool isRented = false;

        try
        {
            // 第一阶段：逐级半数 2x2 面积严格平均积分（纯整数位移运算）
            while (curW / 2 >= targetMaxDimension && curH / 2 >= targetMaxDimension)
            {
                int nextW = curW / 2;
                int nextH = curH / 2;
                byte[] nextData = ArrayPool<byte>.Shared.Rent(nextW * nextH * 4);

                for (int y = 0; y < nextH; y++)
                {
                    int srcRow0 = (y * 2) * curW * 4;
                    int srcRow1 = (y * 2 + 1) * curW * 4;
                    int dstRow = y * nextW * 4;

                    for (int x = 0; x < nextW; x++)
                    {
                        int srcX0 = x * 2 * 4;
                        int srcX1 = (x * 2 + 1) * 4;
                        int dstX = x * 4;

                        int i00 = srcRow0 + srcX0;
                        int i01 = srcRow0 + srcX1;
                        int i10 = srcRow1 + srcX0;
                        int i11 = srcRow1 + srcX1;

                        for (int c = 0; c < 4; c++)
                        {
                            int sum = curData[i00 + c] + curData[i01 + c] + curData[i10 + c] + curData[i11 + c] + 2;
                            nextData[dstRow + dstX + c] = (byte)(sum >> 2);
                        }
                    }
                }

                if (isRented)
                {
                    ArrayPool<byte>.Shared.Return(curData);
                }

                curData = nextData;
                curW = nextW;
                curH = nextH;
                isRented = true;
            }

            // 第二阶段：若尺寸仍大于目标，采用双线性插值精确平滑微调至目标尺寸
            if (curW > targetMaxDimension || curH > targetMaxDimension)
            {
                float scale = Math.Min((float)targetMaxDimension / curW, (float)targetMaxDimension / curH);
                int finalW = Math.Max(1, (int)MathF.Round(curW * scale));
                int finalH = Math.Max(1, (int)MathF.Round(curH * scale));

                byte[] finalData = ResizeBilinear(curData, curW, curH, finalW, finalH);
                if (isRented)
                {
                    ArrayPool<byte>.Shared.Return(curData);
                }
                return (finalW, finalH, finalData);
            }

            if (isRented)
            {
                byte[] clone = new byte[curW * curH * 4];
                Array.Copy(curData, clone, clone.Length);
                ArrayPool<byte>.Shared.Return(curData);
                return (curW, curH, clone);
            }

            return (curW, curH, curData);
        }
        catch
        {
            if (isRented)
            {
                ArrayPool<byte>.Shared.Return(curData);
            }
            return (srcW, srcH, src);
        }
    }

    public const uint ImageIdMiniCover = 1;
    public const uint ImageIdNowPlaying = 2;
    public const uint ImageIdArtistDetail = 3;

    private readonly record struct ImageCacheKey(string FilePath, int Cols, int Rows, long LastWriteTicks, uint ImageId = 0);

    private static readonly Lock s_cacheLock = new();
    private const int MaxMemoryCacheEntries = 4;
    private static readonly Dictionary<ImageCacheKey, LinkedListNode<(ImageCacheKey Key, byte[] Payload)>> s_memoryCache = new(MaxMemoryCacheEntries);
    private static readonly LinkedList<(ImageCacheKey Key, byte[] Payload)> s_lruList = new();

    /// <summary>
    /// 在终端指定行、列输出 Kitty 原生图像（f=100 PNG 格式分块传输）
    /// </summary>
    /// <param name="filePath">本地图像文件绝对路径</param>
    /// <param name="col">屏幕 1-based 列坐标</param>
    /// <param name="row">屏幕 1-based 行坐标</param>
    /// <param name="cols">占据列宽（若 rows 为 0，终端将按原图物理宽高比自适应行数）</param>
    /// <param name="rows">占据行高（若为 0，则基于 cols 严格保持 1:1 原画比例，杜绝拉伸变形）</param>
    /// <param name="imageId">可选 Kitty 图像 ID（0 为未指定，将触发全局清屏；>0 为精准 ID 覆盖）</param>
    public static void RenderKittyImage(string filePath, int col, int row, int cols, int rows, uint imageId = 0)
    {
        if (!IsImageSupported || string.IsNullOrEmpty(filePath) || (cols <= 0 && rows <= 0)) return;

        try
        {
            var fi = new FileInfo(filePath);
            if (!fi.Exists || fi.Length == 0) return;

            var key = new ImageCacheKey(filePath, cols, rows, fi.LastWriteTimeUtc.Ticks, imageId);
            byte[]? cachedPayload = null;

            lock (s_cacheLock)
            {
                if (s_memoryCache.TryGetValue(key, out var node))
                {
                    s_lruList.Remove(node);
                    s_lruList.AddFirst(node);
                    cachedPayload = node.Value.Payload;
                }
            }

            if (cachedPayload == null)
            {
                byte[] fileBytes = File.ReadAllBytes(filePath);

                // 根据目标网格尺寸计算超采样物理像素目标（消除混叠锯齿，保留细腻视网膜质感）
                int targetPixelDim = cols > 0 ? Math.Clamp(cols * 24, 200, 720) : (rows > 0 ? Math.Clamp(rows * 48, 200, 720) : 600);
                var decoded = DecodeImageRgba(fileBytes, IsValidWebpFile(filePath));
                if (decoded != null)
                {
                    int currentW = decoded.Value.width;
                    int currentH = decoded.Value.height;
                    byte[] currentPixels = decoded.Value.pixelData;

                    if (currentW > targetPixelDim * 1.25 || currentH > targetPixelDim * 1.25)
                    {
                        var (smoothW, smoothH, smoothPixels) = DownsamplePyramidAreaAverage(
                            currentPixels, currentW, currentH, targetPixelDim);
                        currentW = smoothW;
                        currentH = smoothH;
                        currentPixels = smoothPixels;
                    }

                    // 施加平滑抗锯齿微圆角裁切（保底 7px 微圆角，与图二质感完全一致）
                    ApplyAntialiasedRoundedCorners(currentPixels, currentW, currentH);

                    using var downsampledMs = new MemoryStream();
                    var imgWriter = new StbImageWriteSharp.ImageWriter();
                    imgWriter.WritePng(currentPixels, currentW, currentH, StbImageWriteSharp.ColorComponents.RedGreenBlueAlpha, downsampledMs);
                    fileBytes = downsampledMs.ToArray();
                }

                string base64 = Convert.ToBase64String(fileBytes);

                using var ms = new MemoryStream();
                using var writer = new StreamWriter(ms, Encoding.ASCII);

                // Kitty escape sequence: f=100 (PNG), a=T (transmit and display)
                // 若仅指定 cols (rows <= 0)，终端自动依据当前字体实际像素尺寸严格保持 1:1 原画宽高比
                string sizePart;
                if (cols > 0 && rows > 0) sizePart = $"c={cols},r={rows},";
                else if (cols > 0) sizePart = $"c={cols},";
                else sizePart = $"r={rows},";

                int chunkSize = 4096;
                for (int offset = 0; offset < base64.Length; offset += chunkSize)
                {
                    int len = Math.Min(chunkSize, base64.Length - offset);
                    string chunk = base64.Substring(offset, len);
                    bool isLast = (offset + len >= base64.Length);

                    if (offset == 0)
                    {
                        int m = isLast ? 0 : 1;
                        string idPart = imageId > 0 ? $"i={imageId}," : "";
                        writer.Write($"\x1b_Ga=T,{idPart}q=2,f=100,{sizePart}m={m};{chunk}\x1b\\");
                    }
                    else
                    {
                        int m = isLast ? 0 : 1;
                        writer.Write($"\x1b_Gq=2,m={m};{chunk}\x1b\\");
                    }
                }
                writer.Flush();
                cachedPayload = ms.ToArray();

                lock (s_cacheLock)
                {
                    if (!s_memoryCache.ContainsKey(key))
                    {
                        if (s_memoryCache.Count >= MaxMemoryCacheEntries)
                        {
                            var last = s_lruList.Last;
                            if (last != null)
                            {
                                s_lruList.RemoveLast();
                                s_memoryCache.Remove(last.Value.Key);
                            }
                        }

                        var newNode = new LinkedListNode<(ImageCacheKey Key, byte[] Payload)>((key, cachedPayload));
                        s_lruList.AddFirst(newNode);
                        s_memoryCache[key] = newNode;
                    }
                }
            }

            if (imageId > 0)
            {
                DeleteKittyImage(imageId);
            }
            else
            {
                ClearImages();
            }

            // 移动光标至指定行列（1-indexed）
            var moveCursorBytes = Encoding.ASCII.GetBytes($"\x1b[{row};{col}H");
            WriteRawBytesToTerminal(moveCursorBytes, cachedPayload);
            AppLogger.Info("TerminalImageHelper", $"RenderKittyImage sent {cachedPayload.Length} bytes at ({col}, {row}) size {cols}x{rows} (cached, id={imageId})");
        }
        catch (Exception ex)
        {
            AppLogger.Error("TerminalImageHelper", $"RenderKittyImage failed: {ex.Message}");
        }
    }

    private static void WriteRawBytesToTerminal(byte[] header, byte[] payload)
    {
        try
        {
            using var tty = File.OpenWrite("/dev/tty");
            tty.Write(header, 0, header.Length);
            tty.Write(payload, 0, payload.Length);
            tty.Flush();
            return;
        }
        catch
        {
            // fallback
        }

        try
        {
            using var stdout = Console.OpenStandardOutput();
            stdout.Write(header, 0, header.Length);
            stdout.Write(payload, 0, payload.Length);
            stdout.Flush();
        }
        catch (Exception ex)
        {
            AppLogger.Debug("TerminalImage", $"Write chunk to stdout failed: {ex.Message}");
        }
    }

    private static void WriteRawBytesToTerminal(byte[] bytes)
    {
        try
        {
            using var tty = File.OpenWrite("/dev/tty");
            tty.Write(bytes, 0, bytes.Length);
            tty.Flush();
            return;
        }
        catch
        {
            // fallback
        }

        try
        {
            using var stdout = Console.OpenStandardOutput();
            stdout.Write(bytes, 0, bytes.Length);
            stdout.Flush();
        }
        catch (Exception ex)
        {
            AppLogger.Debug("TerminalImage", $"Write raw bytes to stdout failed: {ex.Message}");
        }
    }

    /// <summary>
    /// 清除终端中显示的 Kitty 图像
    /// </summary>
    public static void ClearImages()
    {
        if (!IsImageSupported) return;
        try
        {
            byte[] cmd = Encoding.ASCII.GetBytes("\x1b_Ga=d,d=a,q=2\x1b\\");
            WriteRawBytesToTerminal(cmd);
        }
        catch (Exception ex)
        {
            AppLogger.Debug("TerminalImage", $"ClearImages failed: {ex.Message}");
        }
    }

    /// <summary>
    /// 对 32 位 RGBA 像素执行无黑边抗锯齿微圆角裁切（四角平滑 Alpha 渐变，零额外大内存分配）
    /// </summary>
    internal static void ApplyAntialiasedRoundedCorners(byte[] rgba, int width, int height, float? customRadius = null)
    {
        if (rgba == null || width <= 4 || height <= 4) return;

        // 自适应微圆角：以短边约 1.5% 为基准，小图保底 7px（与图二一致），大图自适应至 16px
        float radius = customRadius ?? Math.Clamp(Math.Min(width, height) * 0.016f, 7f, 16f);
        int rInt = (int)MathF.Ceiling(radius);
        if (rInt <= 0 || rInt * 2 > width || rInt * 2 > height) return;

        float rInner = radius - 0.5f;
        float rOuter = radius + 0.5f;
        float rInnerSq = rInner * rInner;
        float rOuterSq = rOuter * rOuter;

        for (int y = 0; y < rInt; y++)
        {
            float dy = radius - (y + 0.5f);
            int rowTopIdx = y * width * 4;
            int rowBottomIdx = (height - 1 - y) * width * 4;

            for (int x = 0; x < rInt; x++)
            {
                float dx = radius - (x + 0.5f);
                float distSq = dx * dx + dy * dy;

                if (distSq <= rInnerSq)
                {
                    continue;
                }

                if (distSq >= rOuterSq)
                {
                    rgba[rowTopIdx + x * 4 + 3] = 0;
                    rgba[rowTopIdx + (width - 1 - x) * 4 + 3] = 0;
                    rgba[rowBottomIdx + x * 4 + 3] = 0;
                    rgba[rowBottomIdx + (width - 1 - x) * 4 + 3] = 0;
                    continue;
                }

                float dist = MathF.Sqrt(distSq);
                float alphaFactor = Math.Clamp(radius + 0.5f - dist, 0f, 1f);

                int tl = rowTopIdx + x * 4 + 3;
                rgba[tl] = (byte)MathF.Round(rgba[tl] * alphaFactor);

                int tr = rowTopIdx + (width - 1 - x) * 4 + 3;
                rgba[tr] = (byte)MathF.Round(rgba[tr] * alphaFactor);

                int bl = rowBottomIdx + x * 4 + 3;
                rgba[bl] = (byte)MathF.Round(rgba[bl] * alphaFactor);

                int br = rowBottomIdx + (width - 1 - x) * 4 + 3;
                rgba[br] = (byte)MathF.Round(rgba[br] * alphaFactor);
            }
        }
    }

    /// <summary>
    /// 清除终端中指定 ID 的 Kitty 图像
    /// </summary>
    public static void DeleteKittyImage(uint imageId)
    {
        if (!IsImageSupported || imageId == 0) return;
        try
        {
            byte[] cmd = Encoding.ASCII.GetBytes($"\x1b_Ga=d,d=i,i={imageId},q=2\x1b\\");
            WriteRawBytesToTerminal(cmd);
        }
        catch (Exception ex)
        {
            AppLogger.Debug("TerminalImage", $"DeleteKittyImage failed: {ex.Message}");
        }
    }
}
