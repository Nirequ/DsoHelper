using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using DsoHelper.Infrastructure;

namespace DsoHelper.UI;

/// <summary>
/// A small topmost square that the user drags over an inventory cell.
/// After the mouse is released the position is confirmed in a few seconds.
/// </summary>
public partial class SlotPickerWindow : Window
{
    private const int ConfirmSeconds = 3;
    private const double DefaultMarkerSize = 64;

    private readonly DispatcherTimer _countdown;

    private bool _dragging;
    private PixelPoint _dragOffset;
    private int _secondsLeft;

    public event EventHandler<PixelPoint>? SlotPicked;

    public SlotPickerWindow()
    {
        InitializeComponent();

        _countdown = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _countdown.Tick += OnCountdownTick;

        PointerPressed += OnPointerPressed;
        PointerMoved += OnPointerMoved;
        PointerReleased += OnPointerReleased;
        KeyDown += OnKeyDown;
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        Activate();
    }

    protected override void OnClosed(EventArgs e)
    {
        _countdown.Stop();
        base.OnClosed(e);
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        _countdown.Stop();
        HintText.Text = "+";
        _dragging = true;
        _dragOffset = GetDragOffset();
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    private PixelPoint GetDragOffset()
    {
        var center = new PixelPoint(
            (int)Math.Round(GetMarkerWidth() * RenderScaling / 2),
            (int)Math.Round(GetMarkerHeight() * RenderScaling / 2));

        return NativeMethods.GetCursorPos(out var cursor)
            ? new PixelPoint(cursor.X - Position.X, cursor.Y - Position.Y)
            : center;
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_dragging || !NativeMethods.GetCursorPos(out var cursor))
        {
            return;
        }

        Position = new PixelPoint(cursor.X - _dragOffset.X, cursor.Y - _dragOffset.Y);
        e.Handled = true;
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_dragging)
        {
            return;
        }

        _dragging = false;
        e.Pointer.Capture(null);
        StartCountdown();
        e.Handled = true;
    }

    private void StartCountdown()
    {
        _secondsLeft = ConfirmSeconds;
        HintText.Text = _secondsLeft.ToString();
        _countdown.Stop();
        _countdown.Start();
    }

    private void OnCountdownTick(object? sender, EventArgs e)
    {
        _secondsLeft--;
        if (_secondsLeft > 0)
        {
            HintText.Text = _secondsLeft.ToString();
            return;
        }

        _countdown.Stop();
        Confirm();
    }

    private void Confirm()
    {
        var center = new PixelPoint(
            Position.X + (int)Math.Round(GetMarkerWidth() * RenderScaling / 2),
            Position.Y + (int)Math.Round(GetMarkerHeight() * RenderScaling / 2));

        SlotPicked?.Invoke(this, center);
        Close();
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
            _countdown.Stop();
            Confirm();
            e.Handled = true;
        }
    }

    private double GetMarkerWidth() => Bounds.Width > 0 ? Bounds.Width : DefaultMarkerSize;

    private double GetMarkerHeight() => Bounds.Height > 0 ? Bounds.Height : DefaultMarkerSize;
}
