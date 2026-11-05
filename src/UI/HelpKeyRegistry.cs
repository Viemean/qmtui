namespace QmTui.UI;

/// <summary>
/// 集中式快捷键与鼠标操作只读注册表
/// </summary>
public static class HelpKeyRegistry
{
    public const int KeyColumnWidth = 13;
    public const string IntroTip = "使用 Tab 切换焦点区域，方向按键切换焦点按钮，Enter 确认";
    public const string MouseTip = "鼠标可完成大部分功能操作，尝试使用鼠标点击可点击的按钮";

    public static readonly (string Group, (string Key, string Desc)[] Rows)[] Groups =
    [
        ("进阶快捷操作",
        [
            ("N", "主列表：插队为下一首"),
            ("D / Del", "主列表：移出当前列表"),
            ("G", "列表内快速查找"),
            ("Y", "匹配歌词 / 撤销匹配：只对本地音乐和WebDav有效"),
            ("Z", "逐字过渡开关"),
            ("/  F3  C-V", "搜索（C-V 粘贴搜索）"),
            ("W", "Web 服务：无音频输出时转外部浏览器"),
            ("K", "一起听功能窗口"),
            ("B", "音乐切换通知开关"),
            ("Backspace", "WebDAV 返回上级目录"),
        ]),
        ("常用界面操作",
        [
            ("Space", "播放 / 暂停"),
            ("Tab / S-Tab", "切换焦点区域"),
            ("J / L", "上一首 / 下一首"),
            ("- / +", "音量 ±5"),
            ("M", "静音"),
            ("F", "收藏 / 取消收藏"),
            ("A", "当前选中歌曲加入歌单"),
            ("C", "打开评论"),
            ("V", "全屏播放界面"),
            ("P", "沉浸全屏模式"),
            ("O", "切换播放循环模式"),
            ("T", "歌词译文开关"),
            ("U", "登录账号管理"),
            ("R", "听歌识曲"),
            ("H", "切换主题：自动/亮/暗"),
            ("Esc", "逐级返回 / 关闭弹窗"),
            ("Q", "退出应用"),
        ]),
    ];
}
