using System.Collections.Immutable;

using System.Globalization;

using System.IO;

using System.Text.Json;
using System.Text.Json.Serialization;

using System.Text.RegularExpressions;



namespace FrameTrace;



public enum MetricLayout { Flow, Table, Tiles }

public enum SectionKind { Frames, Gpu, Cpu, Ram
    , Artwork, Game
}
public enum ArtworkAnimationSource { Loop, GpuFan, GpuUsage, CpuUsage, RamUsage, GpuTemperatureAlarm, CpuTemperatureAlarm }
public enum ArtworkGraphSource { FrameTime, GpuUsage, CpuUsage, RamUsage }
public enum ArtworkBarSource { None, GpuUsage, CpuUsage, RamUsage, GpuTemperature, CpuTemperature }
public enum RtssTextSource { None, FrameRate, GameInfo, Clock, GpuUsage, GpuClock, GpuVram, GpuTemperature, GpuName, GpuPower, CpuUsage, CpuTemperature, CpuName, CpuPower, RamUsage, RamUsed }

public sealed record Hotkey(uint Modifiers, int Key);

public sealed record SectionStyle(SectionKind Kind, string Name, string NameColor, string ValueColor, double NameSize, double ValueSize, double X, double Y, bool Visible, ImmutableArray<string> Metrics)

{

    public string Id { get; init; } = "";
    [JsonIgnore] public string Key => string.IsNullOrEmpty(Id) ? Kind.ToString() : Id;
    [JsonIgnore] public string EditorLabel => string.IsNullOrWhiteSpace(Name) ? Kind.ToString() : Name;
    public double LabelSize { get; init; } = 0;

    public ImmutableDictionary<string, string> Labels { get; init; } = ImmutableDictionary<string, string>.Empty;

    public MetricLayout Layout { get; init; } = MetricLayout.Flow;
    public double Padding { get; init; } = 10;
    public double HeroSize { get; init; } = 0;
    public bool GraphBelow { get; init; } = false;
    public bool TextShadow { get; init; } = false;

    public bool ShowName { get; init; } = true;

    public bool Horizontal { get; init; } = false;

    public bool Graph { get; init; } = false;
    public bool GraphTransparent { get; init; }
    public ArtworkGraphSource GraphSource { get; init; }

    public double GraphWidth { get; init; } = 320;

    public double GraphHeight { get; init; } = 90;
    public bool ThemeCard { get; init; }
    public bool UsageGauge { get; init; }
    public bool UsageBar { get; init; }
    public double UsageBarWidth { get; init; } = 220;
    public bool FanWidget { get; init; }
    public double FanSize { get; init; } = 36;
    public string ImageData { get; init; } = "";
    public ImmutableArray<string> AnimationFrames { get; init; } = [];
    public ArtworkAnimationSource AnimationSource { get; init; }
    public int AnimationIntervalMs { get; init; } = 250;
    public double AnimationMinimum { get; init; }
    public double AnimationMaximum { get; init; } = 100;
    public bool AlarmThresholdCustomized { get; init; }
    public ArtworkBarSource BarSource { get; init; }
    public double BarMinimum { get; init; }
    public double BarMaximum { get; init; } = 100;
    public double ImageWidth { get; init; } = 240;
    public double ImageHeight { get; init; } = 120;
    public bool ArtworkFill { get; init; }
    public double ArtworkOpacity { get; init; } = 1;
    public double ArtworkRadius { get; init; }
    public RtssTextSource RtssTextSource { get; init; }
    public string RtssFontFace { get; init; } = "";

}

public sealed record Preferences(int SchemaVersion, string Accent, double Opacity, string Font, Hotkey Shortcut, bool OverlayEnabled, ImmutableArray<string> IgnoredApps, ImmutableArray<SectionStyle> Sections)

{

    public const int MaxOverlayItems = 96;

    public double OverlayScale { get; init; } = 1;
    public bool HideUnknownTechnology { get; init; } = true;
    public int SensorRefreshMs { get; init; } = 1000;
    public bool StartMinimized { get; init; }
    public bool RunAtLogin { get; init; }
    public bool ReduceMotion { get; init; }
    public bool CheckUpdatesOnStartup { get; init; } = true;

    public static string FilePath => Path.Combine(AppIdentity.DataDirectory, "preferences.json");

