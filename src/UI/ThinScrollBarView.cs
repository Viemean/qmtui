using System.Text;
using Terminal.Gui.App;
using Terminal.Gui.Drawing;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace QmTui.UI;

/// <summary>
/// 自隐藏细滚动条。
/// 宽度为 1 单元格，超时自动隐藏，支持拖拽与分页点击跳转。
/// </summary>
public sealed class ThinScrollBarView : View
{
    private readonly Label _trackLabel;
    private int _totalItems;
    private int _visibleItems;
    private int _firstVisibleItem;
    private long _lastActivityTick;
    private bool _isActive;
    private bool _isDragging;
    private int _dragOffset;
    private object? _timeoutToken;

    public event Action<int>? ScrollPositionChanged;
    public bool IsDragging => _isDragging;

    public ThinScrollBarView()
    {
        Width = 1;
        Height = Dim.Fill();
        CanFocus = false;
        TabStop = TabBehavior.NoStop;

        _trackLabel = new Label
        {
            X = 0,
            Y = 0,
            Width = 1,
            Height = Dim.Fill(),
            CanFocus = false,
            TabStop = TabBehavior.NoStop,
            Text = ""
        };
        _trackLabel.SetScheme(new Scheme
        {
            Normal = new Terminal.Gui.Drawing.Attribute(MikuTheme.QqGreenPrimary, Color.None),
            Focus = new Terminal.Gui.Drawing.Attribute(MikuTheme.QqGreenPrimary, Color.None),
            HotNormal = new Terminal.Gui.Drawing.Attribute(MikuTheme.QqGreenPrimary, Color.None)
        });
        Add(_trackLabel);

        // 定时检查是否超过 3 秒无活动，隐藏滚动条
        _timeoutToken = Application.AddTimeout(TimeSpan.FromMilliseconds(500), () =>
        {
            if (_isActive && !_isDragging && Environment.TickCount64 - _lastActivityTick > 3000)
            {
                _isActive = false;
                _trackLabel.Text = "";
                SetNeedsDraw();
            }
            return true;
        });

        MouseEvent += (s, m) =>
        {
            if (HandleMouseEvent(m.Flags, m.Position?.Y))
            {
                m.Handled = true;
            }
        };
    }

    public bool AutoShowOnMetricsChange { get; set; } = true;

    public void UpdateMetrics(int totalItems, int visibleItems, int firstVisibleItem)
    {
        bool changed = _totalItems != totalItems || _visibleItems != visibleItems || _firstVisibleItem != firstVisibleItem;
        _totalItems = totalItems;
        _visibleItems = visibleItems;
        _firstVisibleItem = firstVisibleItem;

        if (changed)
        {
            if (AutoShowOnMetricsChange)
            {
                TriggerActivity();
            }
            else if (_isActive)
            {
                RenderScrollBar();
            }
        }
    }

    public void TriggerActivity()
    {
        _lastActivityTick = Environment.TickCount64;
        if (!_isActive)
        {
            _isActive = true;
        }
        RenderScrollBar();
    }

    private void RenderScrollBar()
    {
        if (!_isActive || _totalItems <= _visibleItems || _visibleItems <= 0)
        {
            _trackLabel.Text = "";
            SetNeedsDraw();
            return;
        }

        int trackHeight = Viewport.Height > 0 ? Viewport.Height : (Frame.Height > 0 ? Frame.Height : 20);
        int maxFirstItem = Math.Max(1, _totalItems - _visibleItems);
        int thumbHeight = Math.Max(1, (int)Math.Round((double)_visibleItems / _totalItems * trackHeight));
        int maxThumbTop = Math.Max(0, trackHeight - thumbHeight);
        int thumbTop = maxThumbTop > 0 ? Math.Clamp((int)Math.Round((double)_firstVisibleItem / maxFirstItem * maxThumbTop), 0, maxThumbTop) : 0;

        var sb = new StringBuilder();
        for (int y = 0; y < trackHeight; y++)
        {
            if (y >= thumbTop && y < thumbTop + thumbHeight)
            {
                sb.Append('█');
            }
            else
            {
                sb.Append('│');
            }
            if (y < trackHeight - 1)
            {
                sb.Append('\n');
            }
        }

        _trackLabel.Text = sb.ToString();
        SetNeedsDraw();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && _timeoutToken != null)
        {
            Application.RemoveTimeout(_timeoutToken);
            _timeoutToken = null;
        }
        base.Dispose(disposing);
    }

    internal bool HandleMouseEvent(MouseFlags flags, int? y)
    {
        TriggerActivity();

        if (_totalItems <= _visibleItems || _visibleItems <= 0)
        {
            _isDragging = false;
            return false;
        }

        int trackHeight = Viewport.Height > 0 ? Viewport.Height : (Frame.Height > 0 ? Frame.Height : 20);
        int maxFirstItem = Math.Max(1, _totalItems - _visibleItems);
        int thumbHeight = Math.Max(1, (int)Math.Round((double)_visibleItems / _totalItems * trackHeight));
        int maxThumbTop = Math.Max(0, trackHeight - thumbHeight);
        int thumbTop = maxThumbTop > 0 ? Math.Clamp((int)Math.Round((double)_firstVisibleItem / maxFirstItem * maxThumbTop), 0, maxThumbTop) : 0;

        bool isLeftDown = flags.HasFlag(MouseFlags.LeftButtonPressed);
        bool isLeftClick = flags.HasFlag(MouseFlags.LeftButtonClicked);
        bool isLeftReleased = flags.HasFlag(MouseFlags.LeftButtonReleased);

        if (isLeftReleased || (!isLeftDown && !isLeftClick))
        {
            _isDragging = false;
        }

        if (!y.HasValue) return false;

        int clickY = y.Value;

        if (_isDragging && isLeftDown)
        {
            if (maxThumbTop > 0)
            {
                int targetThumbTop = clickY - _dragOffset;
                double ratio = Math.Clamp((double)targetThumbTop / maxThumbTop, 0.0, 1.0);
                int target = Math.Clamp((int)Math.Round(ratio * maxFirstItem), 0, maxFirstItem);
                ScrollPositionChanged?.Invoke(target);
            }
            return true;
        }

        if (isLeftDown || isLeftClick)
        {
            if (clickY >= thumbTop && clickY < thumbTop + thumbHeight)
            {
                _isDragging = true;
                _dragOffset = clickY - thumbTop;
            }
            else if (clickY < thumbTop)
            {
                int target = Math.Max(0, _firstVisibleItem - _visibleItems);
                ScrollPositionChanged?.Invoke(target);
            }
            else
            {
                int target = Math.Min(maxFirstItem, _firstVisibleItem + _visibleItems);
                ScrollPositionChanged?.Invoke(target);
            }
            return true;
        }

        if (flags.HasFlag(MouseFlags.WheeledUp))
        {
            ScrollPositionChanged?.Invoke(Math.Max(0, _firstVisibleItem - 3));
            return true;
        }

        if (flags.HasFlag(MouseFlags.WheeledDown))
        {
            ScrollPositionChanged?.Invoke(Math.Min(maxFirstItem, _firstVisibleItem + 3));
            return true;
        }

        return false;
    }
}
