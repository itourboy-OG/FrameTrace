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
