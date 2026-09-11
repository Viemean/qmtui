using System.Collections.ObjectModel;
using Terminal.Gui.App;
using Terminal.Gui.Drawing;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using QmTui.Api;
using QmTui.Models;

namespace QmTui.UI;

public sealed class QualityDialog : Dialog
{
    private readonly ListView _qualityListView;
    private readonly Label _hintLabel;
    private readonly Button _confirmBtn;
    private readonly Action<AudioQualityTier, QualityOption?> _onQualitySelected;
    private readonly List<QualityOption> _options = [];
    private AudioQualityTier _currentTier;

    private static Scheme TransparentDialogScheme { get; } = new Scheme
    {
        Normal    = new Terminal.Gui.Drawing.Attribute(MikuTheme.MikuTextWhite, Terminal.Gui.Drawing.Color.None),
        Focus     = new Terminal.Gui.Drawing.Attribute(Terminal.Gui.Drawing.Color.White, MikuTheme.QqGreenDark),
        HotNormal = new Terminal.Gui.Drawing.Attribute(MikuTheme.MikuPinkAccent, Terminal.Gui.Drawing.Color.None),
        HotFocus  = new Terminal.Gui.Drawing.Attribute(Terminal.Gui.Drawing.Color.White, MikuTheme.MikuPinkAccent),
        Disabled  = new Terminal.Gui.Drawing.Attribute(MikuTheme.MikuTextMuted, Terminal.Gui.Drawing.Color.None),
        Highlight = new Terminal.Gui.Drawing.Attribute(MikuTheme.QqGreenPrimary, Terminal.Gui.Drawing.Color.None),
        Active    = new Terminal.Gui.Drawing.Attribute(MikuTheme.QqGreenLight, MikuTheme.QqGreenDark),
        ReadOnly  = new Terminal.Gui.Drawing.Attribute(MikuTheme.MikuTextMuted, Terminal.Gui.Drawing.Color.None),
        Editable  = new Terminal.Gui.Drawing.Attribute(Terminal.Gui.Drawing.Color.White, Terminal.Gui.Drawing.Color.None)
    };

    public QualityDialog(Song? activeSong, AudioQualityTier currentTier, Action<AudioQualityTier, QualityOption?> onQualitySelected, string customTitle = "音质切换")
    {
        _currentTier = currentTier;
        _onQualitySelected = onQualitySelected;

        Title = customTitle;
        Width = 64;
        Height = 16;
        X = Pos.Center();
        Y = Pos.Center();
        CanFocus = true;
        SetScheme(TransparentDialogScheme);

        var tipLabel = new Label
        {
            Text = activeSong != null
                ? $"曲目: {activeSong.Title} - {activeSong.Artist}"
                : "全局默认音质设置 (无正在播放曲目)",
            X = 2,
            Y = 0
        };
        tipLabel.SetScheme(TransparentDialogScheme);
        Add(tipLabel);

        _qualityListView = new ListView
        {
            X = 2,
            Y = 2,
            Width = Dim.Fill(2),
            Height = Dim.Fill(2),
            CanFocus = true,
            TabStop = TabBehavior.TabStop
        };
        _qualityListView.SetScheme(TransparentDialogScheme);
        _qualityListView.KeyBindings.Remove(Key.Space);
        _qualityListView.Accepted += (s, e) => ApplySelection();
        _qualityListView.ValueChanged += (s, e) => UpdateHintAndButtons();
        _qualityListView.KeyDown += (s, k) =>
        {
            if (k == Key.Esc || k == Key.Q)
            {
                k.Handled = true;
                Application.RequestStop(this);
                return;
            }

            if (k == Key.Enter)
            {
                var idx = _qualityListView.SelectedItem ?? -1;
                if (idx >= 0 && idx < _options.Count && !_options[idx].Available)
                {
                    k.Handled = true;
                    if (_hintLabel != null)
                    {
                        _hintLabel.Text = $"[无音源] 当前曲目不支持 {_options[idx].Name}，无法选择";
                    }
                    return;
                }
            }
        };
        Add(_qualityListView);

        _hintLabel = new Label
        {
            Text = "[Enter/双击] 选择音质   [Esc] 关闭",
            X = 2,
            Y = Pos.AnchorEnd(1),
            Width = Dim.Fill(22),
            Height = 1
        };
        _hintLabel.SetScheme(new Scheme
        {
            Normal = new Terminal.Gui.Drawing.Attribute(MikuTheme.QqGreenLight, Terminal.Gui.Drawing.Color.None)
        });
        Add(_hintLabel);

        _confirmBtn = new Button
        {
            Text = "确定",
            X = Pos.AnchorEnd(19),
            Y = Pos.AnchorEnd(1),
            ShadowStyle = ShadowStyles.None,
            CanFocus = true,
            TabStop = TabBehavior.TabStop
        };
        _confirmBtn.KeyBindings.Remove(Key.Space);
        _confirmBtn.KeyBindings.Remove(Key.Esc);
        _confirmBtn.Accepting += (s, e) => ApplySelection();
        Add(_confirmBtn);

        var cancelBtn = new Button
        {
            Text = "取消",
            X = Pos.AnchorEnd(9),
            Y = Pos.AnchorEnd(1),
            ShadowStyle = ShadowStyles.None,
            CanFocus = true,
            TabStop = TabBehavior.TabStop
        };
        cancelBtn.KeyBindings.Remove(Key.Space);
        cancelBtn.KeyBindings.Remove(Key.Esc);
        cancelBtn.Accepting += (s, e) => Application.RequestStop(this);
        Add(cancelBtn);

        _confirmBtn.KeyDown += (s, k) =>
        {
            if (k == Key.CursorRight) { cancelBtn.SetFocus(); k.Handled = true; }
            else if (k == Key.CursorUp) { _qualityListView.SetFocus(); k.Handled = true; }
        };
        cancelBtn.KeyDown += (s, k) =>
        {
            if (k == Key.CursorLeft) { _confirmBtn.SetFocus(); k.Handled = true; }
            else if (k == Key.CursorUp) { _qualityListView.SetFocus(); k.Handled = true; }
        };

        KeyDown += (s, k) =>
        {
            if (k == Key.Esc || k == Key.Q)
            {
                k.Handled = true;
                Application.RequestStop(this);
            }
        };

        // 初始化默认档位
        BuildDefaultOptions();
        RefreshDisplayList();

        _qualityListView.SetFocus();
        Application.Invoke(() => _qualityListView.SetFocus());

        // 异步探测真实音源状态
        if (activeSong != null)
        {
            Task.Run(async () =>
            {
                var probed = await MusicApi.ProbeSongQualitiesAsync(activeSong.Mid, activeSong.EffectiveMediaMid);
                Application.Invoke(() =>
                {
                    _options.Clear();
                    _options.AddRange(probed);
                    RefreshDisplayList();
                });
            });
        }

        MikuTheme.ApplyTo(this, TransparentDialogScheme);
    }

