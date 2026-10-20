using System.Text;
using Terminal.Gui.Configuration;
using Terminal.Gui.Drawing;
using Terminal.Gui.ViewBase;
using Attribute = Terminal.Gui.Drawing.Attribute;

namespace QmTui.UI;

/// <summary>
/// 调色板定义。
/// </summary>
internal sealed class MikuPalette
{
    public required Color QqGreenPrimary { get; init; }
    public required Color QqGreenLight { get; init; }
    public required Color QqGreenDark { get; init; }
    public required Color QqGreenActive { get; init; }
    public required Color QqTextLyricDim { get; init; }

    public required Color MikuPinkAccent { get; init; }
    public required Color MikuPinkLight { get; init; }

    public required Color MikuBgCanvas { get; init; }
    public required Color MikuBgSurface { get; init; }
    public required Color MikuBgDialog { get; init; }
    public required Color MikuBgFocus { get; init; }

    public required Color MikuTextWhite { get; init; }
    public required Color MikuTextSoftWhite { get; init; }
    public required Color MikuTextSub { get; init; }
    public required Color MikuTextMuted { get; init; }

    public required Color FrameBorderDimColor { get; init; }
    public required Color QqPlayingFocusText { get; init; }
}

/// <summary>
/// 主题配色定义。
/// 包含正文、边框、聚焦高亮、辅助文字与背景等各级颜色值，支持暗色、亮色与环境自动检测。
/// </summary>
public static class MikuTheme
{
    private static readonly MikuPalette s_darkPalette = new()
    {
        QqGreenPrimary = new Color(0x31, 0xC2, 0x7C),
        QqGreenLight = new Color(0x5F, 0xE3, 0xA1),
        QqGreenDark = new Color(0x20, 0x58, 0x54),
        QqGreenActive = new Color(0x18, 0x46, 0x42),
        QqTextLyricDim = new Color(0xB8, 0xCC, 0xD0),

        MikuPinkAccent = new Color(0xFF, 0x52, 0x77),
        MikuPinkLight = new Color(0xFF, 0x7A, 0x99),

        MikuBgCanvas = Color.None,
        MikuBgSurface = Color.None,
        MikuBgDialog = new Color(0x1A, 0x21, 0x2C),
        MikuBgFocus = new Color(0x20, 0x58, 0x54),

        MikuTextWhite = new Color(0xED, 0xF6, 0xF6),
        MikuTextSoftWhite = new Color(0xED, 0xED, 0xED),
        MikuTextSub = new Color(0xA2, 0xE8, 0xE2),
        MikuTextMuted = new Color(0x62, 0x82, 0x8A),

        FrameBorderDimColor = new Color(0x38, 0x58, 0x54),
        QqPlayingFocusText = new Color(0x5F, 0xE3, 0xA1)
    };

    private static readonly MikuPalette s_lightPalette = new()
    {
        QqGreenPrimary = new Color(0x0F, 0x82, 0x49),
        QqGreenLight = new Color(0x0C, 0x6E, 0x3D),
        QqGreenDark = new Color(0x15, 0x73, 0x42),
        QqGreenActive = new Color(0x28, 0x58, 0x42),
        QqTextLyricDim = new Color(0x38, 0x42, 0x48),

        MikuPinkAccent = new Color(0xD1, 0x24, 0x53),
        MikuPinkLight = new Color(0xB8, 0x1B, 0x43),

        MikuBgCanvas = Color.None,
        MikuBgSurface = Color.None,
        MikuBgDialog = new Color(0xF6, 0xF8, 0xFA),
        MikuBgFocus = new Color(0x15, 0x73, 0x42),

        MikuTextWhite = new Color(0x1F, 0x23, 0x28),
        MikuTextSoftWhite = new Color(0x2E, 0x33, 0x38),
        MikuTextSub = new Color(0x0D, 0x6B, 0x62),
        MikuTextMuted = new Color(0x57, 0x60, 0x6A),

        FrameBorderDimColor = new Color(0x8A, 0x98, 0x95),
        QqPlayingFocusText = new Color(0xA8, 0xFF, 0xD4)
    };

