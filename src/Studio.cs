using System.Collections.Immutable;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Frameglass;

public partial class MainWindow
{
    private ImmutableArray<DisplayInfo> displays = [];
    private readonly Dictionary<string, CheckBox> metricChecks = new();
    private bool zoomUpdating, fitStudio = true;
    private double zoom = 1;
    private Point? panStart;
    private Point panOffsets;
    private SectionStyle Selected => draft.Sections.Single(s => s.Key == ((SectionStyle)SectionSelector.SelectedItem).Key);

    private void InitializeStudio()
    {
        displays = Desktop.Displays();
        DisplaySelector.ItemsSource = displays;
        Rect? current = Desktop.Foreground()?.Monitor;
        DisplaySelector.SelectedItem = displays.FirstOrDefault(d => d.Bounds == current) ?? displays.Single(d => d.Primary);
        PresetSelector.ItemsSource = LayoutPresets.Names; PresetSelector.SelectedIndex = 0;
#if PREVIEW_BUILD
        string stableLayoutsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Frameglass", "saved-layouts.json");
        CustomPresetSelector.ItemsSource = SavedLayouts.ReadPreview(SavedLayouts.FilePath, stableLayoutsPath);
#else
        CustomPresetSelector.ItemsSource = SavedLayouts.Read(SavedLayouts.FilePath);
#endif
        AddMetricSelector.ItemsSource = OverlayData.Build(readings, summary).SelectMany(section => section.Metrics.Where(metric => metric.Id != "upscaler").Select(metric => new MetricChoice(section.Kind, metric.Id, section.Kind + " · " + metric.Label))).ToArray();
        AddMetricSelector.SelectedIndex = 0;
    }
    private void RefreshDisplays()
    {
        string? selected = (DisplaySelector.SelectedItem as DisplayInfo)?.Name;
        displays = Desktop.Displays(); DisplaySelector.ItemsSource = displays;
        DisplaySelector.SelectedItem = displays.FirstOrDefault(d => d.Name == selected) ?? displays.Single(d => d.Primary);
    }
    private void FollowGameDisplay(Rect bounds)
    {
        if (FollowDisplay.IsChecked != true || DisplaySelector.SelectedItem is DisplayInfo current && current.Bounds == bounds) return;
        DisplayInfo? next = displays.FirstOrDefault(d => d.Bounds == bounds);
        if (next is null) { displays = Desktop.Displays(); DisplaySelector.ItemsSource = displays; next = displays.Single(d => d.Bounds == bounds); }
        DisplaySelector.SelectedItem = next;
    }
    internal static Rect SelectOverlayMonitor(Rect gameMonitor, bool followGameDisplay, Rect? selectedDisplay) =>
        !followGameDisplay && selectedDisplay is Rect manualDisplay ? manualDisplay : gameMonitor;
    private void SelectDisplay(object sender, SelectionChangedEventArgs e)
    {
        if (!ready || DisplaySelector.SelectedItem is not DisplayInfo display) return;
        preview.Width = display.CanvasSize.Width; preview.Height = display.CanvasSize.Height;
        CanvasDescription.Text = $"{display.Bounds.Width:0} × {display.Bounds.Height:0} · Windows scale {display.Scale:P0}. Ctrl + wheel to zoom; middle-drag to pan. Positions match this display.";
        FitPreview(); RefreshTestOverlay();
    }
    private void SetZoom(double value)
    {
        zoom = Math.Clamp(value, 0.1, 4);
        preview.LayoutTransform = new ScaleTransform(zoom, zoom);
        zoomUpdating = true; ZoomSlider.Value = zoom * 100; ZoomLabel.Text = $"{zoom:P0}"; zoomUpdating = false;
    }
    private void FitPreview()
    {
        fitStudio = true;
        if (PreviewViewport.ActualWidth < 10 || PreviewViewport.ActualHeight < 10) return;
        SetZoom(Math.Min((PreviewViewport.ActualWidth - 24) / preview.Width, (PreviewViewport.ActualHeight - 24) / preview.Height));
        PreviewViewport.ScrollToHorizontalOffset(0); PreviewViewport.ScrollToVerticalOffset(0);
    }
    private void FitStudio(object sender, RoutedEventArgs e) => FitPreview();
    private void ActualStudio(object sender, RoutedEventArgs e) { fitStudio = false; SetZoom(1); }
    private void ChangeZoom(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!ready || zoomUpdating) return;
        fitStudio = false; SetZoom(e.NewValue / 100);
    }
    private void ViewportResized(object sender, SizeChangedEventArgs e) { if (ready && fitStudio) FitPreview(); }
    private void StudioWheel(object sender, MouseWheelEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
        Point cursor = e.GetPosition(PreviewViewport); double old = zoom;
        double left = PreviewViewport.HorizontalOffset, top = PreviewViewport.VerticalOffset;
        fitStudio = false; SetZoom(zoom * (e.Delta > 0 ? 1.2 : 1 / 1.2));
        PreviewViewport.UpdateLayout();
        PreviewViewport.ScrollToHorizontalOffset((left + cursor.X) * zoom / old - cursor.X);
        PreviewViewport.ScrollToVerticalOffset((top + cursor.Y) * zoom / old - cursor.Y);
        e.Handled = true;
    }
    private void StartPan(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle) return;
        panStart = e.GetPosition(PreviewViewport); panOffsets = new Point(PreviewViewport.HorizontalOffset, PreviewViewport.VerticalOffset);
        PreviewViewport.CaptureMouse(); e.Handled = true;
    }
    private void PanStudio(object sender, MouseEventArgs e)
    {
        if (panStart is not Point start || e.MiddleButton != MouseButtonState.Pressed) return;
        Point now = e.GetPosition(PreviewViewport);
        PreviewViewport.ScrollToHorizontalOffset(panOffsets.X + start.X - now.X);
        PreviewViewport.ScrollToVerticalOffset(panOffsets.Y + start.Y - now.Y); e.Handled = true;
    }
    private void EndPan(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle) return;
        panStart = null; PreviewViewport.ReleaseMouseCapture(); e.Handled = true;
    }
    private void ApplyPreset(object sender, RoutedEventArgs e)
    {
        if (PresetSelector.SelectedItem is not string name) return;
        draft = LayoutPresets.Create(draft, name); preview.Apply(draft); RefreshTestOverlay();
        preview.UpdateData(OverlayData.Build(readings, summary));
        draft = preview.ArrangePreset(name); LoadControls();
        StudioStatus.Text = name + " is ready in preview. Save & apply when it looks right.";
    }
    private void LoadControls()
    {
        RefreshSectionSelector();
        loading = true;
        HotkeyInput.Text = Desktop.HotkeyLabel(draft.Shortcut); OverlayStartup.IsChecked = draft.OverlayEnabled;
        StartMinimizedInput.IsChecked = draft.StartMinimized;
        RunAtLoginInput.IsChecked = draft.RunAtLogin;
        ReduceMotionInput.IsChecked = draft.ReduceMotion;
        CheckUpdatesOnStartupInput.IsChecked = draft.CheckUpdatesOnStartup;
        SensorRefreshSlider.Value = draft.SensorRefreshMs;
        IgnoredApps.Text = string.Join(Environment.NewLine, draft.IgnoredApps);
        FontSelector.SelectedIndex = Array.IndexOf(new[] { "Consolas", "Segoe UI", "Arial", "Cascadia Mono" }, draft.Font);
        OverlaySize.Value = draft.OverlayScale * 100; OverlaySizeLabel.Text = $"{draft.OverlayScale:P0}";
        HideUnknownTechnology.IsChecked = draft.HideUnknownTechnology;
        PanelOpacity.Value = draft.Opacity; loading = false; LoadSection(); preview.Apply(draft); RefreshTestOverlay(); preview.UpdateGraph(summary.Points);
    }
    private void LoadSection()
    {
        loading = true; SectionStyle section = Selected;
        SectionName.Text = section.Name; SectionVisible.IsChecked = section.Visible; NameSize.Value = section.NameSize; ValueSize.Value = section.ValueSize;
        IndependentLabelSize.IsChecked = section.LabelSize > 0; LabelSize.Value = section.LabelSize > 0 ? section.LabelSize : section.ValueSize;
        ShowSectionName.IsChecked = section.ShowName; HorizontalSection.IsChecked = section.Horizontal;
        GraphOptions.Visibility = section.Kind == SectionKind.Frames ? Visibility.Visible : Visibility.Collapsed;
        MetricLayoutSelector.SelectedIndex = (int)section.Layout; SectionPadding.Value = section.Padding;
        HeroSize.Value = section.HeroSize; GraphBelow.IsChecked = section.GraphBelow; TextShadow.IsChecked = section.TextShadow;
        ShowGraph.IsChecked = section.Graph; GraphWidth.Value = section.GraphWidth; GraphHeight.Value = section.GraphHeight;
        NameColor.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(section.NameColor)); NameColor.BorderThickness = new Thickness(3);
        ValueColor.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(section.ValueColor)); ValueColor.BorderThickness = new Thickness(3);
        MetricOptions.Children.Clear(); metricChecks.Clear();
        if (section.Kind == SectionKind.Frames)
            MetricOptions.Children.Add(new TextBlock { Text = "Upscaler detection is not available. FG identification supports tagged AFMF / XeSS-FG only; unknown does not mean disabled.", TextWrapping = TextWrapping.Wrap, Foreground = Brushes.LightSlateGray, Margin = new Thickness(0, 0, 0, 8) });
        ImmutableArray<MetricValue> available = OverlayData.Build(readings, summary).Single(d => d.Kind == section.Kind).Metrics;
        foreach (string id in section.Metrics.Concat(Preferences.AvailableMetrics(section.Kind).Except(section.Metrics)))
        {
            MetricValue metric = available.Single(m => m.Id == id);
            DockPanel row = new() { Margin = new Thickness(0, 3, 0, 3) };
            CheckBox check = new() { Tag = id, IsChecked = section.Metrics.Contains(id), ToolTip = metric.Label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
            AutomationProperties.SetAutomationId(check, "MetricVisible_" + id);
            AutomationProperties.SetName(check, "Show " + metric.Label);
            if (id == "upscaler" && !section.Metrics.Contains(id)) { check.IsEnabled = false; check.ToolTip = "Upscaler detection is not implemented; unavailable for new layouts."; }
            check.Click += EditSection; metricChecks.Add(id, check); row.Children.Add(check);
            StackPanel arrows = new() { Orientation = Orientation.Horizontal };
            Button up = new() { Content = "↑", Padding = new Thickness(5, 6, 5, 6), ToolTip = "Move up", IsEnabled = section.Metrics.IndexOf(id) > 0 };
            Button down = new() { Content = "↓", Padding = new Thickness(5, 6, 5, 6), ToolTip = "Move down", IsEnabled = section.Metrics.Contains(id) && section.Metrics.IndexOf(id) < section.Metrics.Length - 1 };
            up.Click += (_, _) => MoveMetric(id, section.Metrics.IndexOf(id) - 1);
            down.Click += (_, _) => MoveMetric(id, section.Metrics.IndexOf(id) + 1);
            AutomationProperties.SetAutomationId(up, "MetricUp_" + id); AutomationProperties.SetAutomationId(down, "MetricDown_" + id);
            arrows.Children.Add(up); arrows.Children.Add(down); DockPanel.SetDock(arrows, Dock.Right); row.Children.Add(arrows);
            TextBox label = new() { Text = section.Labels.TryGetValue(id, out string? custom) ? custom : metric.Label, MaxLength = 80, Padding = new Thickness(6), ToolTip = metric.Label + " · blank hides the label" };
            AutomationProperties.SetAutomationId(label, "MetricLabel_" + id);
            label.TextChanged += (_, _) => { if (!loading) UpdateSection(Selected with { Labels = Selected.Labels.SetItem(id, label.Text) }); };
            row.Children.Add(label); MetricOptions.Children.Add(row);
        }
        loading = false;
    }
    private void MoveMetric(string id, int index)
    {
        ImmutableArray<string> moved = Selected.Metrics.Remove(id).Insert(index, id);
        UpdateSection(Selected with { Metrics = moved }); LoadSection();
    }
    private void SelectSection(object sender, SelectionChangedEventArgs e) { if (ready && !loading && SectionSelector.SelectedItem is SectionStyle) LoadSection(); }
    private void EditSection(object sender, RoutedEventArgs e)
    {
        if (!ready || loading) return;
        ImmutableArray<string> metrics = Selected.Metrics.Concat(Preferences.AvailableMetrics(Selected.Kind).Except(Selected.Metrics)).Where(id => metricChecks[id].IsChecked == true).ToImmutableArray();
        UpdateSection(Selected with { Name = SectionName.Text, Visible = SectionVisible.IsChecked == true, NameSize = NameSize.Value, ValueSize = ValueSize.Value, Metrics = metrics, ShowName = ShowSectionName.IsChecked == true, Horizontal = HorizontalSection.IsChecked == true, Graph = ShowGraph.IsChecked == true, GraphWidth = GraphWidth.Value, GraphHeight = GraphHeight.Value, Layout = (MetricLayout)MetricLayoutSelector.SelectedIndex, Padding = SectionPadding.Value, HeroSize = HeroSize.Value, GraphBelow = GraphBelow.IsChecked == true, TextShadow = TextShadow.IsChecked == true, LabelSize = IndependentLabelSize.IsChecked == true ? LabelSize.Value : 0 });
        if (sender is CheckBox check && check.Tag is string) LoadSection();
    }
    private void UpdateSection(SectionStyle section)
    {
        draft = draft with { Sections = draft.Sections.Select(s => s.Key == section.Key ? section : s).ToImmutableArray() };
        RefreshSectionSelector(); preview.Apply(draft); RefreshTestOverlay(); preview.UpdateGraph(summary.Points);
        StudioStatus.Text = "Preview updated. Save & apply to use this layout in your game.";
    }
    private void ChangeOverlaySize(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!ready || loading) return;
        try
        {
            Preferences resized = preview.ResizeOverlay(e.NewValue / 100);
            draft = draft with { OverlayScale = resized.OverlayScale, Sections = resized.Sections };
            OverlaySizeLabel.Text = $"{draft.OverlayScale:P0}";
            preview.Apply(draft); RefreshTestOverlay(); preview.UpdateGraph(summary.Points);
        }
        catch (ArgumentException error)
        {
            loading = true; OverlaySize.Value = draft.OverlayScale * 100; loading = false;
            StudioStatus.Text = error.Message;
        }
    }
    private void ChangeEditorGrid(object sender, RoutedEventArgs e)
    {
        if (!ready) return;
        preview.ShowGrid = EditorGrid.IsChecked == true;
        preview.SnapToGrid = EditorSnap.IsChecked == true;
        preview.InvalidateVisual();
    }
    private void SelectAllOverlay(object sender, RoutedEventArgs e) { preview.SelectAll(); preview.Focus(); }
    private void EditShared(object sender, RoutedEventArgs e)
    {
        if (!ready || loading) return;
        draft = draft with { Font = (string)((ComboBoxItem)FontSelector.SelectedItem).Content, Opacity = PanelOpacity.Value, HideUnknownTechnology = HideUnknownTechnology.IsChecked == true }; preview.Apply(draft); RefreshTestOverlay(); preview.UpdateGraph(summary.Points);
    }
    private void PickNameColor(object sender, RoutedEventArgs e)
    {
        string? color = Desktop.PickColor(this, Selected.NameColor); if (color is null) return;
        UpdateSection(Selected with { NameColor = color }); LoadSection();
    }
    private void PickValueColor(object sender, RoutedEventArgs e)
    {
        string? color = Desktop.PickColor(this, Selected.ValueColor); if (color is null) return;
        UpdateSection(Selected with { ValueColor = color }); LoadSection();
    }
}

