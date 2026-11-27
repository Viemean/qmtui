using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Drawing;
using System.Reflection;
using QmTui.Utils;
using Terminal.Gui.App;
using Terminal.Gui.Drivers;
using Terminal.Gui.Drawing;

namespace QmTui.UI;

/// <summary>
/// IOutput 包装层：吞掉 DriverImpl.Refresh() 每帧渲染前的「临时隐藏光标」请求。
/// Terminal.Gui 2.6 的 Refresh() 只要有脏视图就发一次 ?25l，写完再由主循环发 ?25h + CUP；
/// 播放时歌词 20Hz 重绘就让原生光标（kitty 等）高频闪烁。kitty 的闪烁计时器只在键鼠/焦点变化时重置，
/// 输出和 CUP 都不会重置它，所以只要不再每帧切换可见性，原生光标就能照常闪烁。
/// 官方修掉该行为后，删除本文件并移除 Program.cs 中的安装即可。
/// </summary>
/// <remarks>
/// 已知局限：包装后 DriverImpl 的 LastSetCursorApplied 恒为 true（它只认 OutputBase），光标写入失败
/// 不会当帧重试；但 DriverImpl.Refresh 每帧末尾都会重新置位待更新，最坏只慢一帧，可不处理。
/// </remarks>
internal sealed class NativeCursorOutput : IOutput
{
    private readonly IOutput _inner;

    public NativeCursorOutput(IOutput inner) => _inner = inner;

    public void SetCursor(Cursor cursor)
    {
        // Refresh 的临时隐藏 = 「Position 置空、保留当前可见 Style」；真正的隐藏（失焦等）用的是
        // 默认 Cursor（Style = Hidden）。只吞前一种，后一种照常下发。
        if (cursor.Position is null && cursor.Style != CursorStyle.Hidden)
        {
            return;
        }

        _inner.SetCursor(cursor);
    }

    public void Write(IOutputBuffer buffer)
    {
        if (_inner.IsLegacyConsole)
        {
            _inner.Write(buffer);
            return;
        }

        // 同步输出（DECSET 2026）：让一帧在终端侧原子呈现。去掉每帧的 ?25l 之后，超长帧在慢链路上
        // 可能被终端分段渲染、露出光标停在中间格子；不支持该私有模式的终端会忽略，无副作用。
        _inner.Write("\x1b[?2026h".AsSpan());

        try
        {
            _inner.Write(buffer);
        }
        finally
        {
            try
            {
                _inner.Write("\x1b[?2026l".AsSpan());
            }
            catch (Exception ex) when (ex is IOException or Win32Exception)
            {
                // 帧写已失败时，收尾序列的异常会顶替原始错误；这里只做尽力收尾。
            }
        }
    }

    public bool Force16Colors { get => _inner.Force16Colors; set => _inner.Force16Colors = value; }
    public bool IsLegacyConsole { get => _inner.IsLegacyConsole; set => _inner.IsLegacyConsole = value; }
    public bool UseKittyGraphics { get => _inner.UseKittyGraphics; set => _inner.UseKittyGraphics = value; }
    public ConcurrentQueue<SixelToRender> GetSixels() => _inner.GetSixels();
    public Size GetSize() => _inner.GetSize();
    public void SetSize(int width, int height) => _inner.SetSize(width, height);
    public Cursor GetCursor() => _inner.GetCursor();
    public void Write(ReadOnlySpan<char> text) => _inner.Write(text);
    public void Suspend() => _inner.Suspend();
    public string GetLastOutput() => _inner.GetLastOutput();
    public string ToAnsi(IOutputBuffer buffer) => _inner.ToAnsi(buffer);
    public void Dispose() => _inner.Dispose();
}

/// <summary>把驱动内部的 IOutput 替换为 <see cref="NativeCursorOutput"/>。</summary>
internal static class NativeCursorGuard
{
    [UnconditionalSuppressMessage("Trimming", "IL2075", Justification = "DriverImpl._output 是每帧写入的必经字段，不会被裁剪。")]
    public static void Install()
    {
        IDriver? driver = Application.Driver;

        if (driver is null || driver.GetOutput() is NativeCursorOutput)
        {
            return;
        }

        try
        {
            IOutput current = driver.GetOutput();

            // 按「值等于 GetOutput()」定位那个字段，不硬编码字段名；DriverImpl 只持有一个 IOutput。
            FieldInfo? field = driver.GetType()
                .GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(f => typeof(IOutput).IsAssignableFrom(f.FieldType) && ReferenceEquals(f.GetValue(driver), current));

            if (field is null)
            {
                AppLogger.Debug("Driver", "未找到驱动的 IOutput 字段，保留原生光标行为（不修复闪烁）");
                return;
            }

            field.SetValue(driver, new NativeCursorOutput(current));
        }
        catch (Exception ex)
        {
            AppLogger.Warn("Driver", $"安装光标过滤失败，回退原生行为：{ex.Message}");
        }
    }
}