    private static MikuPalette s_currentPalette = s_darkPalette;

    /// <summary>
    /// 当前是否处于浅色主题。
    /// </summary>
    public static bool IsLightMode { get; private set; }

    /// <summary>
    /// 当前主题模式 ("auto", "light", "dark")。
    /// </summary>
    public static string CurrentThemeMode { get; private set; } = "auto";

    // 动态色彩映射（委托至当前活跃调色板）
    public static Color QqGreenPrimary => s_currentPalette.QqGreenPrimary;
    public static Color QqGreenLight => s_currentPalette.QqGreenLight;
    public static Color QqGreenDark => s_currentPalette.QqGreenDark;
    public static Color QqGreenActive => s_currentPalette.QqGreenActive;
    public static Color QqTextLyricDim => s_currentPalette.QqTextLyricDim;
    public static Color QqPlayingFocusText => s_currentPalette.QqPlayingFocusText;

    public static Color MikuPinkAccent => s_currentPalette.MikuPinkAccent;
    public static Color MikuPinkLight => s_currentPalette.MikuPinkLight;

    public static Color MikuBgCanvas => s_currentPalette.MikuBgCanvas;
    public static Color MikuBgSurface => s_currentPalette.MikuBgSurface;
    public static Color MikuBgDialog => s_currentPalette.MikuBgDialog;
    public static Color MikuBgFocus => s_currentPalette.MikuBgFocus;

    public static Color MikuTextWhite => s_currentPalette.MikuTextWhite;
    public static Color MikuTextSoftWhite => s_currentPalette.MikuTextSoftWhite;
    public static Color MikuTextSub => s_currentPalette.MikuTextSub;
    public static Color MikuTextMuted => s_currentPalette.MikuTextMuted;

    public static Scheme Base { get; private set; } = CreateBaseScheme(s_darkPalette);
    public static Scheme Dialog { get; private set; } = CreateDialogScheme(s_darkPalette);
    public static Scheme PlayerBar { get; private set; } = CreatePlayerBarScheme(s_darkPalette);
    public static Scheme FavoriteActive { get; private set; } = CreateFavoriteActiveScheme(s_darkPalette);
    public static Scheme Lyric { get; private set; } = CreateLyricScheme(s_darkPalette);
    public static Scheme FrameBorder { get; private set; } = CreateFrameBorderActiveScheme(s_darkPalette);
    public static Scheme FrameBorderDim { get; private set; } = CreateFrameBorderDimScheme(s_darkPalette);
    public static Scheme FrameBorderActive { get; private set; } = CreateFrameBorderActiveScheme(s_darkPalette);
    public static Scheme TitleHighlight { get; private set; } = CreateTitleHighlightScheme(s_darkPalette);
    public static Scheme QrCode { get; private set; } = CreateQrCodeScheme();
    public static Scheme SearchCategoryActive { get; private set; } = CreateSearchCategoryActiveScheme(s_darkPalette);
    public static Scheme SearchCategoryDim { get; private set; } = CreateSearchCategoryDimScheme(s_darkPalette);
    public static Scheme TransparentDialog { get; private set; } = CreateTransparentDialogScheme(s_darkPalette);

    private static Scheme CreateBaseScheme(MikuPalette p)
    {
        return new Scheme
        {
            Normal = new Attribute(p.MikuTextWhite, p.MikuBgSurface),
            Focus = new Attribute(Color.White, p.QqGreenDark),
            HotNormal = new Attribute(p.MikuPinkAccent, p.MikuBgSurface),
            HotFocus = new Attribute(Color.White, p.MikuPinkAccent),
            Disabled = new Attribute(p.MikuTextMuted, p.MikuBgSurface),
            Highlight = new Attribute(p.QqGreenPrimary, p.MikuBgSurface),
            Active = new Attribute(Color.White, p.QqGreenActive),
            ReadOnly = new Attribute(p.MikuTextMuted, p.MikuBgSurface),
            Editable = new Attribute(p.MikuTextWhite, Color.None)
        };
    }

