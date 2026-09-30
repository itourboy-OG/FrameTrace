using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace FrameTrace;

public sealed record RtssSprite(string Name, string ImageData, double Width, double Height, BitmapSource Preview)
{
    public override string ToString() => Name;
}
public sealed record RtssAnimation(string Name, ImmutableArray<string> Frames, double Width, double Height, ArtworkAnimationSource Source, double Minimum, double Maximum, BitmapSource Preview)
{
    public override string ToString() => Name;
}
public sealed record RtssLiveGraph(string Name, int Width, int Height, ArtworkGraphSource Source)
{
    public override string ToString() => Name;
}
public sealed record RtssLiveBar(string Name, int Width, int Height, ArtworkBarSource Source, int Minimum, int Maximum)
{
    public override string ToString() => Name;
}
public sealed record RtssPlacement(string LayerName, string TableName, string Text, int X, int Y, int ExtentX, int ExtentY, int ExtentOrigin, int PrefixLines, int Size, string Color);
public sealed record RtssLayoutInfo(string FontFace, int FontHeight, double ZoomRatio, ImmutableArray<RtssPlacement> Placements, int TotalLayers, int PartialLayers);
public sealed record RtssArtwork(ImmutableArray<RtssSprite> Sprites, ImmutableArray<RtssAnimation> Animations, ImmutableArray<RtssLiveGraph> LiveGraphs, ImmutableArray<RtssLiveBar> LiveBars, int UnsupportedTables, RtssLayoutInfo? Layout);

public static class RtssArtworkImporter
{
    private static readonly Regex imageTag = new(@"<I=(\d+),(\d+),(\d+),(\d+),(\d+),(\d+)>", RegexOptions.CultureInvariant);
    private static readonly Regex animationTag = new(@"<AI=(Timer_01|Timer_02|GPU1 usage|CPU usage|RAM usage percent),(\d+),(\d+),(\d+),(\d+),(\d+),(\d+),(\d+),(\d+),(\d+),(\d+),0>", RegexOptions.CultureInvariant);
    private static readonly Regex graphTag = new(@"<G=(Frametime|GPU1 usage|CPU usage|RAM usage percent),(\d+),(\d+),\d+,\d+,\d+,0>", RegexOptions.CultureInvariant);
    private static readonly Regex barTag = new(@"<G=(GPU1 usage|CPU usage|RAM usage percent|GPU1 temperature|CPU temperature),(\d+),(\d+),\d+,(-?\d+),(-?\d+),\d+>", RegexOptions.CultureInvariant);
    private static readonly Regex tableTag = new(@"<TT=([^>]+)>", RegexOptions.CultureInvariant);