    public static Preferences Initial => new(4, "#83EFCD", 0.65, "Consolas", new Hotkey(6, 0x4F), true,

        ["chrome.exe", "msedge.exe", "firefox.exe", "brave.exe", "opera.exe", "discord.exe", "steam.exe", "steamwebhelper.exe", "EpicGamesLauncher.exe", "EpicWebHelper.exe", "explorer.exe", "dwm.exe", "ApplicationFrameHost.exe", "ShellExperienceHost.exe", "SearchHost.exe", "StartMenuExperienceHost.exe", "Codex.exe", "Code.exe"],

        [new(SectionKind.Frames, "FRAMES", "#83EFCD", "#FFFFFF", 14, 30, 0.02, 0.025, true, ["app", "display", "average", "low", "frametime"]),

         new(SectionKind.Gpu, "", "#FF7777", "#F1F4F8", 16, 16, 0.02, 0.27, true, ["usage", "temperature", "power", "clock", "vram"]),

         new(SectionKind.Cpu, "", "#FFBA77", "#F1F4F8", 16, 16, 0.02, 0.51, true, ["usage", "temperature", "power"]),

         new(SectionKind.Ram, "RAM", "#80BDFF", "#F1F4F8", 14, 20, 0.02, 0.72, true, ["used"])]);



    public static ImmutableArray<string> AvailableMetrics(SectionKind kind) => kind switch

    {

        SectionKind.Frames => ["app", "display", "average", "low", "frametime", "generation", "upscaler"],

        SectionKind.Gpu => ["usage", "temperature", "power", "clock", "vram"],

        SectionKind.Cpu => ["usage", "temperature", "power"],

        SectionKind.Ram => ["used"],
        SectionKind.Artwork => [],
        SectionKind.Game => ["game", "api"],

        _ => throw new ArgumentOutOfRangeException(nameof(kind))

    };



    public static void ValidateColor(string color)

    {

        if (color is null || !Regex.IsMatch(color, "^#[0-9a-fA-F]{6}$")) throw new InvalidDataException("A saved color must use #RRGGBB format.");

    }



    public static Preferences Validate(Preferences value)

    {

        if (value.SchemaVersion != 4) throw new InvalidDataException($"Unsupported layout version {value.SchemaVersion}. Expected version 4.");

        ValidateColor(value.Accent);

        if (!double.IsFinite(value.OverlayScale) || value.OverlayScale is < 0.5 or > 3) throw new InvalidDataException("Overlay size must be between 50% and 300%.");
        if (value.SensorRefreshMs is < 250 or > 3000 || value.SensorRefreshMs % 250 != 0) throw new InvalidDataException("Sensor refresh interval must be 250–3000 milliseconds in 250 millisecond steps.");

        if (!double.IsFinite(value.Opacity) || value.Opacity is < 0 or > 1) throw new InvalidDataException("Panel opacity must be between 0% and 100%.");

        if (value.Font is not ("Consolas" or "Segoe UI" or "Arial" or "Cascadia Mono")) throw new InvalidDataException("Select a supported overlay font.");

        if (value.Shortcut is null || value.Shortcut.Modifiers is < 1 or > 7 || value.Shortcut.Key is < 0x30 or > 0xFE)

            throw new InvalidDataException("Choose a shortcut with Ctrl, Alt, or Shift plus a letter, number, or other key.");

        if (value.IgnoredApps.IsDefault || value.IgnoredApps.Any(name => string.IsNullOrWhiteSpace(name) || name != Path.GetFileName(name) || !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)))

            throw new InvalidDataException("Ignored applications must be executable filenames, one per line.");

        if (value.Sections.IsDefault || value.Sections.Length > MaxOverlayItems || value.Sections.Any(section => section is null) || value.Sections.Select(section => section.Key).Distinct().Count() != value.Sections.Length)

            throw new InvalidDataException($"A layout must contain at most {MaxOverlayItems} uniquely identified overlay items.");

        foreach (SectionStyle section in value.Sections)

