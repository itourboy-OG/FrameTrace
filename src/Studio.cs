using System.Collections.Immutable;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace FrameTrace;

public partial class MainWindow
{
    private ImmutableArray<DisplayInfo> displays = [];
    private readonly Dictionary<string, CheckBox> metricChecks = new();
    private bool zoomUpdating, fitStudio = true;
    private double zoom = 1;
    private Point? panStart;
    private Point panOffsets;
    private MouseButton? panButton;
    private bool panMoved;
    private string? contextSectionKey;
    private readonly List<Preferences> layoutHistory = [];
    private Preferences? observedLayout;
    private Preferences? gestureStart;
    private bool restoringLayout;
    private readonly CheckBox themeCardToggle = new() { Content = "Graphical card layout" };
    private readonly CheckBox usageGaugeToggle = new() { Content = "Live usage gauge" };
    private readonly StackPanel usageBarOptions = new() { Margin = new Thickness(0, 14, 0, 0) };
    private readonly CheckBox usageBarToggle = new() { Content = "Live usage bar" };
    private readonly Slider usageBarWidth = new() { Minimum = 80, Maximum = 500, Value = 220, TickFrequency = 10, IsSnapToTickEnabled = true };
    private readonly StackPanel fanOptions = new() { Margin = new Thickness(0, 12, 0, 0) };
    private readonly CheckBox fanToggle = new() { Content = "Animated GPU fan · follows fan speed" };
    private readonly TextBlock fanStatus = new() { Foreground = Brushes.LightSlateGray, Margin = new Thickness(0, 2, 0, 5), TextWrapping = TextWrapping.Wrap };
    private readonly Slider fanSize = new() { Minimum = 24, Maximum = 80, Value = 36, TickFrequency = 2, IsSnapToTickEnabled = true };
    private SectionStyle Selected => draft.Sections.Single(s => s.Key == ((SectionStyle)SectionSelector.SelectedItem).Key);

    internal void UpdateEditorGame(CaptureCandidate? candidate)
    {
        if (candidate is null || activeApplication.Equals(candidate.Application, StringComparison.OrdinalIgnoreCase)) return;
        activeApplication = candidate.Application;
        if (Pages.SelectedIndex == 1) LoadSection();
    }