    public static RtssArtwork Read(string path)
    {
        FileInfo layout = new(path);
        if (!layout.Exists) throw new FileNotFoundException("The selected RTSS skin could not be found.", path);
        if (layout.Length > 2_000_000) throw new InvalidDataException($"RTSS skin exceeds the 2 MB limit: {path}");
        string? imageName = null;
        string? tableName = null, tableImage = null, tableSecondLine = null, tableCell = null, tableGraph = null;
        int tableCount = 0, emptyTableCount = 0;
        ImmutableArray<(string Name, string Image, string SecondLine, string Cell, string Graph)>.Builder tables = ImmutableArray.CreateBuilder<(string, string, string, string, string)>();
        string[] lines = File.ReadAllLines(path, Encoding.Latin1);
        foreach (string raw in lines)
        {
            string line = raw.Trim();
            if (line.StartsWith('['))
            {
                if (tableName is not null && tableImage is not null) tables.Add((tableName, tableImage, tableSecondLine ?? "", tableCell ?? "", tableGraph ?? ""));
                bool isTable = line.StartsWith("[Table", StringComparison.Ordinal);
                if (isTable) tableCount++;
                tableName = isTable ? "" : null;
                tableImage = null; tableSecondLine = null; tableCell = null; tableGraph = null;
            }
            else if (line.StartsWith("EmbeddedImage=", StringComparison.Ordinal)) imageName = line[14..];
            else if (tableName is not null && line == "Lines=0") emptyTableCount++;
            else if (tableName is not null && line.StartsWith("Name=", StringComparison.Ordinal)) tableName = line[5..].Replace("IMG\\", "").Trim('\u005c', ' ');
            else if (tableName is not null && line.StartsWith("Line0Name=", StringComparison.Ordinal)) tableImage = line[10..];
            else if (tableName is not null && line.StartsWith("Line1Name=", StringComparison.Ordinal)) tableSecondLine = line[10..];
            else if (tableName is not null && line.StartsWith("Line0Cell0Text=", StringComparison.Ordinal)) tableCell = line[15..];
            else if (tableName is not null && line.StartsWith("Line1Cell0Text=", StringComparison.Ordinal)) tableGraph = line[15..];
        }
        if (tableName is not null && tableImage is not null) tables.Add((tableName, tableImage, tableSecondLine ?? "", tableCell ?? "", tableGraph ?? ""));
        if (string.IsNullOrWhiteSpace(imageName) || imageName != Path.GetFileName(imageName) || !Path.GetExtension(imageName).Equals(".png", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("This RTSS skin does not name a safe companion PNG in EmbeddedImage.");
        string imagePath = Path.Combine(layout.DirectoryName ?? throw new InvalidDataException("Skin has no directory."), imageName);
        FileInfo atlasFile = new(imagePath);
        if (!atlasFile.Exists) throw new FileNotFoundException("The RTSS skin needs its companion PNG beside the .ovl file.", imagePath);
        if (atlasFile.Length > 8_000_000) throw new InvalidDataException($"RTSS companion PNG exceeds the 8 MB limit: {imagePath}");
        byte[] atlasBytes = File.ReadAllBytes(imagePath);
        if (atlasBytes.Length < 24 || !atlasBytes.AsSpan().StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) ||
            BinaryPrimitives.ReadInt32BigEndian(atlasBytes.AsSpan(16, 4)) is < 1 or > 4096 ||
            BinaryPrimitives.ReadInt32BigEndian(atlasBytes.AsSpan(20, 4)) is < 1 or > 4096)
            throw new InvalidDataException($"RTSS companion must be a PNG atlas no larger than 4096 × 4096: {imagePath}");
        using MemoryStream input = new(atlasBytes);
        BitmapDecoder decoder = BitmapDecoder.Create(input, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        BitmapSource atlas = decoder.Frames[0];
        ImmutableArray<RtssSprite>.Builder sprites = ImmutableArray.CreateBuilder<RtssSprite>();
        ImmutableArray<RtssAnimation>.Builder animations = ImmutableArray.CreateBuilder<RtssAnimation>();
        ImmutableArray<RtssLiveGraph>.Builder graphs = ImmutableArray.CreateBuilder<RtssLiveGraph>();
        ImmutableArray<RtssLiveBar>.Builder bars = ImmutableArray.CreateBuilder<RtssLiveBar>();
        int convertedTables = 0;
        foreach ((string name, string tag, string secondLine, string cell, string graph) in tables)
        {
            MatchCollection imageMatches = imageTag.Matches(tag + secondLine + cell);
            if (imageMatches.Count > 32) throw new InvalidDataException($"RTSS sprite '{name}' contains more than 32 image regions.");
            if (imageMatches.Count == 0)
            {
                Match animated = animationTag.Match(tag + secondLine);
                if (animated.Success)
                {
                    if (name.Length is < 1 or > 80 || name.Any(char.IsControl)) throw new InvalidDataException("An RTSS animation name must be a single line of at most 80 characters.");
                    int[] values = animated.Groups.Cast<Group>().Skip(2).Select(group => ParseNumber(group, name)).ToArray();
                    (int animationWidth, int animationHeight, int startX, int startY, int frameWidth, int frameHeight, int count, int perLine) = (values[0], values[1], values[4], values[5], values[6], values[7], values[8], values[9]);
                    if (animationWidth is < 8 or > 2048 || animationHeight is < 8 or > 2048 || count is < 2 or > 32 || perLine is < 1 or > 32 || frameWidth < 1 || frameHeight < 1 || startX < 0 || startY < 0)
                        throw new InvalidDataException($"Animation '{name}' has unsupported dimensions or frame count.");
                    ImmutableArray<string>.Builder frames = ImmutableArray.CreateBuilder<string>();
                    BitmapSource? preview = null;
                    for (int index = 0; index < count; index++)
                    {
                        int frameX = startX + index % perLine * frameWidth, frameY = startY + index / perLine * frameHeight;
                        if (frameX + frameWidth > atlas.PixelWidth || frameY + frameHeight > atlas.PixelHeight)
                            throw new InvalidDataException($"Animation '{name}' refers to pixels outside {imageName}.");
                        CroppedBitmap frameCrop = new(atlas, new Int32Rect(frameX, frameY, frameWidth, frameHeight));
                        preview ??= frameCrop;
                        frames.Add(Convert.ToBase64String(EncodePng(frameCrop)));
                    }
                    if (frames.Sum(frame => (long)frame.Length) > 4_000_000)
                        throw new InvalidDataException($"Animation '{name}' exceeds the 3 MB artwork limit.");
                    ArtworkAnimationSource source = animated.Groups[1].Value switch
                    {
                        "Timer_01" => ArtworkAnimationSource.GpuFan,
                        "GPU1 usage" => ArtworkAnimationSource.GpuUsage,
                        "CPU usage" => ArtworkAnimationSource.CpuUsage,
                        "RAM usage percent" => ArtworkAnimationSource.RamUsage,
                        _ => ArtworkAnimationSource.Loop
                    };
                    animations.Add(new RtssAnimation(name, frames.ToImmutable(), animationWidth, animationHeight, source, values[2], values[3], preview!));
                    convertedTables++;
                    continue;
                }
                Match barMatch = barTag.Match(tag + secondLine);
                if (barMatch.Success)
                {
                    if (name.Length is < 1 or > 80 || name.Any(char.IsControl)) throw new InvalidDataException("An RTSS live bar name must be a single line of at most 80 characters.");
                    int width = ParseNumber(barMatch.Groups[2], name), height = ParseNumber(barMatch.Groups[3], name);
                    if (!int.TryParse(barMatch.Groups[4].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int minimum) ||
                        !int.TryParse(barMatch.Groups[5].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int maximum) ||
                        width is < 1 or > 1200 || height is < 1 or > 400 || minimum >= maximum)
                        throw new InvalidDataException($"RTSS live bar '{name}' has unsupported dimensions or sensor range.");
                    ArtworkBarSource source = barMatch.Groups[1].Value switch
                    {
                        "GPU1 usage" => ArtworkBarSource.GpuUsage,
                        "CPU usage" => ArtworkBarSource.CpuUsage,
                        "RAM usage percent" => ArtworkBarSource.RamUsage,
                        "GPU1 temperature" => ArtworkBarSource.GpuTemperature,
                        "CPU temperature" => ArtworkBarSource.CpuTemperature,
                        _ => throw new InvalidDataException($"RTSS live bar '{name}' has an unknown sensor.")
                    };
                    bars.Add(new RtssLiveBar(name, width, height, source, minimum, maximum));
                    convertedTables++;
                    continue;
                }
                Match graphMatch = graphTag.Match(graph);
                if (graphMatch.Success)
                {
                    int graphWidth = ParseNumber(graphMatch.Groups[2], name), graphHeight = ParseNumber(graphMatch.Groups[3], name);
                    if (graphWidth is >= 120 and <= 1200 && graphHeight is >= 40 and <= 400)
                    {
                        ArtworkGraphSource source = graphMatch.Groups[1].Value switch
                        {
                            "GPU1 usage" => ArtworkGraphSource.GpuUsage,
                            "CPU usage" => ArtworkGraphSource.CpuUsage,
                            "RAM usage percent" => ArtworkGraphSource.RamUsage,
                            _ => ArtworkGraphSource.FrameTime
                        };
                        graphs.Add(new RtssLiveGraph(name, graphWidth, graphHeight, source));
                        convertedTables++;
                    }
                }
                continue;
            }
            if (name.Length is < 1 or > 80 || name.Any(char.IsControl)) throw new InvalidDataException("An RTSS sprite name must be a single line of at most 80 characters.");
            (int Width, int Height, CroppedBitmap Image)[] pieces = imageMatches.Cast<Match>().Select(match =>
            {
                int[] numbers = match.Groups.Cast<Group>().Skip(1).Select(group => ParseNumber(group, name)).ToArray();
                (int width, int height, int x, int y, int cropWidth, int cropHeight) = (numbers[0], numbers[1], numbers[2], numbers[3], numbers[4], numbers[5]);
                if (width is < 1 or > 2048 || height is < 1 or > 2048 || cropWidth < 1 || cropHeight < 1 || x < 0 || y < 0 || x + cropWidth > atlas.PixelWidth || y + cropHeight > atlas.PixelHeight)
                    throw new InvalidDataException($"Sprite '{name}' refers to pixels outside {imageName} or has an unsupported size.");
                return (width, height, new CroppedBitmap(atlas, new Int32Rect(x, y, cropWidth, cropHeight)));
            }).ToArray();
            int totalWidth = pieces.Sum(piece => piece.Width), totalHeight = pieces.Max(piece => piece.Height);
            if (pieces.Length > 1 && tag != string.Concat(imageMatches.Cast<Match>().Select(image => image.Value)))
            {
                for (int index = 0; index < pieces.Length; index++)
                {
                    (int width, int height, CroppedBitmap image) = pieces[index];
                    sprites.Add(CreateSprite(name[..Math.Min(name.Length, 65)] + $" · part {index + 1}", image, width, height));
                }
                convertedTables++;
                continue;
            }
            if (totalWidth > 2048) throw new InvalidDataException($"Sprite '{name}' exceeds the 2048-pixel artwork width limit.");
            BitmapSource artworkPreview = pieces[0].Image;
            if (pieces.Length > 1)
            {
                DrawingVisual visual = new();
                using (DrawingContext drawing = visual.RenderOpen())
                {
                    int left = 0;
                    foreach ((int width, int height, CroppedBitmap image) in pieces)
                    {
                        drawing.DrawImage(image, new Rect(left, 0, width, height));
                        left += width;
                    }
                }
                RenderTargetBitmap combined = new(totalWidth, totalHeight, 96, 96, PixelFormats.Pbgra32);
                combined.Render(visual);
                combined.Freeze();
                artworkPreview = combined;
            }
            sprites.Add(CreateSprite(name, artworkPreview, totalWidth, totalHeight));
            convertedTables++;
        }
        RtssSprite? normalFlame = sprites.FirstOrDefault(sprite => sprite.Name.Contains("AlarmFire_01", StringComparison.OrdinalIgnoreCase));
        RtssSprite? alarmFlame = sprites.FirstOrDefault(sprite => sprite.Name.Contains("AlarmFire_02", StringComparison.OrdinalIgnoreCase));
        if (normalFlame is not null && alarmFlame is not null)
        {
            ImmutableArray<string> frames = [normalFlame.ImageData, alarmFlame.ImageData];
            animations.Add(new RtssAnimation("GPU temperature alarm · 70°C", frames, normalFlame.Width, normalFlame.Height, ArtworkAnimationSource.GpuTemperatureAlarm, 69, 70, normalFlame.Preview));
            animations.Add(new RtssAnimation("CPU temperature alarm · 85°C", frames, normalFlame.Width, normalFlame.Height, ArtworkAnimationSource.CpuTemperatureAlarm, 84, 85, normalFlame.Preview));
        }
        return new RtssArtwork(sprites.ToImmutable(), animations.ToImmutable(), graphs.ToImmutable(), bars.ToImmutable(), tableCount - emptyTableCount - convertedTables, ReadLayout(lines));
    }

    private static RtssLayoutInfo? ReadLayout(string[] lines)
    {
        string section = "", fontFace = "";
        int fontHeight = 0, totalLayers = 0, partialLayers = 0;
        double zoomRatio = 1;
        string? layerName = null, layerText = null;
        int? x = null, y = null;
        int extentX = 0, extentY = 0, extentOrigin = 0;
        int size = 100;
        string color = "FFFFFF";
        bool sticky = false;
        ImmutableArray<RtssPlacement>.Builder placements = ImmutableArray.CreateBuilder<RtssPlacement>();
        void AddLayer()
        {
            if (!section.StartsWith("[Layer", StringComparison.Ordinal)) return;
            totalLayers++;
            if (sticky || layerText is null || x is null || y is null) return;
            MatchCollection references = tableTag.Matches(layerText);
            if (references.Count == 0 && (layerName is null || !layerName.Contains("Text_", StringComparison.Ordinal))) return;
            if (references.Count > 1) partialLayers++;
            string table = references.Count == 0 ? "" : references[0].Groups[1].Value.Replace("IMG\\", "", StringComparison.Ordinal).Trim('\\', ' ');
            int prefixLines = references.Count == 0
                ? Regex.Match(layerText, @"^(?:\n|\\n)*", RegexOptions.CultureInvariant).Value.Split("\\n", StringSplitOptions.None).Length - 1
                : layerText[..references[0].Index].Split("\\n", StringSplitOptions.None).Length - 1;
            placements.Add(new RtssPlacement(string.IsNullOrWhiteSpace(layerName) ? table : layerName, table, layerText, x.Value, y.Value, extentX, extentY, extentOrigin, prefixLines, size, color));
        }
        foreach (string raw in lines)
        {
            string line = raw.Trim();
            if (line.StartsWith('['))
            {
                AddLayer();
                section = line;
                layerName = null; layerText = null; x = null; y = null; extentX = 0; extentY = 0; extentOrigin = 0; sticky = false; size = 100; color = "FFFFFF";
            }
            else if (section == "[Master]" && line.StartsWith("FontFace=", StringComparison.Ordinal)) fontFace = line[9..];
            else if (section == "[Master]" && line.StartsWith("FontHeight=", StringComparison.Ordinal))
            {
                if (!int.TryParse(line[11..], NumberStyles.Integer, CultureInfo.InvariantCulture, out fontHeight) || fontHeight is < -128 or > 128 or 0)
                    throw new InvalidDataException($"RTSS FontHeight must be between -128 and 128, excluding zero: {line}");
            }
            else if (section == "[Master]" && line.StartsWith("ZoomRatio=", StringComparison.Ordinal))
            {
                if (!double.TryParse(line[10..], NumberStyles.Float, CultureInfo.InvariantCulture, out zoomRatio) || !double.IsFinite(zoomRatio) || zoomRatio is < 0.5 or > 4)
                    throw new InvalidDataException($"RTSS ZoomRatio must be between 0.5 and 4: {line}");
            }
            else if (section.StartsWith("[Layer", StringComparison.Ordinal) && line.StartsWith("Name=", StringComparison.Ordinal)) layerName = line[5..];
            else if (section.StartsWith("[Layer", StringComparison.Ordinal) && line.StartsWith("Text=", StringComparison.Ordinal)) layerText = line[5..];
            else if (section.StartsWith("[Layer", StringComparison.Ordinal) && line.StartsWith("PositionX=", StringComparison.Ordinal)) x = ParsePosition(line[10..], section);
            else if (section.StartsWith("[Layer", StringComparison.Ordinal) && line.StartsWith("PositionY=", StringComparison.Ordinal)) y = ParsePosition(line[10..], section);
            else if (section.StartsWith("[Layer", StringComparison.Ordinal) && line.StartsWith("ExtentX=", StringComparison.Ordinal)) extentX = ParsePosition(line[8..], section);
            else if (section.StartsWith("[Layer", StringComparison.Ordinal) && line.StartsWith("ExtentY=", StringComparison.Ordinal)) extentY = ParsePosition(line[8..], section);
            else if (section.StartsWith("[Layer", StringComparison.Ordinal) && line.StartsWith("ExtentOrigin=", StringComparison.Ordinal))
            {
                extentOrigin = ParsePosition(line[13..], section);
                if (extentOrigin is < 0 or > 8) throw new InvalidDataException($"RTSS {section} has an extent origin outside 0–8: {line}");
            }
            else if (section.StartsWith("[Layer", StringComparison.Ordinal) && line.StartsWith("PositionSticky=", StringComparison.Ordinal)) sticky = ParsePosition(line[15..], section) >= 0;
            else if (section.StartsWith("[Layer", StringComparison.Ordinal) && line.StartsWith("Size=", StringComparison.Ordinal))
            {
                if (!int.TryParse(line[5..], NumberStyles.Integer, CultureInfo.InvariantCulture, out size) || size is < 0 or > 300)
                    throw new InvalidDataException($"RTSS {section} has an invalid text size: {line}");
            }
            else if (section.StartsWith("[Layer", StringComparison.Ordinal) && line.StartsWith("TextColor=", StringComparison.Ordinal))
            {
                MatchCollection colors = Regex.Matches(line[10..], @"(?<![0-9A-Fa-f])[0-9A-Fa-f]{6,8}(?![0-9A-Fa-f])", RegexOptions.CultureInvariant);
                if (colors.Count == 0)
                    throw new InvalidDataException($"RTSS {section} has an invalid text color: {line}");
                color = colors[colors.Count - 1].Value;
            }
        }
        AddLayer();
        if (fontFace.Length is < 1 or > 80 || fontFace.Any(char.IsControl) || fontHeight == 0 || placements.Count == 0) return null;
        return new RtssLayoutInfo(fontFace, fontHeight, zoomRatio, placements.ToImmutable(), totalLayers, partialLayers);
    }

    private static int ParsePosition(string text, string section)
    {
        if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) && value is >= -4096 and <= 4096) return value;
        throw new InvalidDataException($"RTSS {section} contains a position outside -4096 to 4096: {text}");
    }

    private static RtssSprite CreateSprite(string name, BitmapSource preview, int width, int height)
    {
        byte[] png = EncodePng(preview);
        if (png.Length > 3_000_000) throw new InvalidDataException($"Sprite '{name}' exceeds the 3 MB artwork limit.");
        return new RtssSprite(name, Convert.ToBase64String(png), width, height, preview);
    }

    private static byte[] EncodePng(BitmapSource image)
    {
        PngBitmapEncoder encoder = new(); encoder.Frames.Add(BitmapFrame.Create(image));
        using MemoryStream output = new(); encoder.Save(output);
        return output.ToArray();
    }

    private static int ParseNumber(Group group, string name)
    {
        if (int.TryParse(group.Value, NumberStyles.None, CultureInfo.InvariantCulture, out int value)) return value;
        throw new InvalidDataException($"RTSS table '{name}' contains an invalid or oversized numeric field: {group.Value}");
    }
}
