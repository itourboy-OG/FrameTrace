using System.Collections.Immutable;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Frameglass;

public sealed partial class OverlayCanvas
{
    private readonly HashSet<string> selected = [];
    private readonly Rectangle marquee = new() { Stroke = Brushes.Aquamarine, Fill = new SolidColorBrush(Color.FromArgb(35, 131, 239, 205)), StrokeThickness = 1, IsHitTestVisible = false, Visibility = Visibility.Collapsed };
    private ImmutableDictionary<string, Point> dragPositions = ImmutableDictionary<string, Point>.Empty;
    private Point start;
    private Rect dragBounds;
    private bool editing, selecting;
    public bool ShowGrid { get; set; } = true;
    public bool SnapToGrid { get; set; } = true;
    internal ImmutableArray<string> Selection => selected.ToImmutableArray();

    public void EnableEditing()
    {
        editing = true; Focusable = true;
        Background = new SolidColorBrush(Color.FromRgb(23, 32, 43));
        Children.Add(marquee); SetZIndex(marquee, 1000);
        foreach (Border panel in panels.Values) AttachPanelEditor(panel);
        MouseLeftButtonDown += (_, e) =>
        {
            Focus(); selected.Clear(); UpdateSelectionOutlines(); selecting = true;
            start = e.GetPosition(this); marquee.Visibility = Visibility.Visible;
            SetLeft(marquee, start.X); SetTop(marquee, start.Y); marquee.Width = 0; marquee.Height = 0;
            CaptureMouse(); e.Handled = true;
        };
        MouseMove += (_, e) =>
        {
            if (e.LeftButton != MouseButtonState.Pressed) return;
            Point point = e.GetPosition(this);
            if (selecting)
            {
                Rect area = new(start, point);
                SetLeft(marquee, area.X); SetTop(marquee, area.Y); marquee.Width = area.Width; marquee.Height = area.Height;
                SelectRegion(area); e.Handled = true;
            }
            else if (dragging is not null)
            {
                DragSelection(point - start, Keyboard.Modifiers); e.Handled = true;
            }
        };
        MouseLeftButtonUp += (_, e) => { if (selecting || dragging is not null) { EndGesture(); ReleaseMouseCapture(); e.Handled = true; } };
        LostMouseCapture += (_, _) => EndGesture();
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.A && (Keyboard.Modifiers & ModifierKeys.Control) != 0) { SelectAll(); e.Handled = true; }
            if (e.Key == Key.Escape) { EndGesture(); ReleaseMouseCapture(); selected.Clear(); UpdateSelectionOutlines(); e.Handled = true; }
        };
    }

    private void AttachPanelEditor(Border panel)
    {
            panel.Cursor = Cursors.SizeAll;
            panel.MouseLeftButtonDown += (_, e) =>
            {
                Focus();
                string kind = (string)panel.Tag;
                if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0)
                {
                    if (!selected.Add(kind)) selected.Remove(kind);
                }
                else if (!selected.Contains(kind)) { selected.Clear(); selected.Add(kind); }
                UpdateSelectionOutlines(); SectionSelected?.Invoke(kind);
                if (selected.Contains(kind))
                {
                    dragging = panel; start = e.GetPosition(this);
                    BeginSelectionMove(); CaptureMouse();
                }
                e.Handled = true;
            };
    }

    private void EndGesture() { dragging = null; selecting = false; marquee.Visibility = Visibility.Collapsed; }
    public void SelectAll()
    {
        selected.Clear(); selected.UnionWith(panels.Where(pair => pair.Value.Visibility == Visibility.Visible).Select(pair => pair.Key));
        UpdateSelectionOutlines();
    }
    internal void SelectRegion(Rect area)
    {
        selected.Clear();
        foreach ((string kind, Border panel) in panels)
            if (panel.Visibility == Visibility.Visible && area.IntersectsWith(new Rect(GetLeft(panel), GetTop(panel), panel.DesiredSize.Width, panel.DesiredSize.Height))) selected.Add(kind);
        UpdateSelectionOutlines();
    }
    private Rect SelectionBounds()
    {
        Rect bounds = Rect.Empty;
        foreach (string kind in selected) bounds.Union(new Rect(GetLeft(panels[kind]), GetTop(panels[kind]), panels[kind].DesiredSize.Width, panels[kind].DesiredSize.Height));
        return bounds;
    }
    internal void BeginSelectionMove()
    {
        dragPositions = selected.ToImmutableDictionary(item => item, item => new Point(GetLeft(panels[item]), GetTop(panels[item])));
        dragBounds = SelectionBounds();
    }
    internal void DragSelection(Vector delta, ModifierKeys modifiers)
    {
        if (dragBounds.IsEmpty) return;
        if (SnapToGrid && (modifiers & ModifierKeys.Control) == 0)
            delta = new Vector(Math.Round((dragBounds.X + delta.X) / 16) * 16 - dragBounds.X, Math.Round((dragBounds.Y + delta.Y) / 16) * 16 - dragBounds.Y);
        MoveSelection(delta);
    }
    internal void MoveSelection(Vector delta)
    {
        if (dragBounds.IsEmpty) return;
        double x = Math.Clamp(delta.X, -dragBounds.Left, Math.Max(-dragBounds.Left, Width - dragBounds.Right));
        double y = Math.Clamp(delta.Y, -dragBounds.Top, Math.Max(-dragBounds.Top, Height - dragBounds.Bottom));
        preferences = preferences with { Sections = preferences.Sections.Select(section => dragPositions.TryGetValue(section.Key, out Point point)
            ? OverlayData.Move(section, point + new Vector(x, y), new Size(Width, Height), panels[section.Key].DesiredSize) : section).ToImmutableArray() };
        PositionPanels(); SectionsMoved?.Invoke(preferences.Sections);
    }
    private void UpdateSelectionOutlines()
    {
        selected.RemoveWhere(kind => panels[kind].Visibility != Visibility.Visible);
        foreach ((string kind, Border panel) in panels) panel.BorderBrush = editing && selected.Contains(kind) ? Brushes.Aquamarine : Brushes.Transparent;
    }
    protected override void OnRender(DrawingContext drawing)
    {
        base.OnRender(drawing);
        if (!editing || !ShowGrid) return;
        Pen pen = new(new SolidColorBrush(Color.FromArgb(35, 140, 160, 185)), 0.5);
        for (double x = 0; x < Width; x += 16) drawing.DrawLine(pen, new Point(x, 0), new Point(x, Height));
        for (double y = 0; y < Height; y += 16) drawing.DrawLine(pen, new Point(0, y), new Point(Width, y));
    }
}
