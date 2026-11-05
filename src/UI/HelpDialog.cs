using System.Collections.ObjectModel;
using Terminal.Gui.App;
using Terminal.Gui.Drawing;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace QmTui.UI;

/// <summary>
/// 快捷键与鼠标操作总览（只读），由全局 ? / F1 键唤起
/// </summary>
/// <remarks>
/// 文案只描述"实际生效"的按键：HandleGlobalKeyDownAsync 先于各视图 handler 执行并吞掉按键，
/// 因此部分视图级键位分支实际不会触发，此处一律按全局分发的真实行为书写。
/// shortcut: 静态文案与代码无绑定关系, 新增或修改快捷键时必须同步下面的 KeysHelpLines;
/// 若键位继续增长再考虑抽成集中式键位表。
/// </remarks>
public sealed class HelpDialog : Dialog
{
    // 键名文案必须放得进这一列；超宽的键名会把说明文字顶到更右边（HelpDialogTests 会拦）
    private const int KeyColumnWidth = 13;

    internal static readonly (string Group, (string Key, string Desc)[] Rows)[] KeysHelpLines =
    [
        ("全局（任何界面均可用）",
        [
            ("Space", "播放 / 暂停"),
            ("Tab / S-Tab", "切换焦点：侧栏/列表/歌词/控制栏"),
            ("V", "全屏播放界面"),
            ("P", "沉浸模式"),
            ("O", "播放模式：循环/单曲/随机"),
            ("M", "静音"),
            ("- / +", "音量 ±5"),
            ("S", "收藏 / 取消收藏"),
            ("A", "当前曲加入歌单"),
            ("C", "评论"),
            ("T", "歌词译文开关"),
            ("Y", "匹配歌词 / 再按撤销"),
            ("Z", "逐字过渡开关"),
            ("G", "列表内查找"),
            ("J / L", "上一首 / 下一首"),
            ("K", "一起听"),
            ("U", "登录"),
            ("R", "听歌识曲"),
            ("W", "Web 遥控"),
            ("H", "主题：自动 / 亮 / 暗"),
            ("B", "桌面通知开关"),
            ("/  F3  C-V", "搜索（C-V 顺带粘贴）"),
            ("Q", "退出（二次确认）"),
            ("Esc", "逐级返回"),
        ]),
        ("主列表",
        [
            ("Enter", "播放选中曲；歌手/专辑列进详情"),
            ("左/右", "在歌名/歌手/专辑列间移动"),
            ("上/下", "移动光标（到底加载下一页）"),
            ("PgUp / PgDn", "列表翻页"),
            ("Home", "回到列表顶部"),
            ("N", "插队为下一首"),
            ("D / Del", "移出当前列表（歌单/最近播放）"),
            ("1 / 2 / 3", "右上角第 1/2/3 个按钮"),
        ]),
        ("全屏播放界面 (V)",
        [
            ("Esc / V", "退出播放界面"),
            ("E", "播放队列"),
            ("C", "评论"),
            ("T", "歌词译文"),
            ("A", "当前曲加入歌单"),
            ("Y", "匹配歌词"),
            ("U", "登录"),
            ("P", "沉浸模式"),
            ("方向键", "评论翻页（PgUp/PgDn/Home/End）"),
            ("Enter", "选中评论（艺人需双击）"),
        ]),
        ("播放队列抽屉 (E)",
        [
            ("Enter / 双击", "播放选中项"),
            ("上/下", "选择"),
            ("D / Del", "移除"),
            ("C", "清空待播"),
            ("Esc / E", "关闭"),
        ]),
        ("听歌足迹",
        [
            ("1 / 2 / 3 / 4", "日 / 周 / 月 / 年"),
            ("左/右", "切换周期"),
            ("< > , . [ ]", "切换日期"),
            ("下", "到底后再按进入榜单"),
            ("Shift+上", "年视图回到喜欢"),
        ]),
        ("本地音乐 / WebDAV",
        [
            ("A", "添加目录 / 导入当前目录"),
            ("R", "重新扫描 / 刷新"),
            ("F", "管理目录 / WebDAV 站点"),
            ("D", "删目录 / 目录树切换"),
            ("S", "WebDAV 嗅探元数据"),
            ("Backspace", "WebDAV 返回上级目录"),
        ]),
        ("各弹窗通用",
        [
            ("Esc", "关闭（音质/登录/艺人 Q 亦可）"),
            ("N / D", "加入歌单：新建 / 删除"),
            ("Enter", "加入歌单：添加"),
            ("A", "收起加入歌单弹窗"),
            ("1 / 2 / 3", "登录：QQ/微信/服务号"),
            ("T / W / R / B", "登录：扫码/网页/刷新/浏览器"),
            ("K", "关闭一起听（输入框除外）"),
            ("1-9", "选择艺人：序号直达"),
        ]),
        ("鼠标（部分操作只有鼠标，无按键）",
        [
            ("单击 / 双击", "选中 / 播放"),
            ("点击进度条", "seek 到点击位置"),
            ("Enter+左/右", "调进度 ±10s"),
            ("音量按钮", "单击/中键静音，滚轮 ±5"),
            ("双击歌词行", "跳到该句；滚轮翻歌词"),
            ("歌曲信息行", "打开播放界面（迷你封面同理）"),
            ("点击面板标题", "面板间移焦；歌词标题=评论"),
            ("点击底部 [C]", "开关评论（右侧整段为点击区）"),
            ("转存 / 分享", "音质等按钮仅鼠标"),
            ("任意键", "退出 AOD（Q 为退出确认）"),
        ]),
    ];

