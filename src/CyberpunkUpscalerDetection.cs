using System.IO;
using System.Text.Json;

namespace FrameTrace;

internal static class CyberpunkUpscalerDetection
{
    public static string SettingsPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CD Projekt Red", "Cyberpunk 2077", "UserSettings.json");

    public static UpscalerObservation Inspect(string settingsPath)
    {
        JsonDocument document;
        try { document = JsonDocument.Parse(File.ReadAllText(settingsPath)); }
        catch (JsonException error) { throw new InvalidDataException($"Cyberpunk settings file '{settingsPath}' is not valid JSON: {error.Message}", error); }
        using (document)
        {
            JsonElement groups = RequiredArray(document.RootElement, "data", settingsPath);
            JsonElement[] graphics = groups.EnumerateArray().Where(group => group.ValueKind == JsonValueKind.Object && group.TryGetProperty("group_name", out JsonElement name) && name.ValueKind == JsonValueKind.String && name.GetString() == "/graphics/presets").ToArray();
            if (graphics.Length != 1) throw new InvalidDataException($"Cyberpunk settings file '{settingsPath}' must contain one /graphics/presets group.");
            JsonElement options = RequiredArray(graphics[0], "options", settingsPath);
            string selection = ReadOption(options, "ResolutionScaling", settingsPath);
            string? mode = selection is "FSR2" or "FSR3" or "FSR4" or "DLSS" or "XESS" ? ReadOption(options, selection, settingsPath) : null;
            string label = selection switch { "FSR2" => "FSR 2", "FSR3" => "FSR 3", "FSR4" => "FSR 4", "XESS" => "XeSS", _ => selection };
            string? frameGeneration = ReadOptionalStringOption(options, "FrameGeneration", settingsPath);
            string? enabledOption = frameGeneration switch { "FSR3" => "FSR3_FrameGeneration", "DLSS" => "DLSSFrameGen", "XESS" => "XESS_FrameGeneration", _ => null };
            bool? enabled = enabledOption is null ? null : ReadOptionalBoolOption(options, enabledOption, settingsPath);
            string? frameGenerationSetting = enabled is null ? null : (frameGeneration == "FSR3" ? "FSR 3" : frameGeneration == "XESS" ? "XeSS" : frameGeneration) + " FG · " + (enabled.Value ? "On" : "Off");
            return new UpscalerObservation(label + (mode is null ? "" : " · " + mode), true) { Mode = mode, FrameGenerationSetting = frameGenerationSetting };
        }
    }

    private static JsonElement RequiredArray(JsonElement parent, string name, string settingsPath)
    {
        if (parent.ValueKind != JsonValueKind.Object || !parent.TryGetProperty(name, out JsonElement value) || value.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException($"Cyberpunk settings file '{settingsPath}' must contain an array named '{name}'.");
        return value;
    }

    private static string ReadOption(JsonElement options, string name, string settingsPath) =>
        ReadOptionalStringOption(options, name, settingsPath)
        ?? throw new InvalidDataException($"Cyberpunk settings file '{settingsPath}' must contain a string value for '{name}'.");

    private static string? ReadOptionalStringOption(JsonElement options, string name, string settingsPath)
    {
        JsonElement? option = FindOption(options, name, settingsPath);
        if (option is null) return null;
        if (!option.Value.TryGetProperty("value", out JsonElement value) || value.ValueKind != JsonValueKind.String)
            throw new InvalidDataException($"Cyberpunk settings file '{settingsPath}' must contain a string value for '{name}'.");
        string text = value.GetString()!;
        if (string.IsNullOrWhiteSpace(text) || text.Length > 48 || text.Any(char.IsControl))
            throw new InvalidDataException($"Cyberpunk settings file '{settingsPath}' has an invalid '{name}' value.");
        return text;
    }

    private static bool? ReadOptionalBoolOption(JsonElement options, string name, string settingsPath)
    {
        JsonElement? option = FindOption(options, name, settingsPath);
        if (option is null) return null;
        if (!option.Value.TryGetProperty("value", out JsonElement value) || value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new InvalidDataException($"Cyberpunk settings file '{settingsPath}' must contain a Boolean value for '{name}'.");
        return value.GetBoolean();
    }

    private static JsonElement? FindOption(JsonElement options, string name, string settingsPath)
    {
        JsonElement[] matches = options.EnumerateArray().Where(option => option.ValueKind == JsonValueKind.Object && option.TryGetProperty("name", out JsonElement optionName) && optionName.ValueKind == JsonValueKind.String && optionName.GetString() == name).ToArray();
        if (matches.Length > 1) throw new InvalidDataException($"Cyberpunk settings file '{settingsPath}' has more than one '{name}' option.");
        return matches.Length == 0 ? null : matches[0];
    }
}
