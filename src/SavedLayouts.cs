using System.Collections.Immutable;
using System.IO;
using System.Text.Json;
using System.Windows;

namespace Frameglass;

public sealed record NamedLayout(string Name, Preferences Layout);

internal static class SavedLayouts
{
    public static string FilePath => Path.Combine(Path.GetDirectoryName(Preferences.FilePath)!, "saved-layouts.json");

    public static ImmutableArray<NamedLayout> Read(string path)
    {
        if (!File.Exists(path)) return [];
        ImmutableArray<NamedLayout> layouts = JsonSerializer.Deserialize<ImmutableArray<NamedLayout>>(File.ReadAllText(path), new JsonSerializerOptions { RespectRequiredConstructorParameters = true });
        if (layouts.IsDefault || layouts.Length > 100 || layouts.Any(item => item is null) || layouts.Select(item => item.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != layouts.Length)
            throw new InvalidDataException("Saved presets must contain at most 100 uniquely named layouts.");
        return layouts.Select(item =>
        {
            ValidateName(item.Name);
            return item with { Layout = Preferences.Parse(JsonSerializer.Serialize(item.Layout)) };
        }).ToImmutableArray();
    }

    public static ImmutableArray<NamedLayout> ReadPreview(string previewPath, string stablePath)
    {
        if (File.Exists(previewPath)) return Read(previewPath);
        ImmutableArray<NamedLayout> layouts = Read(stablePath);
        if (!layouts.IsEmpty) Write(layouts, previewPath);
        return layouts;
    }

    public static void Save(string name, Preferences layout, string path)
    {
        ValidateName(name); Preferences.Validate(layout);
        ImmutableArray<NamedLayout> existing = Read(path);
        ImmutableArray<NamedLayout> next = existing.Where(item => !string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase)).Append(new NamedLayout(name, layout))
            .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToImmutableArray();
        if (next.Length > 100) throw new InvalidDataException("You can save at most 100 custom presets. Reuse an existing name to update one.");
        Write(next, path);
    }

    public static Preferences Apply(Preferences current, NamedLayout saved) => Preferences.Validate(current with
    {
        Font = saved.Layout.Font, Opacity = saved.Layout.Opacity, OverlayScale = saved.Layout.OverlayScale, HideUnknownTechnology = saved.Layout.HideUnknownTechnology, Sections = saved.Layout.Sections
    });

    private static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 60 || name.Any(char.IsControl)) throw new InvalidDataException("Enter a preset name of 1–60 characters on one line.");
    }

    private static void Write(ImmutableArray<NamedLayout> layouts, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(layouts, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(path + ".tmp", path, true);
    }
}

public partial class MainWindow
{
    private void SaveCustomPreset(object sender, RoutedEventArgs e)
    {
        try
        {
            string name = CustomPresetName.Text.Trim();
            SavedLayouts.Save(name, draft, SavedLayouts.FilePath);
            CustomPresetSelector.ItemsSource = SavedLayouts.Read(SavedLayouts.FilePath);
            CustomPresetSelector.SelectedItem = CustomPresetSelector.Items.Cast<NamedLayout>().Single(item => item.Name == name);
            StudioStatus.Text = $"Saved preset ‘{name}’. Use Save & apply to activate it in-game. Saving this name again updates it.";
        }
        catch (Exception error) { Report("Custom preset was not saved", error); }
    }

    private void LoadCustomPreset(object sender, RoutedEventArgs e)
    {
        if (CustomPresetSelector.SelectedItem is not NamedLayout saved) return;
        try
        {
            draft = SavedLayouts.Apply(draft, saved); LoadControls(); CustomPresetName.Text = saved.Name;
            StudioStatus.Text = $"Loaded ‘{saved.Name}’ in preview. Save & apply to activate it in-game.";
        }
        catch (Exception error) { Report("Custom preset could not be loaded", error); }
    }
}
