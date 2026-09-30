using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Text.RegularExpressions;

namespace FrameTrace;

public sealed record RtssLayoutResult(ImmutableArray<SectionStyle> Sections, int PlacedLayers, int SkippedLayers, int PartialLayers);

public static class RtssLayout
{
    public static RtssLayoutResult Build(RtssArtwork skin, Size canvas)
    {
        RtssLayoutInfo layout = skin.Layout ?? throw new InvalidDataException("This RTSS skin does not include layer positions and font settings. Add its artwork manually.");
        if (!double.IsFinite(canvas.Width) || !double.IsFinite(canvas.Height) || canvas.Width <= 0 || canvas.Height <= 0)
            throw new ArgumentOutOfRangeException(nameof(canvas), "Select a display with a valid canvas size before placing an RTSS skin.");
        double fontSize = Math.Abs(layout.FontHeight) * layout.ZoomRatio;
        FormattedText character = new("0", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface(layout.FontFace), fontSize, Brushes.White, 1);
        double charWidth = Math.Max(character.WidthIncludingTrailingWhitespace, fontSize * 0.64), charHeight = fontSize * 1.12;
        ImmutableArray<SectionStyle>.Builder items = ImmutableArray.CreateBuilder<SectionStyle>();
        int placed = 0, skipped = layout.TotalLayers - layout.Placements.Length;
        foreach (RtssPlacement layer in layout.Placements)
        {
            RtssSprite[] pieces = skin.Sprites.Where(sprite => sprite.Name == layer.TableName || sprite.Name.StartsWith(layer.TableName + " · part ", StringComparison.Ordinal)).ToArray();
            SectionStyle[] sections = layer.TableName == "Background_02" && layer.Text.Contains("<TT=IMG\\\\  Background_03>", StringComparison.Ordinal)
                ? [Background(skin, layer, charHeight)]
                : layer.TableName.Length == 0
                ? TextLayer(layout, layer)
                : pieces.Length > 0
                ? pieces.Select(sprite => Sprite(sprite, layer.LayerName)).ToArray()
                : FindOther(skin, layer);
            if (sections.Length == 0) { skipped++; continue; }
            double x = layer.X < 0 ? -layer.X * charWidth : layer.X * layout.ZoomRatio;
            if (layer.TableName.Length == 0)
                x += Regex.Match(layer.Text, @"( +)(?=%[A-Za-z])", RegexOptions.CultureInvariant).Groups[1].Length * charWidth * 0.6;
            double y = (layer.Y < 0 ? -layer.Y * charHeight : layer.Y * layout.ZoomRatio) + layer.PrefixLines * charHeight;
            if (layer.ExtentX != 0 && layer.ExtentY != 0)
            {
                double extentWidth = Math.Abs(layer.ExtentX) * (layer.ExtentX < 0 ? charWidth : layout.ZoomRatio);
                double extentHeight = Math.Abs(layer.ExtentY) * (layer.ExtentY < 0 ? charHeight : layout.ZoomRatio);
                double itemWidth = sections.Sum(section => section.ImageWidth);
                double itemHeight = sections.Max(section => section.ImageHeight);
                if (extentWidth >= itemWidth) x += (extentWidth - itemWidth) * (layer.ExtentOrigin / 3) / 2;
                if (extentHeight >= itemHeight) y += (extentHeight - itemHeight) * (layer.ExtentOrigin % 3) / 2;
            }
            bool fits = sections.Aggregate(x, (left, section) => left + section.ImageWidth) <= canvas.Width
                && y + sections.Max(section => section.ImageHeight) <= canvas.Height && x >= 0 && y >= 0;
            if (!fits) { skipped++; continue; }
            foreach (SectionStyle section in sections)
            {
                if (items.Count == Preferences.MaxOverlayItems) throw new InvalidDataException($"This skin has more than {Preferences.MaxOverlayItems} supported layers. Remove layers in RTSS and import it again.");
                items.Add(section with
                {
                    X = x / Math.Max(1, canvas.Width - section.ImageWidth),
                    Y = y / Math.Max(1, canvas.Height - section.ImageHeight)
                });
                x += section.ImageWidth;
            }
            placed++;
        }
        if (items.Count == 0) throw new InvalidDataException("No supported RTSS layers fit on the selected display. Select a larger display or add the artwork manually.");
        return new RtssLayoutResult(items.ToImmutable(), placed, skipped, layout.PartialLayers);
    }