    private static Scheme CreateDialogScheme(MikuPalette p)
    {
        return new Scheme
        {
            Normal = new Attribute(p.MikuTextWhite, p.MikuBgDialog),
            Focus = new Attribute(Color.White, p.QqGreenDark),
            HotNormal = new Attribute(p.MikuPinkAccent, p.MikuBgDialog),
            HotFocus = new Attribute(Color.White, p.MikuPinkAccent),
            Disabled = new Attribute(p.MikuTextMuted, p.MikuBgDialog),
            Highlight = new Attribute(p.QqGreenPrimary, p.MikuBgDialog),
            Active = new Attribute(p.QqPlayingFocusText, p.QqGreenDark),
            ReadOnly = new Attribute(p.MikuTextMuted, p.MikuBgDialog),
            Editable = new Attribute(p.MikuTextWhite, p.MikuBgDialog)
        };
    }

    private static Scheme CreatePlayerBarScheme(MikuPalette p)
    {
        return new Scheme
        {
            Normal = new Attribute(p.QqGreenPrimary, p.MikuBgSurface),
            Focus = new Attribute(Color.White, p.QqGreenDark),
            HotNormal = new Attribute(p.MikuPinkAccent, p.MikuBgSurface),
            HotFocus = new Attribute(Color.White, p.MikuPinkAccent),
            Disabled = new Attribute(p.MikuTextMuted, p.MikuBgSurface),
            Highlight = new Attribute(p.QqGreenLight, p.MikuBgSurface),
            Active = new Attribute(p.QqGreenPrimary, p.MikuBgSurface),
            ReadOnly = new Attribute(p.MikuTextMuted, p.MikuBgSurface),
            Editable = new Attribute(p.MikuTextWhite, Color.None)
        };
    }

    private static Scheme CreateFavoriteActiveScheme(MikuPalette p)
    {
        return new Scheme
        {
            Normal = new Attribute(p.QqGreenLight, p.MikuBgSurface),
            Focus = new Attribute(Color.White, p.QqGreenDark),
            HotNormal = new Attribute(p.QqGreenPrimary, p.MikuBgSurface),
            HotFocus = new Attribute(Color.White, p.QqGreenPrimary),
            Disabled = new Attribute(p.MikuTextMuted, p.MikuBgSurface),
            Highlight = new Attribute(p.QqGreenLight, p.MikuBgSurface),
            Active = new Attribute(p.QqGreenLight, p.MikuBgSurface),
            ReadOnly = new Attribute(p.MikuTextMuted, p.MikuBgSurface),
            Editable = new Attribute(p.MikuTextWhite, Color.None)
        };
    }

    private static Scheme CreateLyricScheme(MikuPalette p)
    {
        return new Scheme
        {
            Normal = new Attribute(p.QqTextLyricDim, Color.None),
            Focus = new Attribute(p.QqGreenPrimary, Color.None),
            HotNormal = new Attribute(p.QqGreenLight, Color.None),
            HotFocus = new Attribute(p.QqGreenLight, Color.None),
            Disabled = new Attribute(p.MikuTextMuted, Color.None),
            Highlight = new Attribute(p.QqGreenPrimary, Color.None),
            Active = new Attribute(p.QqGreenPrimary, Color.None),
            ReadOnly = new Attribute(p.QqTextLyricDim, Color.None),
            Editable = new Attribute(p.QqGreenPrimary, Color.None)
        };
    }

    private static Scheme CreateFrameBorderDimScheme(MikuPalette p)
    {
        return new Scheme
        {
            Normal = new Attribute(p.FrameBorderDimColor, Color.None),
            Focus = new Attribute(p.QqGreenPrimary, Color.None),
            HotNormal = new Attribute(p.MikuPinkAccent, Color.None),
            HotFocus = new Attribute(Color.White, p.MikuPinkAccent),
            Disabled = new Attribute(p.MikuTextMuted, Color.None),
            Highlight = new Attribute(p.FrameBorderDimColor, Color.None),
            Active = new Attribute(p.FrameBorderDimColor, Color.None),
            ReadOnly = new Attribute(p.FrameBorderDimColor, Color.None),
            Editable = new Attribute(p.FrameBorderDimColor, Color.None)
        };
    }

