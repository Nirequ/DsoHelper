using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using DsoHelper.Infrastructure;

namespace DsoHelper.UI;

/// <summary>
/// A small topmost square that follows the mouse cursor. A left click records the
/// point under the cursor immediately; Esc (or right click) cancels.
/// </summary>
public partial class SlotPickerWindow : Window
{
    /// <summary>Маркер для ячейки инвентаря (слот плаща).</summary>
    public const double SlotMarkerSize = 64;

    /// <summary>Маркер для заголовков вкладок — они заметно меньше ячейки.</summary>
    public const double TabMarkerSize = 36;

    private readonly DispatcherTimer _follow;
    private readonly double _markerSize;

    public event EventHandler<PixelPoint>? SlotPicked;

    public SlotPickerWindow() : this(SlotMarkerSize)
    {
    }

    public SlotPickerWindow(double markerSize)
    {
        InitializeComponent();

        _markerSize = markerSize;
        Width = markerSize;
        Height = markerSize;
        if (HintText is { } hintText)
        {
            hintText.FontSize = Math.Max(14, markerSize / 3);
        }

        _follow = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(15) };
        _follow.Tick += (_, _) => FollowCursor();

        PointerPressed += OnPointerPressed;
        KeyDown += OnKeyDown;
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        FollowCursor();
        _follow.Start();
        Activate();
        Focus();
    }

    protected override void OnClosed(EventArgs e)
    {
        _follow.Stop();
        base.OnClosed(e);
    }

    private void FollowCursor()
    {
        if (!NativeMethods.GetCursorPos(out var cursor))
        {
            return;
        }

        var halfWidth = (int)Math.Round(_markerSize * RenderScaling / 2);
        var halfHeight = (int)Math.Round(_markerSize * RenderScaling / 2);
        var next = new PixelPoint(cursor.X - halfWidth, cursor.Y - halfHeight);

        if (Position != next)
        {
            Position = next;
        }
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var point = e.GetCurrentPoint(this);

        if (point.Properties.IsRightButtonPressed)
        {
            Close();
            e.Handled = true;
            return;
        }

        if (!point.Properties.IsLeftButtonPressed)
        {
            return;
        }

        if (!NativeMethods.GetCursorPos(out var cursor))
        {
            return;
        }

        _follow.Stop();
        SlotPicked?.Invoke(this, new PixelPoint(cursor.X, cursor.Y));
        Close();
        e.Handled = true;
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
            return;
        }

        if (e.Key is Key.Enter or Key.Space)
        {
            if (NativeMethods.GetCursorPos(out var cursor))
            {
                _follow.Stop();
                SlotPicked?.Invoke(this, new PixelPoint(cursor.X, cursor.Y));
            }

            Close();
            e.Handled = true;
        }
    }
}