    private static SectionStyle Background(RtssArtwork skin, RtssPlacement layer, double charHeight)
    {
        Match rows = Regex.Match(layer.Text, @"<B=\d+,-(\d+)>", RegexOptions.CultureInvariant);
        if (!rows.Success) throw new InvalidDataException($"RTSS background '{layer.LayerName}' has no supported middle height.");
        RtssSprite top = skin.Sprites.Single(sprite => sprite.Name == "Background_02");
        RtssSprite bottom = skin.Sprites.Single(sprite => sprite.Name == "Background_03");
        if (top.Width != bottom.Width) throw new InvalidDataException($"RTSS background '{layer.LayerName}' has mismatched top and bottom widths.");
        double middleHeight = int.Parse(rows.Groups[1].Value, CultureInfo.InvariantCulture) * charHeight;
        int height = (int)Math.Ceiling(top.Height + middleHeight + bottom.Height);
        DrawingVisual visual = new();
        using (DrawingContext drawing = visual.RenderOpen())
        {
            drawing.DrawImage(top.Preview, new Rect(0, 0, top.Width, top.Height));
            drawing.DrawRectangle(new SolidColorBrush(Color.FromArgb(225, 0, 0, 0)), null, new Rect(0, top.Height, top.Width, middleHeight));
            drawing.DrawImage(bottom.Preview, new Rect(0, top.Height + middleHeight, bottom.Width, bottom.Height));
        }
        RenderTargetBitmap image = new((int)top.Width, height, 96, 96, PixelFormats.Pbgra32);
        image.Render(visual);
        PngBitmapEncoder encoder = new();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using MemoryStream output = new();
        encoder.Save(output);
        return Sprite(new RtssSprite(layer.LayerName, Convert.ToBase64String(output.ToArray()), top.Width, height, image), layer.LayerName);
    }

    private static SectionStyle[] TextLayer(RtssLayoutInfo layout, RtssPlacement layer)
    {
        RtssTextSource source = layer.Text switch
        {
            string text when text.Contains("<FR>", StringComparison.Ordinal) && text.Contains("<FT>", StringComparison.Ordinal) => RtssTextSource.FrameRate,
            string text when text.Contains("<EXE>", StringComparison.Ordinal) && text.Contains("<API>", StringComparison.Ordinal) => RtssTextSource.GameInfo,
            string text when text.Contains("<TIME=", StringComparison.Ordinal) => RtssTextSource.Clock,
            string text when text.Contains("%GPU1 memory usage%", StringComparison.Ordinal) => RtssTextSource.GpuVram,
            string text when text.Contains("%GPU1 temperature%", StringComparison.Ordinal) => RtssTextSource.GpuTemperature,
            string text when text.Contains("%GPU1 usage%", StringComparison.Ordinal) => RtssTextSource.GpuUsage,
            string text when text.Contains("%GPU1 clock%", StringComparison.Ordinal) => RtssTextSource.GpuClock,
            string text when text.Contains("%GPU1 power%", StringComparison.Ordinal) => RtssTextSource.GpuPower,
            string text when text.Contains("%GPU%", StringComparison.Ordinal) => RtssTextSource.GpuName,
            string text when text.Contains("%CPU temperature%", StringComparison.Ordinal) => RtssTextSource.CpuTemperature,
            string text when text.Contains("%CPU usage%", StringComparison.Ordinal) => RtssTextSource.CpuUsage,
            string text when text.Contains("%CPU power%", StringComparison.Ordinal) => RtssTextSource.CpuPower,
            string text when text.Contains("%CPU%", StringComparison.Ordinal) => RtssTextSource.CpuName,
            string text when text.Contains("%RAM usage percent%", StringComparison.Ordinal) => RtssTextSource.RamUsage,
            string text when text.Contains("%RAM usage%", StringComparison.Ordinal) => RtssTextSource.RamUsed,
            _ => RtssTextSource.None
        };
        if (source == RtssTextSource.None) return [];
        Match valueStart = Regex.Match(layer.Text, @"<FR>|<EXE>|<TIME=|%[A-Za-z]", RegexOptions.CultureInvariant);
        Match textScale = Regex.Match(layer.Text[..valueStart.Index], @"<S=(\d+)>", RegexOptions.CultureInvariant);
        double percent = textScale.Success ? int.Parse(textScale.Groups[1].Value, CultureInfo.InvariantCulture) : layer.Size >= 50 ? layer.Size : 100;
        double fontSize = Math.Clamp(Math.Abs(layout.FontHeight) * layout.ZoomRatio * percent / 100, 10, 48);
        return [new SectionStyle(SectionKind.Artwork, layer.LayerName, "#" + layer.Color[^6..], "#FFFFFF", fontSize, fontSize, 0, 0, true, [])
        {
            Id = Guid.NewGuid().ToString("N"), Padding = 0, RtssTextSource = source, RtssFontFace = layout.FontFace,
            ImageWidth = source == RtssTextSource.GameInfo ? 240 : 220,
            ImageHeight = source == RtssTextSource.GameInfo ? 44 : fontSize + 6
        }];
    }

