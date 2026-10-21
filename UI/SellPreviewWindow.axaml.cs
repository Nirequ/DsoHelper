using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;

namespace DsoHelper.UI;

/// <summary>
/// Shows a screenshot of the game with the selling grid on top. Every cell can
/// be switched off with a click, so the macro never sells something the user
/// wants to keep (for example a valuable item that happens to be in the grid).
/// </summary>
public partial class SellPreviewWindow : Window
{
    public sealed record Cell(int Index, double Left, double Top, double Width, double Height);

    private static readonly IBrush SellFill = new SolidColorBrush(Color.Parse("#33C93B3B"));
    private static readonly IBrush SellStroke = new SolidColorBrush(Color.Parse("#C93B3B"));
    private static readonly IBrush SkipFill = new SolidColorBrush(Color.Parse("#40000000"));
    private static readonly IBrush SkipStroke = new SolidColorBrush(Color.Parse("#8C93A2"));
    private static readonly IBrush ErrorBrush = new SolidColorBrush(Color.Parse("#C93B3B"));
    private static readonly IBrush PrimaryBrush = new SolidColorBrush(Color.Parse("#E6E8EC"));

    private readonly Dictionary<int, Rectangle> _shapes = new();
    private readonly HashSet<int> _skip = new();

    public SellPreviewWindow()
    {
        InitializeComponent();
        KeyDown += OnKeyDown;
    }

    /// <summary>Raised when the user confirms the grid; carries the excluded cell indices.</summary>
    public event EventHandler<IReadOnlyList<int>>? Saved;

    public void Initialize(
        WriteableBitmap image,
        int width,
        int height,
        IReadOnlyList<Cell> cells,
        IEnumerable<int> skip)
    {
        Screenshot.Source = image;
        Screenshot.Width = width;
        Screenshot.Height = height;

        Surface.Width = width;
        Surface.Height = height;
        GridLayer.Width = width;
        GridLayer.Height = height;

        var known = new HashSet<int>(cells.Select(cell => cell.Index));
        foreach (var index in skip)
        {
            if (known.Contains(index))
            {
                _skip.Add(index);
            }
        }

        foreach (var cell in cells)
        {
            var rectangle = new Rectangle
            {
                Width = cell.Width,
                Height = cell.Height,
                StrokeThickness = 2,
                RadiusX = 2,
                RadiusY = 2,
                Cursor = new Cursor(StandardCursorType.Hand),
                Tag = cell.Index
            };

            rectangle.PointerPressed += OnCellPressed;
            Canvas.SetLeft(rectangle, cell.Left);
            Canvas.SetTop(rectangle, cell.Top);
            GridLayer.Children.Add(rectangle);
            _shapes[cell.Index] = rectangle;
        }

        foreach (var index in _shapes.Keys)
        {
            ApplyVisual(index);
        }

        RefreshSummary();
    }

    private void OnCellPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Rectangle { Tag: int index })
        {
            return;
        }

        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        if (!_skip.Remove(index))
        {
            _skip.Add(index);
        }

        ApplyVisual(index);
        RefreshSummary();
        e.Handled = true;
    }

    private void ApplyVisual(int index)
    {
        if (!_shapes.TryGetValue(index, out var shape))
        {
            return;
        }

        var skipped = _skip.Contains(index);
        shape.Fill = skipped ? SkipFill : SellFill;
        shape.Stroke = skipped ? SkipStroke : SellStroke;
        shape.StrokeDashArray = skipped ? new AvaloniaList<double> { 4, 3 } : null;
    }

    private void RefreshSummary()
    {
        var total = _shapes.Count;
        var sell = total - _skip.Count;
        var skipped = _skip.Count;

        if (sell == 0)
        {
            SummaryText.Text = "Все ячейки исключены — продавать нечего.";
            SummaryText.Foreground = ErrorBrush;
        }
        else
        {
            SummaryText.Text = skipped == 0
                ? $"Будет продано: {sell} из {total} ячеек."
                : $"Будет продано: {sell} из {total} ячеек, исключено: {skipped}.";
            SummaryText.Foreground = PrimaryBrush;
        }

        SaveButton.IsEnabled = sell > 0;
    }

    private void OnAllClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        _skip.Clear();
        foreach (var index in _shapes.Keys)
        {
            ApplyVisual(index);
        }

        RefreshSummary();
    }

    private void OnNoneClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        _skip.Clear();
        foreach (var index in _shapes.Keys)
        {
            _skip.Add(index);
            ApplyVisual(index);
        }

        RefreshSummary();
    }

    private void OnSaveClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        Saved?.Invoke(this, _skip.OrderBy(index => index).ToList());
        Close();
    }

    private void OnCancelClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Close();

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
    }

    private void OnHeaderPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        if (e.Source is Visual source &&
            source.FindAncestorOfType<Button>(includeSelf: true) is not null)
        {
            return;
        }

        BeginMoveDrag(e);
    }
}
