using System.Collections.ObjectModel;
using System.Text;
using Terminal.Gui.App;
using Terminal.Gui.Drawing;
using Terminal.Gui.Input;
using Terminal.Gui.Text;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using QmTui.Api;
using QmTui.Models;
using QmTui.Services;

namespace QmTui.UI;

/// <summary>
/// 听歌足迹视图组件。
/// </summary>
public sealed class ListeningFootprintView : FrameView
{
    private readonly View _headerBar;
    private readonly Label _tabDayLabel;
    private readonly Label _tabWeekLabel;
    private readonly Label _tabMonthLabel;
    private readonly Label _tabYearLabel;
    private readonly Label _dateNavigatorLabel;

    private readonly FrameView _statsFrame;
    private readonly Label _summaryLabel;
    private readonly Label _chartLabel;

    private readonly FrameView _singersFrame;
    private readonly Label _singersLabel;

    private readonly FrameView _rankingFrame;
    private readonly ListView _rankingListView;

    private ListeningReportPeriod _currentPeriod = ListeningReportPeriod.Day;
    private string? _currentDateKey;
    private ListeningReportData? _currentData;
    private readonly List<ListeningRankSong> _rankSongs = [];

    public event Func<Song, Task>? SongAccepted;

    public ListeningFootprintView()
    {
        Title = "听歌足迹";
        X = 0;
        Y = 0;
        Width = Dim.Fill();
        Height = Dim.Fill();
        CanFocus = true;

        // 1. 顶部周期维度 Tab 与日期翻页导航
        _headerBar = new View
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = 1,
            CanFocus = false
        };

        _tabDayLabel = new Label { Text = "[ 日 ]", X = 1, Y = 0 };
        _tabWeekLabel = new Label { Text = " 周 ", X = 8, Y = 0 };
        _tabMonthLabel = new Label { Text = " 月 ", X = 13, Y = 0 };
        _tabYearLabel = new Label { Text = " 年 ", X = 18, Y = 0 };

        _dateNavigatorLabel = new Label
        {
            Text = "[<] 载入中... [>]",
            X = Pos.AnchorEnd(32),
            Y = 0
        };

        _headerBar.Add(_tabDayLabel, _tabWeekLabel, _tabMonthLabel, _tabYearLabel, _dateNavigatorLabel);
        _headerBar.MouseEvent += (sender, m) =>
        {
            if (!m.Flags.HasFlag(MouseFlags.LeftButtonClicked) && !m.Flags.HasFlag(MouseFlags.LeftButtonPressed))
            {
                return;
            }

            int x = m.Position?.X ?? -1;
            if (x < 0) return;

            if (x is >= 1 and <= 5)
            {
                _ = LoadFootprintAsync(ListeningReportPeriod.Day, null, forceRefresh: false);
            }
            else if (x is >= 7 and <= 11)
            {
                _ = LoadFootprintAsync(ListeningReportPeriod.Week, null, forceRefresh: false);
            }
            else if (x is >= 12 and <= 16)
            {
                _ = LoadFootprintAsync(ListeningReportPeriod.Month, null, forceRefresh: false);
            }
            else if (x is >= 17 and <= 21)
            {
                _ = LoadFootprintAsync(ListeningReportPeriod.Year, null, forceRefresh: false);
            }
            else
            {
                int navLeft = _dateNavigatorLabel.Frame.X;
                if (x >= navLeft && x <= navLeft + 4)
                {
                    _ = NavigateDateRelativeAsync(-1);
                }
                else if (x >= navLeft + _dateNavigatorLabel.Frame.Width - 4)
                {
                    _ = NavigateDateRelativeAsync(1);
                }
            }
        };
        Add(_headerBar);

        // 2. 统计概览面板（核心收听时长、活跃时段与 Unicode 字符图表）
        _statsFrame = new FrameView
        {
            Title = "核心指标与走势",
            X = 0,
            Y = Pos.Bottom(_headerBar),
            Width = Dim.Fill(),
            Height = 6,
            CanFocus = false
        };

        _summaryLabel = new Label
        {
            Text = "正在从云端同步听歌足迹数据...",
            X = 1,
            Y = 0,
            Width = Dim.Percent(40),
            Height = Dim.Fill()
        };

        _chartLabel = new Label
        {
            Text = "",
            X = Pos.Right(_summaryLabel) + 1,
            Y = 0,
            Width = Dim.Fill() - 1,
            Height = Dim.Fill()
        };

        _statsFrame.Add(_summaryLabel, _chartLabel);
        Add(_statsFrame);

        // 3. 常听歌手分布面板
        _singersFrame = new FrameView
        {
            Title = "常听歌手分布",
            X = 0,
            Y = Pos.Bottom(_statsFrame),
            Width = Dim.Fill(),
            Height = 5,
            CanFocus = false
        };

        _singersLabel = new Label
        {
            Text = "暂无歌手收听分布数据",
            X = 1,
            Y = 0,
            Width = Dim.Fill() - 2,
            Height = Dim.Fill()
        };