    private void BuildDefaultOptions()
    {
        _options.Clear();
        foreach (var tier in AudioQualityHelper.SelectionOrder)
        {
            _options.Add(new QualityOption(tier, AudioQualityHelper.GetBadge(tier), AudioQualityHelper.GetQualityName(tier), AudioQualityHelper.GetDefaultSpec(tier), "", true));
        }
    }

    private void RefreshDisplayList()
    {
        var displayList = new List<string>();
        int selectedIndex = -1;

        for (int i = 0; i < _options.Count; i++)
        {
            var opt = _options[i];
            var isCurrent = opt.Tier == _currentTier;
            if (isCurrent) selectedIndex = i;
            displayList.Add(opt.DisplayText(isCurrent));
        }

        // 若当前选中的项不可用，自动挑选最高可用音质作为默认选中项
        if (selectedIndex < 0 || (selectedIndex < _options.Count && !_options[selectedIndex].Available))
        {
            for (int i = 0; i < _options.Count; i++)
            {
                if (_options[i].Available)
                {
                    selectedIndex = i;
                    break;
                }
            }
        }

        _qualityListView.SetSource(new ObservableCollection<string>(displayList));
        if (selectedIndex >= 0 && selectedIndex < displayList.Count)
        {
            _qualityListView.SelectedItem = selectedIndex;
        }

        UpdateHintAndButtons();
    }

    private void UpdateHintAndButtons()
    {
        var idx = _qualityListView.SelectedItem ?? -1;
        if (idx >= 0 && idx < _options.Count)
        {
            var opt = _options[idx];
            if (!opt.Available)
            {
                _hintLabel.Text = $"[无音源] 当前曲目不支持 {opt.Name}，无法选择";
                _confirmBtn.Enabled = false;
                return;
            }
        }

        _hintLabel.Text = "[Enter/双击] 选择音质   [Esc] 关闭";
        _confirmBtn.Enabled = true;
    }

    private void ApplySelection()
    {
        var idx = _qualityListView.SelectedItem ?? -1;
        if (idx >= 0 && idx < _options.Count)
        {
            var opt = _options[idx];
            if (!opt.Available)
            {
                _hintLabel.Text = $"[无音源] 当前曲目不支持 {opt.Name}，无法选择！";
                return;
            }

            _currentTier = opt.Tier;
            _onQualitySelected?.Invoke(opt.Tier, opt);
            Application.RequestStop(this);
        }
    }
}