        {

            if (!Enum.IsDefined(section.Kind) || section.Id is null || (section.Id.Length != 0 && !Guid.TryParseExact(section.Id, "N", out _))) throw new InvalidDataException("Invalid overlay item identity.");
            if (!double.IsFinite(section.LabelSize) || (section.LabelSize != 0 && section.LabelSize is < 8 or > 96)) throw new InvalidDataException("Label size must be automatic or 8–96.");
            if (section.Labels is null || section.Labels.Any(pair => !AvailableMetrics(section.Kind).Contains(pair.Key) || pair.Value is null || pair.Value.Length > 80 || pair.Value.Any(char.IsControl)))

                throw new InvalidDataException("Metric labels must be single-line text of at most 80 characters.");

            if (!double.IsFinite(section.GraphWidth) || !double.IsFinite(section.GraphHeight) || section.GraphWidth is < 120 or > 1200 || section.GraphHeight is < 40 or > 400)

                throw new InvalidDataException("Graph size must be 120–1200 wide and 40–400 high.");
            if (!Enum.IsDefined(section.GraphSource)) throw new InvalidDataException("Select a supported live graph source.");
            if (section.Graph && ((section.GraphSource == ArtworkGraphSource.GpuUsage && section.Kind != SectionKind.Gpu) || (section.GraphSource == ArtworkGraphSource.CpuUsage && section.Kind != SectionKind.Cpu) || (section.GraphSource == ArtworkGraphSource.RamUsage && section.Kind != SectionKind.Ram)))
                throw new InvalidDataException("Hardware-load graphs require their matching GPU, CPU, or RAM item.");
            if (section.UsageGauge && section.Kind is not (SectionKind.Gpu or SectionKind.Cpu))
                throw new InvalidDataException("Live usage gauges require a GPU or CPU item.");
            if (!double.IsFinite(section.UsageBarWidth) || section.UsageBarWidth is < 80 or > 500 || section.UsageBar && section.Kind is not (SectionKind.Gpu or SectionKind.Cpu))
                throw new InvalidDataException("Live usage bars require a GPU or CPU item and a width of 80–500.");
            if (!double.IsFinite(section.FanSize) || section.FanSize is < 24 or > 80 || section.FanWidget && section.Kind != SectionKind.Gpu)
                throw new InvalidDataException("Animated fans require a GPU item and a size of 24–80.");
            if (section.Kind == SectionKind.Artwork && (section.ImageData is null || section.ImageData.Length > 4_000_000 || !double.IsFinite(section.ImageWidth) || !double.IsFinite(section.ImageHeight) || section.ImageWidth is < 1 or > 2048 || section.ImageHeight is < 1 or > 2048))
                throw new InvalidDataException("Artwork must be a valid image no larger than 3 MB and 1–2048 pixels in each dimension.");
            if (section.AnimationFrames.IsDefault || section.AnimationFrames.Length > 32 || section.AnimationFrames.Any(frame => frame is null) || section.Kind != SectionKind.Artwork && !section.AnimationFrames.IsEmpty || !Enum.IsDefined(section.AnimationSource) || section.AnimationSource != ArtworkAnimationSource.Loop && section.AnimationFrames.IsEmpty)
                throw new InvalidDataException("Animated artwork needs 2–32 frames in an artwork item.");
            if (!double.IsFinite(section.AnimationMinimum) || !double.IsFinite(section.AnimationMaximum) || section.AnimationMinimum >= section.AnimationMaximum)
                throw new InvalidDataException("Animated artwork needs a finite sensor range with a minimum below its maximum.");
            if (section.AnimationIntervalMs is < 50 or > 1000)
                throw new InvalidDataException("Looping artwork must use a 50–1000 millisecond frame interval.");
            if (!Enum.IsDefined(section.BarSource) || !double.IsFinite(section.BarMinimum) || !double.IsFinite(section.BarMaximum) || section.BarMinimum >= section.BarMaximum || section.BarSource != ArtworkBarSource.None && (section.Kind != SectionKind.Artwork || section.ImageData.Length > 0 || !section.AnimationFrames.IsEmpty || section.ArtworkFill || section.ShowName))
                throw new InvalidDataException("Live bars need an artwork item with a supported sensor range and no other artwork content.");
            if (!Enum.IsDefined(section.RtssTextSource) || section.RtssTextSource != RtssTextSource.None && (section.Kind != SectionKind.Artwork || section.ImageData.Length > 0 || !section.AnimationFrames.IsEmpty || section.ArtworkFill || section.BarSource != ArtworkBarSource.None))
                throw new InvalidDataException("An RTSS text field must be an artwork item without an image, animation, or bar.");
            if (section.RtssFontFace is null || section.RtssFontFace.Length > 80 || section.RtssFontFace.Any(char.IsControl))
                throw new InvalidDataException("An imported font name must be a single line of at most 80 characters.");
            if (!section.AnimationFrames.IsEmpty)
            {
                if (section.AnimationFrames.Length < 2 || section.ImageData.Length > 0 || section.ArtworkFill || section.ShowName || section.AnimationFrames.Sum(frame => (long)frame.Length) > 4_000_000)
                    throw new InvalidDataException("Animated artwork must contain 2–32 PNG frames smaller than 3 MB total, with no other artwork content.");
                foreach (string frame in section.AnimationFrames)
                {
                    byte[] bytes;
                    try { bytes = Convert.FromBase64String(frame); }
                    catch (FormatException error) { throw new InvalidDataException("An animated artwork frame is not valid Base64 PNG data.", error); }
                    if (!bytes.AsSpan().StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
                        throw new InvalidDataException("Animated artwork frames must be PNG images.");
                }
            }
            if (section.Kind == SectionKind.Artwork && (!double.IsFinite(section.ArtworkOpacity) || section.ArtworkOpacity is < 0 or > 1 || !double.IsFinite(section.ArtworkRadius) || section.ArtworkRadius is < 0 or > 100 || section.ArtworkFill && section.ImageData.Length > 0))
                throw new InvalidDataException("Artwork opacity must be 0–100%, corner radius 0–100, and a color panel cannot also contain an image.");
            if (section.Kind == SectionKind.Artwork && section.ImageData.Length > 0)
            {
                byte[] image;
                try { image = Convert.FromBase64String(section.ImageData); }
                catch (FormatException error) { throw new InvalidDataException("Artwork image data is not valid Base64 PNG data.", error); }
                if (image.Length > 3_000_000 || !image.AsSpan().StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
                    throw new InvalidDataException("Artwork must contain a PNG image smaller than 3 MB.");
            }
            if (section.Kind != SectionKind.Artwork && !string.IsNullOrEmpty(section.ImageData))
                throw new InvalidDataException("Only artwork items may contain image data.");

            if (!Enum.IsDefined(section.Layout) || !double.IsFinite(section.Padding) || section.Padding is < 0 or > 24 || !double.IsFinite(section.HeroSize) || section.HeroSize is < 0 or > 96)
                throw new InvalidDataException("Select a valid metric layout, padding (0–24), and FPS emphasis size (0–96).");
            ValidateColor(section.NameColor); ValidateColor(section.ValueColor);

            if (!Enum.IsDefined(section.Kind) || section.Name is null || section.Name.Length > 80 || section.Name.Any(char.IsControl)) throw new InvalidDataException("Section names must be a single line of at most 80 characters.");

            if (!double.IsFinite(section.NameSize) || !double.IsFinite(section.ValueSize) || section.NameSize is < 10 or > 48 || section.ValueSize is < 10 or > 64) throw new InvalidDataException("Title size must be 10–48 and value size 10–64.");

            if (!double.IsFinite(section.X) || !double.IsFinite(section.Y) || section.X is < 0 or > 1 || section.Y is < 0 or > 1) throw new InvalidDataException("Sections must be positioned within the screen.");

            if (section.Metrics.IsDefault || section.Metrics.Distinct().Count() != section.Metrics.Length || section.Metrics.Except(AvailableMetrics(section.Kind)).Any())

                throw new InvalidDataException($"Select valid, non-duplicate metrics for {section.Kind}.");

        }

        return value;

    }



    public static Preferences Parse(string json)

    {

        using JsonDocument document = JsonDocument.Parse(json);

        JsonSerializerOptions options = new() { RespectRequiredConstructorParameters = true };

        if (document.RootElement.TryGetProperty("SchemaVersion", out _))

        {

            Preferences parsed = JsonSerializer.Deserialize<Preferences>(json, options) ?? throw new InvalidDataException("Layout JSON is empty.");

            Preferences current = Validate(parsed.SchemaVersion is 2 or 3 ? parsed with { SchemaVersion = 4 } : parsed);
            return current with { Sections = current.Sections.Select(section => section.AnimationSource == ArtworkAnimationSource.CpuTemperatureAlarm && section.AnimationMinimum == 69 && section.AnimationMaximum == 70 && !section.AlarmThresholdCustomized
                ? section with { AnimationMinimum = 84, AnimationMaximum = 85, Name = section.Name == "CPU temperature alarm · 70°C" ? "CPU temperature alarm · 85°C" : section.Name }
                : section).ToImmutableArray() };

        }

        // Version 0.1 preferences are migrated explicitly; corrupt files never silently reset.

        LegacyPreferences old = JsonSerializer.Deserialize<LegacyPreferences>(json, options) ?? throw new InvalidDataException("Legacy settings are empty.");

        ValidateColor(old.Accent);

        if (!double.IsFinite(old.TextSize) || old.TextSize is < 12 or > 26 || old.Corner is < 0 or > 3) throw new InvalidDataException("Invalid legacy overlay size or corner.");

        Preferences initial = Initial;

        return Validate(initial with { Accent = old.Accent, Opacity = old.Opacity, Sections = initial.Sections.Select(section => section with

        {

            ValueSize = old.TextSize, NameColor = old.Accent,

            X = old.Corner % 2 == 0 ? 0.02 : 0.98,

            Y = old.Corner < 2 ? section.Y : 1 - section.Y,

            Visible = section.Kind switch { SectionKind.Frames => old.Frames, SectionKind.Gpu => old.Gpu, SectionKind.Cpu => old.Cpu, SectionKind.Ram => old.Memory, _ => false }

        }).ToImmutableArray() });

    }



    public static Preferences Load() => File.Exists(FilePath) ? Parse(File.ReadAllText(FilePath)) : Initial;

    public static void Save(Preferences value) => Write(value, FilePath);

    public static Preferences ForLayoutExport(Preferences current) => Validate(Initial with
    {
        Accent = current.Accent, Opacity = current.Opacity, Font = current.Font,
        OverlayScale = current.OverlayScale, HideUnknownTechnology = current.HideUnknownTechnology, Sections = current.Sections
    });

    public static bool HasLayoutChanges(Preferences current, Preferences baseline) =>
        JsonSerializer.Serialize(ForLayoutExport(current)) != JsonSerializer.Serialize(ForLayoutExport(baseline));

    public static Preferences ApplyImportedLayout(Preferences current, Preferences imported) => Validate(imported with
    {
        Shortcut = current.Shortcut, OverlayEnabled = current.OverlayEnabled, IgnoredApps = current.IgnoredApps,
        SensorRefreshMs = current.SensorRefreshMs, StartMinimized = current.StartMinimized, RunAtLogin = current.RunAtLogin,
        ReduceMotion = current.ReduceMotion, CheckUpdatesOnStartup = current.CheckUpdatesOnStartup
    });

    public static void Write(Preferences value, string path)

    {

        Validate(value);

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);

        string temporary = path + ".tmp";

        File.WriteAllText(temporary, JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));

        File.Move(temporary, path, true);

    }



    private sealed record LegacyPreferences(string Accent, double TextSize, double Opacity, int Corner, bool Frames, bool Gpu, bool Cpu, bool Memory, string Target);

}



public static class Readings

{

    public static SensorReading? Find(ImmutableArray<SensorReading> sensors, string hardware, string kind, string name) =>

        sensors.FirstOrDefault(sensor => sensor.HardwareType.StartsWith(hardware, StringComparison.Ordinal) && sensor.Kind == kind && sensor.Name == name && (hardware != "Memory" || sensor.Device == "Total Memory"));

    public static string Format(double? value, string unit) => value.HasValue ? $"{value.Value.ToString("0.0", CultureInfo.InvariantCulture)} {unit}" : "Unavailable";

    public static string Sensor(ImmutableArray<SensorReading> sensors, string hardware, string kind, string name)

    {

        SensorReading? reading = Find(sensors, hardware, kind, name);

        return Format(reading?.Value, reading?.Unit ?? "");

    }

    public static string Memory(ImmutableArray<SensorReading> sensors, string name) => Format(Find(sensors, "Gpu", "SmallData", name)?.Value / 1024, "GB");

}