    private static Scheme CreateFrameBorderActiveScheme(MikuPalette p)
    {
        return new Scheme
        {
            Normal = new Attribute(p.QqGreenPrimary, Color.None),
            Focus = new Attribute(p.QqGreenPrimary, Color.None),
            HotNormal = new Attribute(p.MikuPinkAccent, Color.None),
            HotFocus = new Attribute(Color.White, p.MikuPinkAccent),
            Disabled = new Attribute(p.MikuTextMuted, Color.None),
            Highlight = new Attribute(p.QqGreenLight, Color.None),
            Active = new Attribute(p.QqGreenPrimary, Color.None),
            ReadOnly = new Attribute(p.QqGreenPrimary, Color.None),
            Editable = new Attribute(p.QqGreenPrimary, Color.None)
        };
    }

    private static Scheme CreateTitleHighlightScheme(MikuPalette p)
    {
        return new Scheme
        {
            Normal = new Attribute(p.QqGreenPrimary, Color.None),
            Focus = new Attribute(p.QqGreenLight, Color.None),
            HotNormal = new Attribute(p.QqGreenLight, Color.None),
            HotFocus = new Attribute(Color.White, p.MikuPinkAccent),
            Disabled = new Attribute(p.MikuTextMuted, Color.None),
            Highlight = new Attribute(p.QqGreenLight, Color.None),
            Active = new Attribute(p.QqGreenPrimary, Color.None),
            ReadOnly = new Attribute(p.QqGreenPrimary, Color.None),
            Editable = new Attribute(p.QqGreenPrimary, Color.None)
        };
    }

    private static Scheme CreateTransparentDialogScheme(MikuPalette p)
    {
        return new Scheme
        {
            Normal = new Attribute(p.MikuTextWhite, Color.None),
            Focus = new Attribute(Color.White, p.QqGreenDark),
            HotNormal = new Attribute(p.MikuPinkAccent, Color.None),
            HotFocus = new Attribute(Color.White, p.MikuPinkAccent),
            Disabled = new Attribute(p.MikuTextMuted, Color.None),
            Highlight = new Attribute(p.QqGreenPrimary, Color.None),
            Active = new Attribute(Color.White, p.QqGreenActive),
            ReadOnly = new Attribute(p.MikuTextMuted, Color.None),
            Editable = new Attribute(p.MikuTextWhite, Color.None)
        };
    }

    private static Scheme CreateQrCodeScheme()
    {
        var attr = new Attribute(Color.Black, Color.White);
        return new Scheme
        {
            Normal = attr,
            Focus = attr,
            HotNormal = attr,
            HotFocus = attr,
            Disabled = attr,
            Highlight = attr,
            Active = attr,
            ReadOnly = attr,
            Editable = attr
        };
    }

    private static Scheme CreateSearchCategoryActiveScheme(MikuPalette p)
    {
        return new Scheme
        {
            Normal = new Attribute(Color.White, p.QqGreenDark),
            Focus = new Attribute(Color.White, p.QqGreenPrimary),
            HotNormal = new Attribute(p.QqPlayingFocusText, p.QqGreenDark),
            HotFocus = new Attribute(Color.White, p.QqGreenPrimary),
            Disabled = new Attribute(p.MikuTextMuted, Color.None),
            Highlight = new Attribute(p.QqPlayingFocusText, p.QqGreenDark),
            Active = new Attribute(Color.White, p.QqGreenDark),
            ReadOnly = new Attribute(Color.White, p.QqGreenDark),
            Editable = new Attribute(Color.White, p.QqGreenDark)
        };
    }

    private static Scheme CreateSearchCategoryDimScheme(MikuPalette p)
    {
        return new Scheme
        {
            Normal = new Attribute(p.MikuTextMuted, Color.None),
            Focus = new Attribute(p.QqGreenLight, Color.None),
            HotNormal = new Attribute(p.MikuTextSub, Color.None),
            HotFocus = new Attribute(Color.White, p.QqGreenPrimary),
            Disabled = new Attribute(p.MikuTextMuted, Color.None),
            Highlight = new Attribute(p.MikuTextMuted, Color.None),
            Active = new Attribute(p.MikuTextMuted, Color.None),
            ReadOnly = new Attribute(p.MikuTextMuted, Color.None),
            Editable = new Attribute(p.MikuTextMuted, Color.None)
        };
    }