    private void InitializeStudio()
    {
        displays = Desktop.Displays();
        DisplaySelector.ItemsSource = displays;
        Rect? current = Desktop.Foreground()?.Monitor;
        DisplaySelector.SelectedItem = displays.FirstOrDefault(d => d.Bounds == current) ?? displays.Single(d => d.Primary);
        PresetSelector.ItemsSource = LayoutPresets.Choices; PresetSelector.SelectedIndex = 0;
#if PREVIEW_BUILD
        string stableDirectory = Directory.Exists(AppIdentity.StableDataDirectory) ? AppIdentity.StableDataDirectory : AppIdentity.LegacyDataDirectory;
        string stableLayoutsPath = Path.Combine(stableDirectory, "saved-layouts.json");
        CustomPresetSelector.ItemsSource = SavedLayouts.ReadPreview(SavedLayouts.FilePath, stableLayoutsPath);
#else
        CustomPresetSelector.ItemsSource = SavedLayouts.Read(SavedLayouts.FilePath);
#endif
        AddMetricSelector.ItemsSource = OverlayData.Build(readings, summary).SelectMany(section => section.Metrics.Select(metric => new MetricChoice(section.Kind, metric.Id, section.Kind + " · " + metric.Label))).ToArray();
        AddMetricSelector.SelectedIndex = 0;
        AutomationProperties.SetAutomationId(themeCardToggle, "ThemeCard");
        AutomationProperties.SetAutomationId(usageGaugeToggle, "LiveUsageGauge");
        AutomationProperties.SetAutomationId(usageBarToggle, "LiveUsageBar");
        AutomationProperties.SetAutomationId(usageBarWidth, "LiveUsageBarWidth");
        themeCardToggle.ToolTip = "Draw a full card behind this item, with a hardware header and live trace.";
        usageGaugeToggle.ToolTip = "Show a circular gauge that follows the selected item's usage.";
        usageBarToggle.ToolTip = "Show a horizontal bar that follows the selected item's usage.";
        fanToggle.ToolTip = "Show an animated GPU fan icon; it pauses when the fan stops.";
        usageBarWidth.ToolTip = "Width of the live usage bar. Default: 220 pixels. Double-click to reset.";
        usageBarWidth.PreviewMouseDoubleClick += (_, e) => { usageBarWidth.Value = 220; e.Handled = true; };
        usageBarOptions.Children.Add(usageBarToggle);
        usageBarOptions.Children.Add(new TextBlock { Text = "Bar width", Foreground = Brushes.LightSlateGray, ToolTip = usageBarWidth.ToolTip, Margin = new Thickness(0, 8, 0, 4) });
        usageBarOptions.Children.Add(usageBarWidth);
        AutomationProperties.SetAutomationId(fanToggle, "AnimatedFan");
        AutomationProperties.SetAutomationId(fanStatus, "FanStatus");
        AutomationProperties.SetAutomationId(fanSize, "AnimatedFanSize");
        fanSize.ToolTip = "Size of the animated GPU fan icon. Default: 36 pixels. Double-click to reset.";
        fanSize.PreviewMouseDoubleClick += (_, e) => { fanSize.Value = 36; e.Handled = true; };
        fanOptions.Children.Add(fanToggle);
        fanOptions.Children.Add(fanStatus);
        fanOptions.Children.Add(new TextBlock { Text = "Fan icon size", Foreground = Brushes.LightSlateGray, ToolTip = fanSize.ToolTip, Margin = new Thickness(0, 8, 0, 4) });
        fanOptions.Children.Add(fanSize);
        WidgetOptions.Children.Add(themeCardToggle);
        WidgetOptions.Children.Add(new TextBlock { Text = "A full panel with a hardware header. GPU and CPU show a live load trace; FPS uses the frame-time graph when enabled.", Foreground = Brushes.LightSlateGray, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 12) });
        WidgetOptions.Children.Add(fanOptions);
        WidgetOptions.Children.Add(usageBarOptions);
        WidgetOptions.Children.Add(usageGaugeToggle);
        themeCardToggle.Click += EditSection;
        usageGaugeToggle.Click += EditSection;
        usageBarToggle.Click += EditSection;
        usageBarWidth.ValueChanged += EditSection;
        fanToggle.Click += EditSection;
        fanSize.ValueChanged += EditSection;
        InitializeArtworkTools();
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
        CanvasDescription.Text = $"{display.Bounds.Width:0} × {display.Bounds.Height:0} · Windows scale {display.Scale:P0}. Ctrl + wheel to zoom; right-drag to pan. Positions match this display.";
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
    private void FitItems(object sender, RoutedEventArgs e)
    {
        Rect bounds = preview.VisibleBounds();
        if (bounds.IsEmpty) return;
        fitStudio = false;
        SetZoom(Math.Min(1.25, Math.Min((PreviewViewport.ActualWidth - 24) / bounds.Width, (PreviewViewport.ActualHeight - 24) / bounds.Height)));
        PreviewViewport.UpdateLayout();
        PreviewViewport.ScrollToHorizontalOffset(Math.Max(0, bounds.X * zoom - 12));
        PreviewViewport.ScrollToVerticalOffset(Math.Max(0, bounds.Y * zoom - 12));
    }
    private void ActualStudio(object sender, RoutedEventArgs e) { fitStudio = false; SetZoom(1); }
    private void ResetZoom(object sender, MouseButtonEventArgs e) { FitPreview(); e.Handled = true; }
    private void ResetOverlaySize(object sender, MouseButtonEventArgs e) { OverlaySize.Value = 100; e.Handled = true; }
    private void ResetPanelOpacity(object sender, MouseButtonEventArgs e) { PanelOpacity.Value = 0.65; e.Handled = true; }
    private void ResetSensorRefresh(object sender, MouseButtonEventArgs e) { SensorRefreshSlider.Value = 1000; e.Handled = true; }
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
        if (e.ChangedButton is not (MouseButton.Middle or MouseButton.Right)) return;
        if (e.OriginalSource is not DependencyObject source || !preview.ContainsSource(source)) return;
        panButton = e.ChangedButton;
        contextSectionKey = preview.SectionAt(source);
        panMoved = false;
        panStart = e.GetPosition(PreviewViewport); panOffsets = new Point(PreviewViewport.HorizontalOffset, PreviewViewport.VerticalOffset);
        PreviewViewport.CaptureMouse(); e.Handled = true;
    }
    private void PanStudio(object sender, MouseEventArgs e)
    {
        if (panStart is not Point start || panButton is not MouseButton button || (button == MouseButton.Middle ? e.MiddleButton : e.RightButton) != MouseButtonState.Pressed) return;
        Point now = e.GetPosition(PreviewViewport);
        if (!panMoved && Math.Abs(now.X - start.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(now.Y - start.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        panMoved = true;
        PreviewViewport.ScrollToHorizontalOffset(panOffsets.X + start.X - now.X);
        PreviewViewport.ScrollToVerticalOffset(panOffsets.Y + start.Y - now.Y); e.Handled = true;
    }
    private void EndPan(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != panButton) return;
        bool showMenu = panButton == MouseButton.Right && !panMoved;
        string? key = contextSectionKey;
        panStart = null; panButton = null; PreviewViewport.ReleaseMouseCapture(); e.Handled = true;
        if (showMenu) { ContextMenu menu = CreateCanvasMenu(key); menu.IsOpen = true; }
    }
    internal ContextMenu CreateCanvasMenu(string? key)
    {
        ContextMenu menu = new() { PlacementTarget = PreviewViewport, Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint };
        if (key is not null)
        {
            preview.SelectItem(key);
            MenuItem edit = new() { Header = "Edit item" };
            AutomationProperties.SetAutomationId(edit, "CanvasEditItem");
            edit.Click += (_, _) => StudioInspectorTabs.SelectedIndex = 0;
            menu.Items.Add(edit);
            if (Selected.Kind == SectionKind.Artwork)
            {
                MenuItem resize = new() { Header = "Resize artwork" };
                AutomationProperties.SetAutomationId(resize, "CanvasResizeArtwork");
                resize.Click += (_, _) => { StudioArtworkTab.IsSelected = true; artworkWidth.Focus(); };
                menu.Items.Add(resize);
            }
            MenuItem forward = new() { Header = "Bring forward", IsEnabled = draft.Sections.IndexOf(Selected) < draft.Sections.Length - 1 };
            AutomationProperties.SetAutomationId(forward, "CanvasBringForward");
            forward.Click += (_, _) => MoveLayer(1);
            menu.Items.Add(forward);
            MenuItem backward = new() { Header = "Send backward", IsEnabled = draft.Sections.IndexOf(Selected) > 0 };
            AutomationProperties.SetAutomationId(backward, "CanvasSendBackward");
            backward.Click += (_, _) => MoveLayer(-1);
            menu.Items.Add(backward);
            MenuItem duplicate = new() { Header = "Duplicate item", IsEnabled = draft.Sections.Length < 64 };
            AutomationProperties.SetAutomationId(duplicate, "CanvasDuplicateItem");
            duplicate.Click += (_, _) => DuplicateOverlayItem();
            menu.Items.Add(duplicate);
            menu.Items.Add(new Separator());
            MenuItem remove = new() { Header = "Remove item" };
            AutomationProperties.SetAutomationId(remove, "CanvasRemoveItem");
            remove.Click += (_, _) => RemoveOverlayItem(remove, new RoutedEventArgs());
            menu.Items.Add(remove);
        }
        else
        {
            MenuItem zoomIn = new() { Header = "Zoom in" };
            AutomationProperties.SetAutomationId(zoomIn, "CanvasZoomIn");
            zoomIn.Click += (_, _) => { fitStudio = false; SetZoom(zoom * 1.2); };
            menu.Items.Add(zoomIn);
            MenuItem zoomOut = new() { Header = "Zoom out" };
            AutomationProperties.SetAutomationId(zoomOut, "CanvasZoomOut");
            zoomOut.Click += (_, _) => { fitStudio = false; SetZoom(zoom / 1.2); };
            menu.Items.Add(zoomOut);
            MenuItem fit = new() { Header = "Fit canvas" };
            AutomationProperties.SetAutomationId(fit, "CanvasFit");
            fit.Click += FitStudio;
            menu.Items.Add(fit);
        }
        return menu;
    }
    private void ApplyPreset(object sender, RoutedEventArgs e)
    {
        if (PresetSelector.SelectedItem is not LayoutPresets.Choice choice) return;
        string name = choice.Name;
        if (!ConfirmLayoutReplacement($"load ‘{name}’")) return;
        draft = LayoutPresets.Create(draft, name); preview.Apply(draft); RefreshTestOverlay();
        preview.UpdateData(OverlayData.Build(readings, summary));
        draft = preview.ArrangePreset(name); LoadControls();
        StudioStatus.Text = name + " is ready in preview. Save & apply when it looks right.";
    }

    private bool ConfirmLayoutReplacement(string action) => !Preferences.HasLayoutChanges(draft, layoutBaseline)
        || MessageBox.Show(this, $"Discard your unsaved overlay edits and {action}?\n\nChoose No to keep editing. Use Save & apply or Save preset to keep your changes.", "Unsaved overlay edits", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;

    private void SetLayoutBaseline()
    {
        layoutBaseline = draft;
        RefreshLayoutNotice();
    }

    private void RefreshLayoutNotice()
    {
        if (Preferences.HasLayoutChanges(draft, layoutBaseline))
        {
            if (StudioSaveState.Text.StartsWith("✓", StringComparison.Ordinal))
            {
                StudioSaveState.Text = "Unsaved edits";
                StudioSaveState.Foreground = Brushes.LightSalmon;
            }
            if (SettingsSaveState.Text.StartsWith("✓", StringComparison.Ordinal))
            {
                SettingsSaveState.Text = "Unsaved overlay edits";
                SettingsSaveState.Foreground = Brushes.LightSalmon;
            }
        }
        if (gestureStart is not null)
        {
            if (observedLayout is not null && LayoutReferenceChanged(draft, observedLayout))
            {
                observedLayout = draft;
                UnsavedLayoutNotice.Visibility = Visibility.Visible;
            }
            UndoStudioButton.IsEnabled = false;
            return;
        }
        if (observedLayout is not null && Preferences.HasLayoutChanges(draft, observedLayout))
        {
            if (!restoringLayout)
            {
                layoutHistory.Add(observedLayout);
                if (layoutHistory.Count > 40) layoutHistory.RemoveAt(0);
            }
            observedLayout = draft;
        }
        observedLayout ??= draft;
        UndoStudioButton.IsEnabled = layoutHistory.Count > 0;
        UnsavedLayoutNotice.Visibility = Preferences.HasLayoutChanges(draft, layoutBaseline) ? Visibility.Visible : Visibility.Collapsed;
    }

    private static bool LayoutReferenceChanged(Preferences current, Preferences previous) =>
        current.Accent != previous.Accent || current.Opacity != previous.Opacity || current.Font != previous.Font
        || current.OverlayScale != previous.OverlayScale || current.HideUnknownTechnology != previous.HideUnknownTechnology
        || !current.Sections.Equals(previous.Sections);

    private void BeginLayoutGesture() { gestureStart ??= draft; UndoStudioButton.IsEnabled = false; }

    private void EndLayoutGesture()
    {
        if (gestureStart is null) return;
        if (Preferences.HasLayoutChanges(draft, gestureStart))
        {
            layoutHistory.Add(gestureStart);
            if (layoutHistory.Count > 40) layoutHistory.RemoveAt(0);
        }
        gestureStart = null;
        RefreshLayoutNotice();
    }

    private void UndoStudioShortcut(object sender, KeyEventArgs e)
    {
        if (Pages.SelectedIndex != 1 || e.Key != Key.Z || Keyboard.Modifiers != ModifierKeys.Control || Keyboard.FocusedElement is TextBoxBase) return;
        UndoStudio(sender, new RoutedEventArgs());
        e.Handled = true;
    }

    private void UndoStudio(object sender, RoutedEventArgs e)
    {
        if (gestureStart is not null || layoutHistory.Count == 0) return;
        Preferences previous = layoutHistory[^1];
        layoutHistory.RemoveAt(layoutHistory.Count - 1);
        restoringLayout = true;
        try
        {
            draft = Preferences.ApplyImportedLayout(draft, previous);
            LoadControls();
            StudioStatus.Text = "Last overlay edit undone. Save & apply to use this layout in your game.";
        }
        finally { restoringLayout = false; }
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
        InspectAmdUpscalerInput.IsChecked = draft.InspectAmdUpscaler;
        SensorRefreshSlider.Value = draft.SensorRefreshMs;
        IgnoredApps.Text = string.Join(Environment.NewLine, draft.IgnoredApps);
        FontSelector.SelectedIndex = Array.IndexOf(new[] { "Consolas", "Segoe UI", "Arial", "Cascadia Mono" }, draft.Font);
        OverlaySize.Value = draft.OverlayScale * 100; OverlaySizeLabel.Text = $"{draft.OverlayScale:P0}";
        HideUnknownTechnology.IsChecked = draft.HideUnknownTechnology;
        PanelOpacity.Value = draft.Opacity; loading = false; LoadSection(); preview.Apply(draft); RefreshTestOverlay(); preview.UpdateGraph(summary.Points); RefreshLayoutNotice();
    }
    private void LoadSection()
    {
        loading = true;
        bool empty = draft.Sections.IsEmpty;
        ((TabItem)StudioInspectorTabs.Items[0]).IsEnabled = !empty;
        ((TabItem)StudioInspectorTabs.Items[3]).IsEnabled = !empty;
        SectionSelector.IsEnabled = !empty;
        RemoveOverlayItemButton.IsEnabled = !empty;
        if (empty)
        {
            MetricOptions.Children.Clear(); metricChecks.Clear();
            StudioArtworkTab.IsSelected = true;
            loading = false;
            return;
        }
        SectionStyle section = Selected;
        SectionName.Text = section.Name; SectionVisible.IsChecked = section.Visible; NameSize.Value = section.NameSize; ValueSize.Value = section.ValueSize;
        IndependentLabelSize.IsChecked = section.LabelSize > 0; LabelSize.Value = section.LabelSize > 0 ? section.LabelSize : section.ValueSize;
        ShowSectionName.IsChecked = section.ShowName; HorizontalSection.IsChecked = section.Horizontal;
        GraphOptions.Visibility = section.Kind is SectionKind.Artwork or SectionKind.Game ? Visibility.Collapsed : Visibility.Visible;
        MetricLayoutSelector.SelectedIndex = (int)section.Layout; SectionPadding.Value = section.Padding;
        HeroSize.Value = section.HeroSize; GraphBelow.IsChecked = section.GraphBelow; GraphTransparent.IsChecked = section.GraphTransparent; TextShadow.IsChecked = section.TextShadow;
        artworkOptions.Visibility = section.Kind == SectionKind.Artwork && (section.ImageData.Length > 0 || !section.AnimationFrames.IsEmpty || section.ArtworkFill || section.BarSource != ArtworkBarSource.None) ? Visibility.Visible : Visibility.Collapsed;
        artworkWidth.Value = section.ImageWidth;
        artworkHeight.Value = section.ImageHeight;
        artworkOpacity.Value = section.ArtworkOpacity;
        artworkRadius.Value = section.ArtworkRadius;
        radiusOptions.Visibility = section.ArtworkFill ? Visibility.Visible : Visibility.Collapsed;
        animationOptions.Visibility = section.AnimationFrames.IsEmpty ? Visibility.Collapsed : Visibility.Visible;
        animationPlayback.SelectedIndex = (int)section.AnimationSource;
        animationSpeed.Value = section.AnimationIntervalMs;
        animationSpeedOptions.Visibility = section.AnimationSource == ArtworkAnimationSource.Loop ? Visibility.Visible : Visibility.Collapsed;
        alarmThresholdOptions.Visibility = section.AnimationSource is ArtworkAnimationSource.GpuTemperatureAlarm or ArtworkAnimationSource.CpuTemperatureAlarm ? Visibility.Visible : Visibility.Collapsed;
        alarmThreshold.Value = Math.Clamp(section.AnimationMaximum, alarmThreshold.Minimum, alarmThreshold.Maximum);
        NameColor.Content = section.BarSource != ArtworkBarSource.None ? "Bar color" : section.ArtworkFill ? "Fill color" : "Name color";
        themeCardToggle.Visibility = section.Kind == SectionKind.Artwork ? Visibility.Collapsed : Visibility.Visible;
        themeCardToggle.IsChecked = section.ThemeCard;
        usageGaugeToggle.Visibility = section.Kind is SectionKind.Gpu or SectionKind.Cpu ? Visibility.Visible : Visibility.Collapsed;
        usageGaugeToggle.IsChecked = section.UsageGauge;
        usageBarOptions.Visibility = section.Kind is SectionKind.Gpu or SectionKind.Cpu ? Visibility.Visible : Visibility.Collapsed;
        usageBarToggle.IsChecked = section.UsageBar; usageBarWidth.Value = section.UsageBarWidth;
        fanOptions.Visibility = section.Kind == SectionKind.Gpu ? Visibility.Visible : Visibility.Collapsed;
        fanToggle.IsChecked = section.FanWidget; fanSize.Value = section.FanSize;
        UpdateFanStatus();
        ShowGraph.IsChecked = section.Graph; GraphWidth.Value = section.GraphWidth; GraphHeight.Value = section.GraphHeight;
        NameColor.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(section.NameColor)); NameColor.BorderThickness = new Thickness(3);
        ValueColor.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(section.ValueColor)); ValueColor.BorderThickness = new Thickness(3);
        MetricOptions.Children.Clear(); metricChecks.Clear();
        if (section.Kind == SectionKind.Frames)
        {
            MetricOptions.Children.Add(new TextBlock { Text = "Cyberpunk shows saved upscaler and frame-generation choices. Other games may show loaded AMD FSR or Intel XeSS libraries; that does not prove the active mode.", TextWrapping = TextWrapping.Wrap, Foreground = Brushes.LightSlateGray, Margin = new Thickness(0, 0, 0, 8) });
            Border confirmations = new() { Background = new SolidColorBrush(Color.FromRgb(27, 39, 50)), BorderBrush = new SolidColorBrush(Color.FromRgb(53, 71, 86)), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Padding = new Thickness(8), Margin = new Thickness(0, 0, 0, 10) };
            StackPanel content = new(); confirmations.Child = content;
            string[] games = RunningEditorGames();
            if (activeApplication.Length == 0 && games.Length == 1) activeApplication = games[0];
            content.Children.Add(new TextBlock { Text = "GAME FOR YOUR CONFIRMATION", Foreground = Brushes.MediumAquamarine, Margin = new Thickness(0, 0, 0, 6) });
            ComboBox gamePicker = new() { ItemsSource = games, SelectedItem = activeApplication.Length == 0 ? null : activeApplication, Style = (Style)FindResource("CaptureTargetStyle"), MaxDropDownHeight = 260, ToolTip = "Choose a running game. This choice stays available if you switch from the game to Frame Trace.", Margin = new Thickness(0, 0, 0, 8) };
            AutomationProperties.SetAutomationId(gamePicker, "ConfirmationGame");
            gamePicker.SelectionChanged += (_, _) =>
            {
                if (loading || gamePicker.SelectedItem is not string application || activeApplication.Equals(application, StringComparison.OrdinalIgnoreCase)) return;
                activeApplication = application;
                LoadSection();
            };
            content.Children.Add(gamePicker);
            if (games.Length == 0) content.Children.Add(new TextBlock { Text = "Open a game, then return to Overlay Studio to choose it.", Foreground = Brushes.LightSlateGray, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) });
            GameTechnologyChoice? choice = draft.GameTechnologyChoices.FirstOrDefault(item => item.Application.Equals(activeApplication, StringComparison.OrdinalIgnoreCase));
            CheckBox confirmedFsr411 = new() { Content = "My game uses FSR 4.1.1 upscaling", IsChecked = choice?.Fsr411 == true, IsEnabled = activeApplication.Length != 0, ToolTip = "Your per-game confirmation, not automatic detection. Changes the displayed FSR version; keeps the detected quality preset when available." };
            CheckBox confirmedFsr4Fg = new() { Content = "My game uses FSR 4 frame generation", IsChecked = choice?.Fsr4FrameGeneration == true, IsEnabled = activeApplication.Length != 0, ToolTip = "Your per-game confirmation, not automatic detection. Shows FSR 4 FG; keeps Cyberpunk's saved On/Off state when available." };
            AutomationProperties.SetAutomationId(confirmedFsr411, "ConfirmFsr411");
            AutomationProperties.SetAutomationId(confirmedFsr4Fg, "ConfirmFsr4Fg");
            confirmedFsr411.Click += (_, _) => EditGameTechnologyChoice(confirmedFsr411, confirmedFsr4Fg);
            confirmedFsr4Fg.Click += (_, _) => EditGameTechnologyChoice(confirmedFsr411, confirmedFsr4Fg);
            content.Children.Add(confirmedFsr411); content.Children.Add(confirmedFsr4Fg);
            content.Children.Add(new TextBlock { Text = "These are your choices for this game, not verified runtime readings. Save & apply to use them in the overlay.", Foreground = Brushes.LightSlateGray, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 5, 0, 0) });
            MetricOptions.Children.Add(confirmations);
        }
        if (section.Kind == SectionKind.Game)
            MetricOptions.Children.Add(new TextBlock { Text = "D3D9 is identified separately. DXGI is the presentation API shared by newer Direct3D versions; the exact version is not available from capture.", TextWrapping = TextWrapping.Wrap, Foreground = Brushes.LightSlateGray, Margin = new Thickness(0, 0, 0, 8) });
        ImmutableArray<MetricValue> available = OverlayData.Build(readings, summary).FirstOrDefault(d => d.Kind == section.Kind)?.Metrics ?? [];
        if (section.Metrics.Length > 1)
        {
            Button splitAll = new() { Content = "Split all readings", ToolTip = "Keep the first reading here and create one movable item for each other reading.", Margin = new Thickness(0, 0, 0, 10) };
            AutomationProperties.SetAutomationId(splitAll, "SplitAllMetrics");
            splitAll.Click += SplitAllMetrics;
            MetricOptions.Children.Add(splitAll);
        }
        if (section.Metrics.Length == 1)
        {
            SectionStyle[] targets = draft.Sections.Where(item => item.Key != section.Key && item.Kind == section.Kind && !item.Metrics.Contains(section.Metrics[0])).ToArray();
            if (targets.Length > 0)
            {
                MetricOptions.Children.Add(new TextBlock { Text = "JOIN WITH ANOTHER ITEM", Foreground = Brushes.LightSlateGray, Margin = new Thickness(0, 0, 0, 5) });
                ComboBox targetPicker = new() { ItemsSource = targets, ItemTemplate = SectionSelector.ItemTemplate, SelectedIndex = 0, Style = (Style)FindResource("CaptureTargetStyle") };
                AutomationProperties.SetAutomationId(targetPicker, "JoinMetricTarget");
                MetricOptions.Children.Add(targetPicker);
                Button join = new() { Content = "Join reading", ToolTip = "Move this reading into the chosen item, then remove this item.", Margin = new Thickness(0, 6, 0, 10) };
                AutomationProperties.SetAutomationId(join, "JoinMetric");
                join.Click += (_, _) => JoinMetric(((SectionStyle)targetPicker.SelectedItem).Key);
                MetricOptions.Children.Add(join);
            }
        }
        foreach (string id in section.Metrics.Concat(Preferences.AvailableMetrics(section.Kind).Except(section.Metrics)))
        {
            MetricValue metric = available.Single(m => m.Id == id);
            Border card = new() { Background = new SolidColorBrush(Color.FromRgb(27, 39, 50)), BorderBrush = new SolidColorBrush(Color.FromRgb(53, 71, 86)), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Padding = new Thickness(8), Margin = new Thickness(0, 0, 0, 7) };
            StackPanel content = new(); card.Child = content;
            DockPanel header = new(); content.Children.Add(header);
            CheckBox check = new() { Tag = id, IsChecked = section.Metrics.Contains(id), ToolTip = "Show or hide " + metric.Label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 7, 0) };
            AutomationProperties.SetAutomationId(check, "MetricVisible_" + id);
            AutomationProperties.SetName(check, "Show " + metric.Label);
            check.Click += EditSection; metricChecks.Add(id, check);
            StackPanel arrows = new() { Orientation = Orientation.Horizontal };
            Button up = new() { Content = "↑", Padding = new Thickness(5, 6, 5, 6), ToolTip = "Move up", IsEnabled = section.Metrics.IndexOf(id) > 0 };
            Button down = new() { Content = "↓", Padding = new Thickness(5, 6, 5, 6), ToolTip = "Move down", IsEnabled = section.Metrics.Contains(id) && section.Metrics.IndexOf(id) < section.Metrics.Length - 1 };
            up.Click += (_, _) => MoveMetric(id, section.Metrics.IndexOf(id) - 1);
            down.Click += (_, _) => MoveMetric(id, section.Metrics.IndexOf(id) + 1);
            AutomationProperties.SetAutomationId(up, "MetricUp_" + id); AutomationProperties.SetAutomationId(down, "MetricDown_" + id);
            arrows.Children.Add(up); arrows.Children.Add(down); DockPanel.SetDock(arrows, Dock.Right); header.Children.Add(arrows);
            DockPanel.SetDock(check, Dock.Left); header.Children.Add(check);
            header.Children.Add(new TextBlock { Text = metric.Label, FontWeight = FontWeights.SemiBold, Foreground = section.Metrics.Contains(id) ? Brushes.MediumAquamarine : Brushes.LightSlateGray, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
            DockPanel editor = new() { Margin = new Thickness(0, 7, 0, 0) }; content.Children.Add(editor);
            if (section.Metrics.Contains(id) && section.Metrics.Length > 1)
            {
                Button separate = new() { Content = "Move out", ToolTip = "Move this reading into its own movable item.", Padding = new Thickness(5, 6, 5, 6), Margin = new Thickness(0, 0, 5, 0) };
                AutomationProperties.SetAutomationId(separate, "SeparateMetric_" + id);
                separate.Click += (_, _) => SeparateMetric(id);
                DockPanel.SetDock(separate, Dock.Right); editor.Children.Add(separate);
            }
            TextBox label = new() { Text = section.Labels.TryGetValue(id, out string? custom) ? custom : metric.Label, MaxLength = 80, Padding = new Thickness(6), ToolTip = metric.Label + " · blank hides the label" };
            AutomationProperties.SetAutomationId(label, "MetricLabel_" + id);
            label.TextChanged += (_, _) => { if (!loading) UpdateSection(Selected with { Labels = Selected.Labels.SetItem(id, label.Text) }); };
            editor.Children.Add(label); MetricOptions.Children.Add(card);
        }
        loading = false;
    }
    private string[] RunningEditorGames() => RunningApplications.List().Select(item => item.Application)
        .Concat(capture?.Candidates().Where(item => item.ProcessId != Environment.ProcessId).Select(item => item.Application) ?? [])
        .Concat(selectedTarget.ProcessId == 0 ? [] : [selectedTarget.Application])
        .Concat(activeApplication.Length == 0 ? [] : [activeApplication])
        .Where(application => !draft.IgnoredApps.Contains(application, StringComparer.OrdinalIgnoreCase) || activeApplication.Equals(application, StringComparison.OrdinalIgnoreCase))
        .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(application => application, StringComparer.OrdinalIgnoreCase).ToArray();
    private void UpdateFanStatus()
    {
        double? rpm = Readings.Find(readings, "Gpu", "Fan", "GPU Fan")?.Value;
        fanStatus.Text = rpm switch
        {
            null => "Fan speed sensor unavailable. The icon stays still.",
            <= 0 => "Current fan: 0 RPM. Zero RPM mode is normal at idle; the icon moves when the fan starts.",
            _ => $"Current fan: {rpm:0} RPM. The icon follows the reported speed."
        };
        fanStatus.Foreground = rpm is > 0 ? new SolidColorBrush(Color.FromRgb(131, 239, 205)) : Brushes.LightSlateGray;
    }
    private void EditGameTechnologyChoice(CheckBox confirmedFsr411, CheckBox confirmedFsr4Fg)
    {
        if (!ready || loading || activeApplication.Length == 0) return;
        GameTechnologyChoice choice = new(activeApplication, confirmedFsr411.IsChecked == true, confirmedFsr4Fg.IsChecked == true);
        draft = draft with { GameTechnologyChoices = GameTechnologyChoices.Set(draft.GameTechnologyChoices, choice) };
        StudioSaveState.Text = "Unsaved game choices";
        StudioSaveState.Foreground = Brushes.LightSalmon;
        StudioStatus.Text = "Save & apply to show your confirmation for " + activeApplication + ".";
    }
    private void MoveMetric(string id, int index)
    {
        ImmutableArray<string> moved = Selected.Metrics.Remove(id).Insert(index, id);
        UpdateSection(Selected with { Metrics = moved }); LoadSection();
    }
    private void SelectSection(object sender, SelectionChangedEventArgs e) { if (ready && !loading && SectionSelector.SelectedItem is SectionStyle) LoadSection(); }
    private void EditSection(object sender, RoutedEventArgs e)
    {
        if (!ready || loading || draft.Sections.IsEmpty) return;
        ImmutableArray<string> metrics = Selected.Metrics.Concat(Preferences.AvailableMetrics(Selected.Kind).Except(Selected.Metrics)).Where(id => metricChecks[id].IsChecked == true).ToImmutableArray();
        SectionStyle edited = Selected with { Name = SectionName.Text, Visible = SectionVisible.IsChecked == true, NameSize = NameSize.Value, ValueSize = ValueSize.Value, Metrics = metrics, ShowName = ShowSectionName.IsChecked == true, Horizontal = HorizontalSection.IsChecked == true, Graph = ShowGraph.IsChecked == true, GraphTransparent = GraphTransparent.IsChecked == true, GraphWidth = GraphWidth.Value, GraphHeight = GraphHeight.Value, Layout = (MetricLayout)MetricLayoutSelector.SelectedIndex, Padding = SectionPadding.Value, HeroSize = HeroSize.Value, GraphBelow = GraphBelow.IsChecked == true, TextShadow = TextShadow.IsChecked == true, LabelSize = IndependentLabelSize.IsChecked == true ? LabelSize.Value : 0 };
        edited = edited with { ThemeCard = themeCardToggle.IsChecked == true, UsageGauge = usageGaugeToggle.IsChecked == true, UsageBar = usageBarToggle.IsChecked == true, UsageBarWidth = usageBarWidth.Value, FanWidget = fanToggle.IsChecked == true, FanSize = fanSize.Value, ImageWidth = artworkWidth.Value, ImageHeight = artworkHeight.Value, ArtworkOpacity = artworkOpacity.Value, ArtworkRadius = artworkRadius.Value, AnimationIntervalMs = (int)animationSpeed.Value, AnimationSource = Selected.AnimationFrames.IsEmpty ? Selected.AnimationSource : (ArtworkAnimationSource)animationPlayback.SelectedIndex };
        if (edited.AnimationSource is ArtworkAnimationSource.GpuTemperatureAlarm or ArtworkAnimationSource.CpuTemperatureAlarm)
            edited = edited with { AnimationMinimum = alarmThreshold.Value - 1, AnimationMaximum = alarmThreshold.Value };
        if (sender == alarmThreshold)
        {
            string sensor = edited.AnimationSource == ArtworkAnimationSource.CpuTemperatureAlarm ? "CPU" : "GPU";
            string oldName = $"{sensor} temperature alarm · {Selected.AnimationMaximum:0}°C";
            edited = edited with { AlarmThresholdCustomized = true, Name = Selected.Name == oldName ? $"{sensor} temperature alarm · {alarmThreshold.Value:0}°C" : edited.Name };
        }
        UpdateSection(edited);
        if (sender is CheckBox { Tag: string }) LoadSection();
        if (sender == animationPlayback || sender == alarmThreshold) LoadSection();
    }
    private void UpdateSection(SectionStyle section)
    {
        draft = draft with { Sections = draft.Sections.Select(s => s.Key == section.Key ? section : s).ToImmutableArray() };
        RefreshSectionSelector(); preview.Apply(draft); RefreshTestOverlay(); preview.UpdateGraph(summary.Points);
        StudioStatus.Text = "Preview updated. Save & apply to use this layout in your game.";
        RefreshLayoutNotice();
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
            RefreshLayoutNotice();
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
        draft = draft with { Font = (string)((ComboBoxItem)FontSelector.SelectedItem).Content, Opacity = PanelOpacity.Value, HideUnknownTechnology = HideUnknownTechnology.IsChecked == true }; preview.Apply(draft); RefreshTestOverlay(); preview.UpdateGraph(summary.Points); RefreshLayoutNotice();
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