    private static SectionStyle[] FindOther(RtssArtwork skin, RtssPlacement layer)
    {
        RtssAnimation? animation = skin.Animations.FirstOrDefault(item => item.Name == layer.TableName);
        if (animation is not null)
            return [new SectionStyle(SectionKind.Artwork, layer.LayerName, "#83EFCD", "#FFFFFF", 24, 24, 0, 0, true, [])
            {
                Id = Guid.NewGuid().ToString("N"), ShowName = false, Padding = 0,
                AnimationFrames = animation.Frames, AnimationSource = animation.Source,
                AnimationMinimum = animation.Minimum, AnimationMaximum = animation.Maximum,
                ImageWidth = animation.Width, ImageHeight = animation.Height
            }];
        RtssLiveBar? bar = skin.LiveBars.FirstOrDefault(item => item.Name == layer.TableName);
        if (bar is not null)
        {
            string color = bar.Source switch
            {
                ArtworkBarSource.GpuUsage => "#FFAA49", ArtworkBarSource.CpuUsage => "#69CDF6",
                ArtworkBarSource.RamUsage => "#DBB7FA", ArtworkBarSource.GpuTemperature => "#FF7777",
                ArtworkBarSource.CpuTemperature => "#FFBA77", _ => throw new InvalidDataException($"Unsupported RTSS live bar source: {bar.Source}")
            };
            return [new SectionStyle(SectionKind.Artwork, layer.LayerName, color, "#FFFFFF", 24, 24, 0, 0, true, [])
            {
                Id = Guid.NewGuid().ToString("N"), ShowName = false, Padding = 0,
                BarSource = bar.Source, BarMinimum = bar.Minimum, BarMaximum = bar.Maximum,
                ImageWidth = bar.Width, ImageHeight = bar.Height
            }];
        }
        RtssLiveGraph? graph = skin.LiveGraphs.FirstOrDefault(item => item.Name == layer.TableName);
        if (graph is null) return [];
        SectionKind kind = graph.Source switch
        {
            ArtworkGraphSource.GpuUsage => SectionKind.Gpu, ArtworkGraphSource.CpuUsage => SectionKind.Cpu,
            ArtworkGraphSource.RamUsage => SectionKind.Ram, _ => SectionKind.Frames
        };
        SectionStyle source = Preferences.Initial.Sections.Single(item => item.Kind == kind);
        return [source with
        {
            Id = Guid.NewGuid().ToString("N"), Name = layer.LayerName, ShowName = false,
            Metrics = [], Graph = true, GraphTransparent = true, GraphSource = graph.Source, GraphWidth = graph.Width,
            GraphHeight = graph.Height, GraphBelow = true, Padding = 0,
            ImageWidth = graph.Width, ImageHeight = graph.Height
        }];
    }

    private static SectionStyle Sprite(RtssSprite sprite, string name) => new(SectionKind.Artwork, name, "#83EFCD", "#FFFFFF", 24, 24, 0, 0, true, [])
    {
        Id = Guid.NewGuid().ToString("N"), ImageData = sprite.ImageData, ImageWidth = sprite.Width,
        ImageHeight = sprite.Height, ShowName = false, Padding = 0
    };
}