    /// <summary>
    /// 初始化并应用默认或配置的主题模式。
    /// </summary>
    public static void Apply()
    {
        GlyphSettings.Current = GlyphSettings.Current with
        {
            LeftBracket = new Rune('['),
            RightBracket = new Rune(']')
        };
        ButtonSettings.Current = ButtonSettings.Current with
        {
            DefaultShadow = ShadowStyles.None
        };
        DialogSettings.Current = DialogSettings.Current with
        {
            DefaultShadow = ShadowStyles.None
        };

        ApplyTheme(Models.UserConfig.Current.ThemeMode);
    }

    /// <summary>
    /// 应用指定的主题模式并重新注册至 SchemeManager。
    /// </summary>
    /// <param name="themeMode">主题模式 ("auto", "light", "dark")。</param>
    public static void ApplyTheme(string? themeMode)
    {
        CurrentThemeMode = string.IsNullOrWhiteSpace(themeMode) ? "auto" : themeMode;
        bool isLight = ResolveIsLight(CurrentThemeMode);
        IsLightMode = isLight;
        s_currentPalette = isLight ? s_lightPalette : s_darkPalette;

        Base = CreateBaseScheme(s_currentPalette);
        Dialog = CreateDialogScheme(s_currentPalette);
        PlayerBar = CreatePlayerBarScheme(s_currentPalette);
        FavoriteActive = CreateFavoriteActiveScheme(s_currentPalette);
        Lyric = CreateLyricScheme(s_currentPalette);
        FrameBorder = CreateFrameBorderActiveScheme(s_currentPalette);
        FrameBorderDim = CreateFrameBorderDimScheme(s_currentPalette);
        FrameBorderActive = CreateFrameBorderActiveScheme(s_currentPalette);
        TitleHighlight = CreateTitleHighlightScheme(s_currentPalette);
        QrCode = CreateQrCodeScheme();
        SearchCategoryActive = CreateSearchCategoryActiveScheme(s_currentPalette);
        SearchCategoryDim = CreateSearchCategoryDimScheme(s_currentPalette);
        TransparentDialog = CreateTransparentDialogScheme(s_currentPalette);

        SchemeManager.AddScheme("Base", Base);
        SchemeManager.AddScheme("Dialog", Dialog);
        SchemeManager.AddScheme("PlayerBar", PlayerBar);
        SchemeManager.AddScheme("Lyric", Lyric);
        SchemeManager.AddScheme("FrameBorder", FrameBorder);
        SchemeManager.AddScheme("FrameBorderDim", FrameBorderDim);
        SchemeManager.AddScheme("FrameBorderActive", FrameBorderActive);
        SchemeManager.AddScheme("TitleHighlight", TitleHighlight);
        SchemeManager.AddScheme("QrCode", QrCode);
    }

    private static bool ResolveIsLight(string themeMode)
    {
        if (string.Equals(themeMode, "light", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        if (string.Equals(themeMode, "dark", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return ThemeDetector.DetectIsLight();
    }

    /// <summary>
    /// 递归为指定 View 及其子视图应用 Scheme。
    /// </summary>
    /// <param name="view">目标视图。</param>
    /// <param name="scheme">要应用的方案，若为 null 则使用 Base 方案。</param>
    public static void ApplyTo(View view, Scheme? scheme = null)
    {
        if (view is ArtistAlbumDetailView detailView)
        {
            detailView.ApplyInnerScheme();
            return;
        }

        scheme ??= Base;
        view.SetScheme(scheme);
        if (view is Terminal.Gui.Views.Button btn)
        {
            btn.ShadowStyle = ShadowStyles.None;
        }
        foreach (var sub in view.SubViews)
        {
            ApplyTo(sub, scheme);
        }
    }
}