        _singersFrame.Add(_singersLabel);
        Add(_singersFrame);

        // 4. 单曲收听排行榜列表
        _rankingFrame = new FrameView
        {
            Title = "单曲收听排行 (Enter 点播)",
            X = 0,
            Y = Pos.Bottom(_singersFrame),
            Width = Dim.Fill(),
            Height = Dim.Fill(),
            CanFocus = true
        };

        _rankingListView = new ListView
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(),
            CanFocus = true
        };

        _rankingListView.SetScheme(MikuTheme.Base);
        _rankingListView.KeyBindings.Remove(Key.Space);

        _rankingListView.Accepted += async (s, e) =>
        {
            var idx = _rankingListView.SelectedItem ?? 0;
            if (idx >= 0 && idx < _rankSongs.Count)
            {
                var song = _rankSongs[idx].Song;
                if (SongAccepted != null)
                {
                    await SongAccepted.Invoke(song);
                }
            }
        };

        _rankingFrame.Add(_rankingListView);
        Add(_rankingFrame);
    }

    /// <summary>
    /// 加载默认最新足迹数据。
    /// </summary>
    public async Task LoadDefaultAsync()
    {
        await LoadFootprintAsync(_currentPeriod, _currentDateKey, forceRefresh: false);
    }

    /// <summary>
    /// 聚焦到排行榜曲目列表。
    /// </summary>
    public void SetFocusToList()
    {
        _rankingListView.SetFocus();
    }

    private async Task LoadFootprintAsync(ListeningReportPeriod period, string? date, bool forceRefresh)
    {
        _currentPeriod = period;
        _currentDateKey = date;
        UpdateTabStyles();

        var report = await ListeningFootprintCacheService.GetStatsAsync(period, date, forceRefresh);
        _currentData = report;

        Application.Invoke(() =>
        {
            RenderReport(report);
        });
    }

    private void UpdateTabStyles()
    {
        _tabDayLabel.Text = _currentPeriod == ListeningReportPeriod.Day ? "[ 日 ]" : "  日  ";
        _tabWeekLabel.Text = _currentPeriod == ListeningReportPeriod.Week ? "[ 周 ]" : "  周  ";
        _tabMonthLabel.Text = _currentPeriod == ListeningReportPeriod.Month ? "[ 月 ]" : "  月  ";
        _tabYearLabel.Text = _currentPeriod == ListeningReportPeriod.Year ? "[ 年 ]" : "  年  ";

        _tabDayLabel.SetScheme(_currentPeriod == ListeningReportPeriod.Day ? MikuTheme.SearchCategoryActive : MikuTheme.SearchCategoryDim);
        _tabWeekLabel.SetScheme(_currentPeriod == ListeningReportPeriod.Week ? MikuTheme.SearchCategoryActive : MikuTheme.SearchCategoryDim);
        _tabMonthLabel.SetScheme(_currentPeriod == ListeningReportPeriod.Month ? MikuTheme.SearchCategoryActive : MikuTheme.SearchCategoryDim);
        _tabYearLabel.SetScheme(_currentPeriod == ListeningReportPeriod.Year ? MikuTheme.SearchCategoryActive : MikuTheme.SearchCategoryDim);
    }

    private void RenderReport(ListeningReportData report)
    {
        UpdateTabStyles();

        // 1. 日期导航文本
        string displayDate = FormatDateKey(report.Period, report.DateKey);
        _dateNavigatorLabel.Text = $"[<] {displayDate} [>]";

        // 2. 指标汇总
        string timeStr = FormatDuration(report.ListenTimeSec);
        var sbSummary = new StringBuilder();
        string periodTitle = report.Period switch
        {
            ListeningReportPeriod.Day => "今日累计收听",
            ListeningReportPeriod.Week => "本周日均收听",
            ListeningReportPeriod.Month => "本月累计收听",
            ListeningReportPeriod.Year => "年度累计收听",
            _ => "累计收听"
        };
        sbSummary.AppendLine($"{periodTitle}: {timeStr}");
        if (report.ActiveHour >= 0 && report.Period == ListeningReportPeriod.Day)
        {
            sbSummary.AppendLine($"最活跃时段: {report.ActiveHour:D2}:00");
        }
        else if (report.Percent > 0)
        {
            string trend = report.UpOrDown >= 0 ? "+" : "-";
            sbSummary.AppendLine($"环比变化: {trend}{report.Percent}%");
        }
        sbSummary.AppendLine($"常听歌手数: {report.TopSingers.Count} 位");
        _summaryLabel.Text = sbSummary.ToString().TrimEnd();

        // 3. 走势字符图表（Unicode 柱状字符）
        _chartLabel.Text = BuildAsciiChart(report.Period, report.ChartPoints);

        // 4. 常听歌手分布条
        _singersLabel.Text = BuildSingersBar(report.TopSingers);

        // 5. 单曲收听排行榜
        _rankSongs.Clear();
        _rankSongs.AddRange(report.RankSongs);

        var listItems = new List<string>(_rankSongs.Count);
        for (int i = 0; i < _rankSongs.Count; i++)
        {
            var item = _rankSongs[i];
            var s = item.Song;
            string durStr = s.Duration > 0 ? $"{s.Duration / 60:D2}:{s.Duration % 60:D2}" : "--:--";
            listItems.Add($"{i + 1,2}. {s.Title} - {s.Artist} ({durStr}) [收听 {item.ListenCnt} 次]");
        }

        if (listItems.Count == 0)
        {
            listItems.Add("当前周期暂无单曲收听排行记录");
        }

        _rankingListView.SetSource(new ObservableCollection<string>(listItems));
        _rankingFrame.Title = $"单曲收听排行 (共 {_rankSongs.Count} 首，Enter 点播)";
    }

    private static string FormatDuration(long sec)
    {
        if (sec <= 0) return "0 分钟";
        long h = sec / 3600;
        long m = (sec % 3600) / 60;
        long s = sec % 60;
        if (h > 0)
        {
            return $"{h} 小时 {m} 分钟";
        }
        if (m > 0)
        {
            return $"{m} 分钟 {s} 秒";
        }
        return $"{s} 秒";
    }

    private static string FormatDateKey(ListeningReportPeriod period, string key)
    {
        if (string.IsNullOrWhiteSpace(key)) return "最新数据";

        return period switch
        {
            ListeningReportPeriod.Day when key.Length == 8 => $"{key[..4]}-{key[4..6]}-{key[6..8]}",
            ListeningReportPeriod.Week when key.Length == 8 => $"{key[4..6]}月{key[6..8]}日 当周",
            ListeningReportPeriod.Month when key.Length == 6 => $"{key[..4]}年{key[4..6]}月",
            _ => key
        };
    }

    private static string BuildAsciiChart(ListeningReportPeriod period, IReadOnlyList<ListeningChartPoint> points)
    {
        if (points.Count == 0) return "暂无时段走势分布数据";

        char[] bars = [' ', ' ', '▂', '▃', '▄', '▅', '▆', '▇', '█'];
        long maxVal = 0;
        foreach (var p in points)
        {
            if (p.Value > maxVal) maxVal = p.Value;
        }

        var sb = new StringBuilder();
        if (period == ListeningReportPeriod.Day)
        {
            sb.AppendLine("24小时听歌走势分布:");
            var barLine = new StringBuilder();
            for (int i = 0; i < points.Count && i < 24; i++)
            {
                long v = points[i].Value;
                int barIdx = maxVal > 0 ? (int)(v * 8 / maxVal) : 0;
                barIdx = Math.Clamp(barIdx, 0, 8);
                barLine.Append(bars[barIdx]);
            }
            sb.AppendLine(barLine.ToString());
            sb.AppendLine("00:00   06:00   12:00   18:00   23:00");
        }
        else
        {
            sb.AppendLine("周期走势点位分布:");
            var barLine = new StringBuilder();
            foreach (var p in points)
            {
                int barIdx = maxVal > 0 ? (int)(p.Value * 8 / maxVal) : 0;
                barIdx = Math.Clamp(barIdx, 0, 8);
                barLine.Append(bars[barIdx]).Append(' ');
            }
            sb.AppendLine(barLine.ToString().TrimEnd());
        }

        return sb.ToString().TrimEnd();
    }

    private static string BuildSingersBar(IReadOnlyList<ListeningSingerStat> singers)
    {
        if (singers.Count == 0) return "当前周期暂无常听歌手统计数据";

        var sb = new StringBuilder();
        int maxItems = Math.Min(3, singers.Count);

        int maxNameCols = 16;
        for (int i = 0; i < maxItems; i++)
        {
            int cols = singers[i].SingerName.GetColumns();
            if (cols > maxNameCols) maxNameCols = cols;
        }

        for (int i = 0; i < maxItems; i++)
        {
            var s = singers[i];
            long min = s.ListenTimeSec / 60;
            string singerName = s.SingerName;
            int nameCols = singerName.GetColumns();
            string padding = new string(' ', Math.Max(0, maxNameCols - nameCols));

            string durationStr = min > 0 ? $"{min,4} 分钟" : $"{s.ListenTimeSec,4} 秒";
            sb.AppendLine($"#{i + 1} {singerName}{padding}  {durationStr}");
        }

        return sb.ToString().TrimEnd();
    }

    private async Task NavigateDateRelativeAsync(int delta)
    {
        if (_currentData == null) return;

        var dates = _currentData.AvailableDates;
        if (dates.Count == 0) return;

        int curIdx = string.IsNullOrWhiteSpace(_currentDateKey)
            ? 0
            : dates.ToList().IndexOf(_currentDateKey);

        if (curIdx < 0) curIdx = 0;

        // delta = -1: 往历史翻 (在降序列表中 index + 1)
        // delta = 1: 往前翻 (index - 1)
        int nextIdx = curIdx - delta;
        if (nextIdx >= 0 && nextIdx < dates.Count)
        {
            await LoadFootprintAsync(_currentPeriod, dates[nextIdx], forceRefresh: false);
        }
    }
}
