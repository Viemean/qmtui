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
/// 听歌足迹视图组件（方案 A：上下流式分层布局，指标概览 + 走势字符图 + 歌手占比 + 单曲收听排行榜）。
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
    private readonly ListView _favoritesListView;
    private readonly List<ListeningMonthFavoriteSong> _favoriteSongs = [];

    private readonly FrameView _rankingFrame;
    private readonly ListView _rankingListView;

    private ListeningReportPeriod _currentPeriod = ListeningReportPeriod.Day;
    private string? _currentDateKey;
    private ListeningReportData? _currentData;
    private readonly List<ListeningRankSong> _rankSongs = [];
    private long _lastNavTick;

    public event Func<Song, Task>? SongAccepted;
    public event Func<IReadOnlyList<Song>, int, Task>? PlayQueueAccepted;
    public event Action<bool>? TabNavigationRequested;

    public ListeningFootprintView()
    {
        Title = "听歌足迹";
        X = 0;
        Y = 0;
        Width = Dim.Fill();
        Height = Dim.Fill();
        CanFocus = true;
        TabStop = Terminal.Gui.ViewBase.TabBehavior.NoStop;

        // 顶部周期维度 Tab 与日期翻页导航
        _headerBar = new View
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = 1,
            CanFocus = false
        };

        _tabDayLabel = new Label { Text = "[1]日", X = 1, Y = 0 };
        _tabWeekLabel = new Label { Text = "[2]周", X = 6, Y = 0 };
        _tabMonthLabel = new Label { Text = "[3]月", X = 11, Y = 0 };
        _tabYearLabel = new Label { Text = "[4]年", X = 16, Y = 0 };

        _dateNavigatorLabel = new Label
        {
            Text = "[<] 载入中... [>]",
            X = Pos.AnchorEnd(32),
            Y = 0
        };

        UpdateTabStyles();
        _headerBar.Add(_tabDayLabel, _tabWeekLabel, _tabMonthLabel, _tabYearLabel, _dateNavigatorLabel);
        _headerBar.MouseEvent += (sender, m) =>
        {
            if (m.Flags != MouseFlags.LeftButtonClicked)
            {
                return;
            }

            int x = m.Position?.X ?? -1;
            if (x < 0) return;

            if (x is >= 1 and <= 5)
            {
                _ = LoadFootprintAsync(ListeningReportPeriod.Day, null, forceRefresh: false);
            }
            else if (x is >= 6 and <= 10)
            {
                _ = LoadFootprintAsync(ListeningReportPeriod.Week, null, forceRefresh: false);
            }
            else if (x is >= 11 and <= 15)
            {
                _ = LoadFootprintAsync(ListeningReportPeriod.Month, null, forceRefresh: false);
            }
            else if (x is >= 16 and <= 20)
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

        // 统计概览面板（核心收听时长、活跃时段与 Unicode 字符图表）
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
            Width = 26,
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

        // 常听歌手分布面板
        _singersFrame = new FrameView
        {
            Title = "常听歌手分布",
            X = 0,
            Y = Pos.Bottom(_statsFrame),
            Width = Dim.Fill(),
            Height = 5,
            CanFocus = true,
            TabStop = Terminal.Gui.ViewBase.TabBehavior.NoStop
        };
        _singersFrame.SetScheme(MikuTheme.FrameBorderDim);

        _singersLabel = new Label
        {
            Text = "暂无歌手收听分布数据",
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill()
        };

        _favoritesListView = new ListView
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(),
            CanFocus = true,
            Visible = false
        };
        _favoritesListView.SetScheme(MikuTheme.Base);
        _favoritesListView.KeyBindings.Remove(Key.Space);
        _favoritesListView.KeyBindings.Remove(Key.Tab);
        _favoritesListView.KeyBindings.Remove(Key.Tab.WithShift);

        _favoritesListView.KeyDown += (s, k) =>
        {
            if (k == Key.Tab || k.AsRune.Value == '\t' || k.ToString().Contains("Tab"))
            {
                k.Handled = true;
                if (k.IsShift)
                {
                    TabNavigationRequested?.Invoke(false);
                }
                else
                {
                    _rankingListView?.SetFocus();
                }
                return;
            }

            if (k == Key.CursorDown && (_favoritesListView.SelectedItem ?? 0) >= _favoriteSongs.Count - 1)
            {
                k.Handled = true;
                _rankingListView?.SetFocus();
                return;
            }

            if (HandleFootprintShortcut(k))
            {
                k.Handled = true;
            }
        };

        _favoritesListView.Accepted += async (s, e) =>
        {
            var idx = _favoritesListView.SelectedItem ?? 0;
            if (idx >= 0 && idx < _favoriteSongs.Count)
            {
                var songs = _favoriteSongs.Select(f => f.Song).ToList();
                if (PlayQueueAccepted != null)
                {
                    await PlayQueueAccepted.Invoke(songs, idx);
                }
                else if (SongAccepted != null)
                {
                    await SongAccepted.Invoke(songs[idx]);
                }
            }
        };

        _singersFrame.Add(_singersLabel, _favoritesListView);
        Add(_singersFrame);

        // 单曲收听排行榜列表
        _rankingFrame = new FrameView
        {
            Title = "单曲收听排行",
            X = 0,
            Y = Pos.Bottom(_singersFrame),
            Width = Dim.Fill(),
            Height = Dim.Fill(),
            CanFocus = true,
            TabStop = Terminal.Gui.ViewBase.TabBehavior.NoStop
        };
        _rankingFrame.SetScheme(MikuTheme.FrameBorderDim);

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
        _rankingListView.KeyBindings.Remove(Key.Tab);
        _rankingListView.KeyBindings.Remove(Key.Tab.WithShift);

        _rankingListView.KeyDown += (s, k) =>
        {
            if (k == Key.Tab || k.AsRune.Value == '\t' || k.ToString().Contains("Tab"))
            {
                k.Handled = true;
                if (k.IsShift && _currentPeriod == ListeningReportPeriod.Year && _favoritesListView.Visible)
                {
                    _favoritesListView.SetFocus();
                }
                else
                {
                    TabNavigationRequested?.Invoke(!k.IsShift);
                }
                return;
            }

            if (k == Key.CursorUp && (_rankingListView.SelectedItem ?? 0) <= 0 && _currentPeriod == ListeningReportPeriod.Year && _favoritesListView.Visible)
            {
                k.Handled = true;
                _favoritesListView.SetFocus();
                return;
            }

            if (HandleFootprintShortcut(k))
            {
                k.Handled = true;
            }
        };

        _rankingListView.Accepted += async (s, e) =>
        {
            var idx = _rankingListView.SelectedItem ?? 0;
            if (idx >= 0 && idx < _rankSongs.Count)
            {
                var songs = _rankSongs.Select(r => r.Song).ToList();
                if (PlayQueueAccepted != null)
                {
                    await PlayQueueAccepted.Invoke(songs, idx);
                }
                else if (SongAccepted != null)
                {
                    await SongAccepted.Invoke(songs[idx]);
                }
            }
        };

        _rankingFrame.Add(_rankingListView);
        Add(_rankingFrame);

        // 键盘快捷按键支持（左右键切换周期，快捷键调度）
        KeyDown += (s, k) =>
        {
            if (k == Key.CursorLeft)
            {
                SwitchPeriodRelative(-1);
                k.Handled = true;
            }
            else if (k == Key.CursorRight)
            {
                SwitchPeriodRelative(1);
                k.Handled = true;
            }
            else if (HandleFootprintShortcut(k))
            {
                k.Handled = true;
            }
        };

        ViewportChanged += (s, e) =>
        {
            if (_currentData != null && Viewport.Width > 0)
            {
                RenderReport(_currentData);
            }
        };
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
        if (_currentPeriod == ListeningReportPeriod.Year && _favoritesListView.Visible)
        {
            _favoritesListView.SetFocus();
            if (_favoritesListView.SelectedItem is null or < 0 && _favoriteSongs.Count > 0)
            {
                _favoritesListView.SelectedItem = 0;
            }
        }
        else
        {
            _rankingListView.SetFocus();
            if (_rankingListView.SelectedItem is null or < 0 && _rankSongs.Count > 0)
            {
                _rankingListView.SelectedItem = 0;
            }
        }
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
        _tabDayLabel.Text = "[1]日";
        _tabWeekLabel.Text = "[2]周";
        _tabMonthLabel.Text = "[3]月";
        _tabYearLabel.Text = "[4]年";

        _tabDayLabel.SetScheme(_currentPeriod == ListeningReportPeriod.Day ? MikuTheme.SearchCategoryActive : MikuTheme.SearchCategoryDim);
        _tabWeekLabel.SetScheme(_currentPeriod == ListeningReportPeriod.Week ? MikuTheme.SearchCategoryActive : MikuTheme.SearchCategoryDim);
        _tabMonthLabel.SetScheme(_currentPeriod == ListeningReportPeriod.Month ? MikuTheme.SearchCategoryActive : MikuTheme.SearchCategoryDim);
        _tabYearLabel.SetScheme(_currentPeriod == ListeningReportPeriod.Year ? MikuTheme.SearchCategoryActive : MikuTheme.SearchCategoryDim);
    }

    private void RenderReport(ListeningReportData report)
    {
        UpdateTabStyles();

        // 日期导航文本
        string displayDate = FormatDateKey(report.Period, report.DateKey);
        _dateNavigatorLabel.Text = $"[<] {displayDate} [>]";

        // 指标汇总
        var sbSummary = new StringBuilder();
        if (report.Period == ListeningReportPeriod.Day)
        {
            sbSummary.AppendLine("今日听歌时长:");
            sbSummary.AppendLine(FormatDuration(report.ListenTimeSec));
            if (report.ActiveHour >= 0)
            {
                sbSummary.AppendLine("最活跃时段:");
                sbSummary.AppendLine($"{report.ActiveHour:D2}:00");
            }
        }
        else if (report.Period == ListeningReportPeriod.Week)
        {
            sbSummary.AppendLine("本周日均听歌时长:");
            sbSummary.AppendLine(FormatDuration(report.ListenTimeSec));
            string trendText = FormatTrendText(report.Period, report.Percent, report.UpOrDown);
            if (!string.IsNullOrEmpty(trendText))
            {
                sbSummary.AppendLine(trendText);
            }
        }
        else if (report.Period == ListeningReportPeriod.Month)
        {
            string monthLabel = report.DateKey.Length == 6 && int.TryParse(report.DateKey[4..6], out var mNum)
                ? $"{mNum}月听歌首数:"
                : "本月听歌首数:";
            sbSummary.AppendLine(monthLabel);
            sbSummary.AppendLine($"{report.ListenCnt} 首");
            string trendText = FormatTrendText(report.Period, report.Percent, report.UpOrDown);
            if (!string.IsNullOrEmpty(trendText))
            {
                sbSummary.AppendLine(trendText);
            }
        }
        else if (report.Period == ListeningReportPeriod.Year)
        {
            sbSummary.AppendLine("年度听歌时长:");
            sbSummary.AppendLine(FormatDuration(report.ListenTimeSec));
            if (report.ListenCnt > 0)
            {
                sbSummary.AppendLine($"{report.ListenCnt} 首");
            }
        }
        _summaryLabel.Text = sbSummary.ToString().TrimEnd();

        // 走势字符图表（Unicode 柱状字符）
        _chartLabel.Text = BuildAsciiChart(report.Period, report.DateKey, report.ChartPoints);

        // 计算可用宽度
        int viewWidth = _singersFrame.Viewport.Width;
        if (viewWidth <= 0)
        {
            viewWidth = _rankingListView.Viewport.Width;
        }
        if (viewWidth <= 0)
        {
            int curW = Viewport.Width > 0 ? Viewport.Width : Frame.Width;
            viewWidth = curW > 4 ? curW - 4 : 52;
        }

        // 常听歌手分布条 / 高光时刻 / 每月最爱歌曲
        if (report.Period == ListeningReportPeriod.Year)
        {
            _singersFrame.Title = "每月最爱歌曲";
            _singersFrame.Height = 6;
            _singersLabel.Visible = false;
            _favoritesListView.Visible = true;

            _favoriteSongs.Clear();
            _favoriteSongs.AddRange(report.FavoriteSongsOfMonth);

            int monthColW = 5;
            int favRemain = Math.Max(20, viewWidth - monthColW - 3);
            int favTitleW = Math.Max(12, (int)Math.Round(favRemain * 0.58));
            int favArtistW = Math.Max(8, favRemain - favTitleW);

            var favItems = new List<string>(_favoriteSongs.Count);
            foreach (var item in _favoriteSongs)
            {
                string mLabel = $"{item.Month,2}月. ";
                string titleStr = SongListView.TruncateAndPadWide(item.Song.Title, favTitleW);
                string artistStr = SongListView.TruncateAndPadWide(item.Song.Artist, favArtistW);
                favItems.Add($"{mLabel}{titleStr}  {artistStr}");
            }
            if (favItems.Count == 0)
            {
                favItems.Add("当前年份暂无每月最爱歌曲记录");
            }
            _favoritesListView.SetSource(new ObservableCollection<string>(favItems));
            if (favItems.Count > 0 && (_favoritesListView.SelectedItem is null or < 0))
            {
                _favoritesListView.SelectedItem = 0;
            }
        }
        else
        {
            _favoritesListView.Visible = false;
            _singersLabel.Visible = true;

            if (report.Period == ListeningReportPeriod.Month && report.HighLights.Count > 0)
            {
                _singersFrame.Title = "本月高光时刻";
                _singersFrame.Height = report.HighLights.Count >= 4 ? 6 : 5;
                _singersLabel.Text = BuildHighLightsBar(report.HighLights);
            }
            else
            {
                _singersFrame.Title = "常听歌手分布";
                _singersFrame.Height = 5;
                _singersLabel.Text = BuildSingersBar(report.TopSingers);
            }
        }

        // 单曲收听排行榜
        _rankSongs.Clear();
        _rankSongs.AddRange(report.RankSongs);

        bool isYear = report.Period == ListeningReportPeriod.Year;
        int countColW = isYear ? 0 : 6;
        int indexColW = 4;
        int remain = Math.Max(24, viewWidth - indexColW - countColW - (isYear ? 4 : 6));
        int titleW = Math.Max(14, (int)Math.Round(remain * 0.58));
        int artistW = Math.Max(10, remain - titleW);

        var listItems = new List<string>(_rankSongs.Count);
        for (int i = 0; i < _rankSongs.Count; i++)
        {
            var item = _rankSongs[i];
            var s = item.Song;
            string idxStr = $"{i + 1,2}. ";
            string titleStr = SongListView.TruncateAndPadWide(s.Title, titleW);
            string artistStr = SongListView.TruncateAndPadWide(s.Artist, artistW);
            if (isYear)
            {
                listItems.Add($"{idxStr}{titleStr}  {artistStr}");
            }
            else
            {
                string countStr = $"{item.ListenCnt}次";
                listItems.Add($"{idxStr}{titleStr}  {artistStr}  {countStr}");
            }
        }

        if (listItems.Count == 0)
        {
            listItems.Add("当前周期暂无单曲收听排行记录");
        }

        _rankingListView.SetSource(new ObservableCollection<string>(listItems));
        if (listItems.Count > 0 && (_rankingListView.SelectedItem is null or < 0))
        {
            _rankingListView.SelectedItem = 0;
        }
        string periodRankTitle = report.Period switch
        {
            ListeningReportPeriod.Day => "本日单曲收听排行",
            ListeningReportPeriod.Week => "本周单曲收听排行",
            ListeningReportPeriod.Month => "本月单曲收听排行",
            ListeningReportPeriod.Year => "本年单曲收听排行",
            _ => "单曲收听排行"
        };
        _rankingFrame.Title = $"{periodRankTitle} (共 {_rankSongs.Count} 首)";
    }

    private static string FormatTrendText(ListeningReportPeriod period, int percent, int upOrDown)
    {
        if (period == ListeningReportPeriod.Year) return "";

        string sign = upOrDown >= 0 ? "+" : "-";
        string pctStr = percent > 0 ? $"{sign}{percent}%" : "0%";

        return period switch
        {
            ListeningReportPeriod.Week => $"相比上周:\n{pctStr}",
            ListeningReportPeriod.Month => $"相比上月:\n{pctStr}",
            _ => ""
        };
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

    private static string BuildAsciiChart(ListeningReportPeriod period, string dateKey, IReadOnlyList<ListeningChartPoint> points)
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
            var topBarLine = new StringBuilder();
            var botBarLine = new StringBuilder();
            for (int i = 0; i < 24; i++)
            {
                long v = i < points.Count ? points[i].Value : 0;
                int totalIdx = maxVal > 0 ? (int)Math.Round((double)v * 16 / maxVal) : 0;
                totalIdx = Math.Clamp(totalIdx, 0, 16);
                if (totalIdx > 8)
                {
                    topBarLine.Append(bars[totalIdx - 8]);
                    botBarLine.Append('█');
                }
                else if (totalIdx > 0)
                {
                    topBarLine.Append(' ');
                    botBarLine.Append(bars[totalIdx]);
                }
                else
                {
                    topBarLine.Append(' ');
                    botBarLine.Append(' ');
                }
            }
            sb.AppendLine(topBarLine.ToString());
            sb.AppendLine(botBarLine.ToString());
            sb.AppendLine("00    06    12    18   23");
        }
        else if (period == ListeningReportPeriod.Week)
        {
            sb.AppendLine("本周听歌走势分布:");
            var barLine = new StringBuilder();
            var weekDayLine = new StringBuilder();
            var dateNumLine = new StringBuilder();

            string[] weekDays = ["日", "一", "二", "三", "四", "五", "六"];

            DateTime baseDate = DateTime.TryParseExact(dateKey, "yyyyMMdd", null, System.Globalization.DateTimeStyles.None, out var parsedDt)
                ? parsedDt
                : DateTime.Today;

            for (int i = 0; i < 7; i++)
            {
                long v = i < points.Count ? points[i].Value : 0;
                int barIdx = maxVal > 0 ? (int)(v * 8 / maxVal) : 0;
                barIdx = Math.Clamp(barIdx, 0, 8);
                barLine.Append($"  {bars[barIdx]} ");

                weekDayLine.Append($" {weekDays[i]} ");

                DateTime dayDt = baseDate.AddDays(i - (int)baseDate.DayOfWeek);
                dateNumLine.Append($" {dayDt.Day,2} ");
            }

            sb.AppendLine(barLine.ToString());
            sb.AppendLine(weekDayLine.ToString());
            sb.AppendLine(dateNumLine.ToString());
        }
        else if (period == ListeningReportPeriod.Month)
        {
            sb.AppendLine("近6个月听歌首数走势:");
            var barLine = new StringBuilder();
            var monthLine = new StringBuilder();
            var valLine = new StringBuilder();

            const int cellW = 5;
            foreach (var p in points)
            {
                int barIdx = maxVal > 0 ? (int)(p.Value * 8 / maxVal) : 0;
                if (p.Value > 0 && barIdx == 0) barIdx = 1;
                barIdx = Math.Clamp(barIdx, 0, 8);

                barLine.Append(PadToColumns($"  {bars[barIdx]}", cellW));
                monthLine.Append(PadToColumns($" {p.Text}", cellW));
                valLine.Append(PadToColumns($"{p.Value,4}", cellW));
            }

            sb.AppendLine(barLine.ToString().TrimEnd());
            sb.AppendLine(monthLine.ToString().TrimEnd());
            sb.AppendLine(valLine.ToString().TrimEnd());
        }
        else if (period == ListeningReportPeriod.Year)
        {
            sb.AppendLine("近5年听歌走势:");
            var barLine = new StringBuilder();
            var yearLine = new StringBuilder();

            const int cellW = 6;
            foreach (var p in points)
            {
                int barIdx = maxVal > 0 ? (int)(p.Value * 8 / maxVal) : 0;
                if (p.Value > 0 && barIdx == 0) barIdx = 1;
                barIdx = Math.Clamp(barIdx, 0, 8);

                barLine.Append(PadToColumns($"  {bars[barIdx]}", cellW));
                yearLine.Append(PadToColumns($" {p.Text}", cellW));
            }

            sb.AppendLine(barLine.ToString().TrimEnd());
            sb.AppendLine(yearLine.ToString().TrimEnd());
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

    private static int GetDisplayColumns(string s)
    {
        int cols = 0;
        foreach (var r in s.EnumerateRunes())
        {
            cols += r.GetColumns();
        }
        return cols;
    }

    private static string PadToColumns(string s, int targetCols, bool rightAlign = false)
    {
        int curCols = GetDisplayColumns(s);
        if (curCols >= targetCols) return s;
        int diff = targetCols - curCols;
        return rightAlign ? new string(' ', diff) + s : s + new string(' ', diff);
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
            string padding = new(' ', Math.Max(0, maxNameCols - nameCols));

            string durationStr = min > 0 ? $"{min,4} 分钟" : $"{s.ListenTimeSec,4} 秒";
            sb.AppendLine($"#{i + 1} {singerName}{padding}  {durationStr}");
        }

        return sb.ToString().TrimEnd();
    }

    private static string BuildHighLightsBar(IReadOnlyList<ListeningHighlight> highlights)
    {
        if (highlights.Count == 0) return "当前周期暂无高光时刻记录";

        var sb = new StringBuilder();
        int maxItems = Math.Min(4, highlights.Count);
        for (int i = 0; i < maxItems; i++)
        {
            var h = highlights[i];
            string detail = !string.IsNullOrWhiteSpace(h.Tips) ? $" ({h.Tips})" : "";
            sb.AppendLine($"#{i + 1} {h.Title}: {h.Subtitle}{detail}");
        }
        return sb.ToString().TrimEnd();
    }

    private void SwitchPeriodRelative(int delta)
    {
        int p = (int)_currentPeriod + delta;
        if (p < 1) p = 4;
        if (p > 4) p = 1;
        _ = LoadFootprintAsync((ListeningReportPeriod)p, null, forceRefresh: false);
    }

    private async Task NavigateDateRelativeAsync(int delta)
    {
        long now = Environment.TickCount64;
        if (now - _lastNavTick < 250) return;
        _lastNavTick = now;

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

    /// <summary>
    /// 处理听歌足迹页面专属快捷键。
    /// </summary>
    /// <param name="k">按键事件对象。</param>
    /// <returns>若成功命中并处理足迹快捷键则返回 true，否则返回 false。</returns>
    public bool HandleFootprintShortcut(Key k)
    {
        char c = char.ToUpperInvariant((char)k.AsRune.Value);
        if (c == '1' || k == Key.D1)
        {
            _ = SwitchPeriodFromKeyAsync(ListeningReportPeriod.Day);
            return true;
        }
        if (c == '2' || k == Key.D2)
        {
            _ = SwitchPeriodFromKeyAsync(ListeningReportPeriod.Week);
            return true;
        }
        if (c == '3' || k == Key.D3)
        {
            _ = SwitchPeriodFromKeyAsync(ListeningReportPeriod.Month);
            return true;
        }
        if (c == '4' || k == Key.D4)
        {
            _ = SwitchPeriodFromKeyAsync(ListeningReportPeriod.Year);
            return true;
        }

        bool isPrev = k.AsRune.Value == '<' || k.AsRune.Value == ',' || k.AsRune.Value == '[';
        bool isNext = k.AsRune.Value == '>' || k.AsRune.Value == '.' || k.AsRune.Value == ']';

        if (isPrev)
        {
            _ = NavigateDateFromKeyAsync(-1);
            return true;
        }
        if (isNext)
        {
            _ = NavigateDateFromKeyAsync(1);
            return true;
        }
        return false;
    }

    /// <summary>
    /// 响应快捷键切换统计周期。
    /// </summary>
    /// <param name="period">目标统计周期维度。</param>
    public async Task SwitchPeriodFromKeyAsync(ListeningReportPeriod period)
    {
        await LoadFootprintAsync(period, null, forceRefresh: false);
    }

    /// <summary>
    /// 响应快捷键切换日期。
    /// </summary>
    /// <param name="delta">翻页相对偏移（-1 为历史，1 为向前）。</param>
    public async Task NavigateDateFromKeyAsync(int delta)
    {
        await NavigateDateRelativeAsync(delta);
    }
}
