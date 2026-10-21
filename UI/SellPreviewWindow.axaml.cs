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
using DsoHelper.AutoSell;

namespace DsoHelper.UI;

/// <summary>
/// Shows a screenshot of the game with the selling grid on top. Every cell can
/// be switched off with a click, so the macro never sells something the user
/// wants to keep (for example a valuable item that happens to be in the grid).
/// </summary>
public partial class SellPreviewWindow : Window
{
    internal sealed record Cell(
        int Index,
        double Left,
        double Top,
        double Width,
        double Height,
        ItemRarity Rarity);

    private static readonly IBrush SellFill = new SolidColorBrush(Color.Parse("#33C93B3B"));
    private static readonly IBrush SkipFill = new SolidColorBrush(Color.Parse("#40000000"));
    private static readonly IBrush SkipStroke = new SolidColorBrush(Color.Parse("#8C93A2"));
    private static readonly IBrush BlockedFill = new SolidColorBrush(Color.Parse("#22000000"));
    private static readonly IBrush ErrorBrush = new SolidColorBrush(Color.Parse("#C93B3B"));
    private static readonly IBrush PrimaryBrush = new SolidColorBrush(Color.Parse("#E6E8EC"));

    private static readonly IBrush RarityBasicBrush = new SolidColorBrush(Color.Parse("#B0B3B8"));
    private static readonly IBrush RarityImprovedBrush = new SolidColorBrush(Color.Parse("#3B82F6"));
    private static readonly IBrush RarityExtraordinaryBrush = new SolidColorBrush(Color.Parse("#A855F7"));
    private static readonly IBrush RarityLegendaryBrush = new SolidColorBrush(Color.Parse("#F97316"));
    private static readonly IBrush RarityUniqueBrush = new SolidColorBrush(Color.Parse("#EAB308"));
    private static readonly IBrush RarityUnknownBrush = new SolidColorBrush(Color.Parse("#525866"));

    private readonly Dictionary<int, Rectangle> _shapes = new();
    private readonly Dictionary<int, Cell> _cells = new();
    private readonly HashSet<int> _skip = new();
    private readonly HashSet<ItemRarity> _allowed = new();
    private bool _useRarity;

    public SellPreviewWindow()
    {
        InitializeComponent();
        KeyDown += OnKeyDown;
    }

    /// <summary>Raised when the user confirms the grid; carries the excluded cell indices.</summary>
    public event EventHandler<IReadOnlyList<int>>? Saved;

    internal void Initialize(
        WriteableBitmap image,
        int width,
        int height,
        IReadOnlyList<Cell> cells,
        IEnumerable<int> skip,
        bool useRarity,
        IEnumerable<ItemRarity> allowedRarities)
    {
        _useRarity = useRarity;
        foreach (var rarity in allowedRarities)
        {
            _allowed.Add(rarity);
        }

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
            _cells[cell.Index] = cell;

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

    private bool IsAllowedByRarity(ItemRarity rarity) => !_useRarity || _allowed.Contains(rarity);

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

        if (_cells.TryGetValue(index, out var cell) && !IsAllowedByRarity(cell.Rarity))
        {
            // The rarity filter decides this cell; change the checkboxes instead.
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
        if (!_shapes.TryGetValue(index, out var shape) || !_cells.TryGetValue(index, out var cell))
        {
            return;
        }

        var manual = _skip.Contains(index);
        var blocked = !manual && !IsAllowedByRarity(cell.Rarity);

        if (manual)
        {
            shape.Fill = SkipFill;
            shape.Stroke = SkipStroke;
            shape.StrokeThickness = 1.5;
            shape.StrokeDashArray = new AvaloniaList<double> { 4, 3 };
        }
        else if (blocked)
        {
            shape.Fill = BlockedFill;
            shape.Stroke = RarityBrush(cell.Rarity);
            shape.StrokeThickness = 1.5;
            shape.StrokeDashArray = new AvaloniaList<double> { 3, 3 };
        }
        else
        {
            shape.Fill = SellFill;
            shape.Stroke = RarityBrush(cell.Rarity);
            shape.StrokeThickness = 2;
            shape.StrokeDashArray = null;
        }

        var state = manual
            ? "исключено вручную"
            : blocked
                ? "пропущено по редкости"
                : "будет продано";

        ToolTip.SetTip(shape, $"{RarityCatalog.DisplayName(cell.Rarity)} — {state}");
    }

    private static IBrush RarityBrush(ItemRarity rarity) => rarity switch
    {
        ItemRarity.Basic => RarityBasicBrush,
        ItemRarity.Improved => RarityImprovedBrush,
        ItemRarity.Extraordinary => RarityExtraordinaryBrush,
        ItemRarity.Legendary => RarityLegendaryBrush,
        ItemRarity.Unique => RarityUniqueBrush,
        _ => RarityUnknownBrush
    };

    private void RefreshSummary()
    {
        var total = _cells.Count;
        var manual = _skip.Count;
        var blocked = _useRarity
            ? _cells.Values.Count(cell => !_skip.Contains(cell.Index) && !IsAllowedByRarity(cell.Rarity))
            : 0;
        var unknown = _cells.Values.Count(cell => cell.Rarity == ItemRarity.Unknown);
        var sell = total - manual - blocked;

        if (sell == 0)
        {
            SummaryText.Text = "Продавать нечего: все ячейки исключены.";
            SummaryText.Foreground = ErrorBrush;
        }
        else
        {
            var details = new List<string>();
            if (blocked > 0) details.Add($"по редкости: {blocked}");
            if (manual > 0) details.Add($"вручную: {manual}");
            if (unknown > 0) details.Add($"не распознано: {unknown}");

            SummaryText.Text = $"Будет продано: {sell} из {total} ячеек" +
                               (details.Count > 0 ? $" ({string.Join(", ", details)})" : string.Empty) + ".";
            SummaryText.Foreground = PrimaryBrush;
        }

        SaveButton.IsEnabled = sell > 0;

        var counts = _cells.Values
            .GroupBy(cell => cell.Rarity)
            .ToDictionary(group => group.Key, group => group.Count());

        var found = new List<string>();
        AddCount(ItemRarity.Basic, "обычные");
        AddCount(ItemRarity.Improved, "улучшенные");
        AddCount(ItemRarity.Extraordinary, "необычные");
        AddCount(ItemRarity.Legendary, "легендарные");
        AddCount(ItemRarity.Unique, "уникальные");
        AddCount(ItemRarity.Unknown, "не распознано");

        RarityBreakdownText.Text = found.Count > 0
            ? "Найдено — " + string.Join(", ", found) + "."
            : string.Empty;

        void AddCount(ItemRarity rarity, string label)
        {
            if (counts.TryGetValue(rarity, out var count) && count > 0)
            {
                found.Add($"{label}: {count}");
            }
        }
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
