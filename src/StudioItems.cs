using System.Collections.Immutable;
using System.Windows;

namespace FrameTrace;

public sealed record MetricChoice(SectionKind Kind, string Id, string Label);

public partial class MainWindow
{
    private void RefreshSectionSelector()
    {
        string? key = (SectionSelector.SelectedItem as SectionStyle)?.Key;
        bool previousLoading = loading; loading = true;
        SectionSelector.ItemsSource = draft.Sections;
        SectionSelector.SelectedItem = draft.Sections.FirstOrDefault(item => item.Key == key) ?? draft.Sections.FirstOrDefault();
        loading = previousLoading;
    }

    private void SelectEditorSection(string key) => SectionSelector.SelectedItem = SectionSelector.Items.Cast<SectionStyle>().Single(item => item.Key == key);

    private void AddSeparateMetric(object sender, RoutedEventArgs e)
    {
        if (AddMetricSelector.SelectedItem is not MetricChoice metric) return;
        if (draft.Sections.Length >= Preferences.MaxOverlayItems) { StudioStatus.Text = $"This layout has reached its {Preferences.MaxOverlayItems}-item limit."; return; }
        SectionStyle source =
            metric.Kind == SectionKind.Game
            ? new SectionStyle(SectionKind.Game, "GAME", "#83EFCD", "#FFFFFF", 16, 20, 0.35, 0.1, true, []) { ShowName = false, Layout = MetricLayout.Table }
            :
            Preferences.Initial.Sections.Single(item => item.Kind == metric.Kind);
        SectionStyle item = source with
        {
            Id = Guid.NewGuid().ToString("N"), Name = metric.Label, ShowName = false,
            Metrics = [metric.Id], X = 0.35, Y = 0.1 + (draft.Sections.Count(section => section.Id.Length != 0) % 8) * 0.06, ValueSize = 20, LabelSize = 16
        };
        draft = draft with { Sections = draft.Sections.Add(item) };
        RefreshSectionSelector(); SelectEditorSection(item.Key); LoadControls();
        StudioStatus.Text = "Separate metric added. Drag it anywhere and edit its label, colors, and sizes. Save & apply to keep it.";
    }

    private SectionStyle NewSingleMetricItem(SectionStyle source, string id, int index)
    {
        string label = OverlayData.Build(readings, summary).Single(item => item.Kind == source.Kind).Metrics.Single(metric => metric.Id == id).Label;
        return source with
        {
            Id = Guid.NewGuid().ToString("N"), Name = label, ShowName = false, Metrics = [id],
            X = 0.35, Y = 0.1 + (index % 8) * 0.07,
            Graph = false, UsageGauge = false, UsageBar = false, FanWidget = false
        };
    }

    private void SeparateMetric(string id)
    {
        SectionStyle source = Selected;
        if (!source.Metrics.Contains(id) || source.Metrics.Length < 2) throw new InvalidOperationException("Choose a reading in an item containing at least two readings.");
        if (draft.Sections.Length >= Preferences.MaxOverlayItems) { StudioStatus.Text = $"This layout has reached its {Preferences.MaxOverlayItems}-item limit."; return; }
        SectionStyle separate = NewSingleMetricItem(source, id, draft.Sections.Count(item => item.Id.Length != 0));
        int index = draft.Sections.IndexOf(source);
        draft = draft with { Sections = draft.Sections.SetItem(index, source with { Metrics = source.Metrics.Remove(id) }).Insert(index + 1, separate) };
        RefreshSectionSelector(); SelectEditorSection(separate.Key); LoadControls();
        StudioStatus.Text = $"Moved {separate.Name} into its own item. Save & apply to keep the layout.";
    }

    private void SplitAllMetrics(object sender, RoutedEventArgs e)
    {
        SectionStyle source = Selected;
        int newCount = source.Metrics.Length - 1;
        if (newCount < 1) return;
        if (draft.Sections.Length + newCount > Preferences.MaxOverlayItems) { StudioStatus.Text = $"This layout has reached its {Preferences.MaxOverlayItems}-item limit."; return; }
        int index = draft.Sections.IndexOf(source);
        ImmutableArray<SectionStyle> separate = source.Metrics.Skip(1)
            .Select((id, offset) => NewSingleMetricItem(source, id, draft.Sections.Count(item => item.Id.Length != 0) + offset)).ToImmutableArray();
        draft = draft with { Sections = draft.Sections.SetItem(index, source with { Metrics = [source.Metrics[0]] }).InsertRange(index + 1, separate) };
        LoadControls();
        StudioStatus.Text = $"Split {source.Metrics.Length} readings into movable items. Save & apply to keep the layout.";
    }

    private void JoinMetric(string targetKey)
    {
        SectionStyle source = Selected;
        if (source.Metrics.Length != 1) throw new InvalidOperationException("Choose an item containing exactly one reading to join.");
        SectionStyle target = draft.Sections.Single(item => item.Key == targetKey);
        string id = source.Metrics[0];
        if (target.Key == source.Key || target.Kind != source.Kind || target.Metrics.Contains(id))
            throw new InvalidOperationException("Choose a different item of the same type that does not already contain this reading.");
        draft = draft with { Sections = draft.Sections
            .Where(item => item.Key != source.Key)
            .Select(item => item.Key == target.Key ? item with { Metrics = item.Metrics.Add(id), Labels = source.Labels.TryGetValue(id, out string? label) ? item.Labels.SetItem(id, label) : item.Labels } : item)
            .ToImmutableArray() };
        RefreshSectionSelector(); SelectEditorSection(target.Key); LoadControls();
        StudioStatus.Text = $"Joined this reading with {target.EditorLabel}. Save & apply to keep the layout.";
    }

    private void RemoveOverlayItem(object sender, RoutedEventArgs e)
    {
        if (draft.Sections.IsEmpty) return;
        string key = Selected.Key;
        draft = draft with { Sections = draft.Sections.Where(item => item.Key != key).ToImmutableArray() };
        LoadControls();
        StudioStatus.Text = "Item removed from the preview. Save & apply to keep the change.";
    }

    private void DuplicateOverlayItem()
    {
        if (draft.Sections.Length >= Preferences.MaxOverlayItems) { StudioStatus.Text = $"This layout has reached its {Preferences.MaxOverlayItems}-item limit."; return; }
        SectionStyle source = Selected;
        SectionStyle copy = source with { Id = Guid.NewGuid().ToString("N"), X = Math.Min(source.X + 0.02, 1), Y = Math.Min(source.Y + 0.02, 1) };
        draft = draft with { Sections = draft.Sections.Add(copy) };
        RefreshSectionSelector(); SelectEditorSection(copy.Key); LoadControls();
        StudioStatus.Text = "Item duplicated in the preview. Save & apply to keep the change.";
    }
}