    private static Scheme TransparentDialogScheme => MikuTheme.TransparentDialog;

    public HelpDialog()
    {
        Title = "快捷键与鼠标操作帮助";
        Width = 76;
        Height = 22;
        SetScheme(TransparentDialogScheme);

        var listView = new ListView
        {
            X = 1,
            Y = 0,
            Width = Dim.Fill(1),
            Height = Dim.Fill(2)
        };
        listView.SetScheme(TransparentDialogScheme);
        listView.KeyBindings.Remove(Key.Space);
        listView.SetSource(new ObservableCollection<string>(BuildDisplayLines()));
        Add(listView);

        var hintLabel = new Label
        {
            Text = "[↑↓/PgUp] 滚动   [Esc / ? / F1] 关闭",
            X = 2,
            Y = Pos.AnchorEnd(1)
        };
        hintLabel.SetScheme(new Scheme
        {
            Normal = new Terminal.Gui.Drawing.Attribute(MikuTheme.MikuTextMuted, Color.None)
        });
        Add(hintLabel);

        void HandleKeyAction(Key k)
        {
            if (!MainWindow.IsTerminalWindowFocused)
            {
                k.Handled = true;
                return;
            }

            bool isClose = k == Key.Esc || k == Key.F1 || k.AsRune.Value == '?';
            if (isClose)
            {
                k.Handled = true;
                Application.RequestStop(this);
            }
        }

        listView.KeyDown += (s, k) => HandleKeyAction(k);
        KeyDown += (s, k) =>
        {
            if (k.Handled) return;
            HandleKeyAction(k);
        };
    }

    internal static List<string> BuildDisplayLines()
    {
        var lines = new List<string>();
        foreach (var (group, rows) in KeysHelpLines)
        {
            lines.Add($"── {group} ──");
            foreach (var (key, desc) in rows)
            {
                // 按显示宽度补齐：中文字符占 2 列；+1 保证键名占满列宽时也留一个空格
                int keyCols = key.Sum(ch => ch > 127 ? 2 : 1);
                lines.Add($" {key}{new string(' ', Math.Max(1, KeyColumnWidth + 1 - keyCols))}{desc}");
            }
            lines.Add("");
        }

        return lines;
    }
}
