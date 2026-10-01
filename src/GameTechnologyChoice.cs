using System.Collections.Immutable;

namespace FrameTrace;

public sealed record GameTechnologyChoice(string Application, bool Fsr411, bool Fsr4FrameGeneration);

internal static class GameTechnologyChoices
{
    public static ImmutableArray<GameTechnologyChoice> Set(ImmutableArray<GameTechnologyChoice> choices, GameTechnologyChoice choice) =>
        choices.Where(item => !item.Application.Equals(choice.Application, StringComparison.OrdinalIgnoreCase))
            .Concat(choice.Fsr411 || choice.Fsr4FrameGeneration ? [choice] : [])
            .ToImmutableArray();

    public static UpscalerObservation Apply(UpscalerObservation observed, GameTechnologyChoice? choice)
    {
        if (choice is null) return observed;
        string label = choice.Fsr411 ? "FSR 4.1.1" + (observed.Mode is null ? "" : " · " + observed.Mode) : observed.Label;
        string? generation = choice.Fsr4FrameGeneration
            ? "FSR 4 FG · " + (observed.FrameGenerationSetting?.EndsWith(" · Off", StringComparison.Ordinal) == true ? "Off" : "On")
            : observed.FrameGenerationSetting;
        return observed with { Label = label, HasEvidence = observed.HasEvidence || choice.Fsr411, FrameGenerationSetting = generation };
    }
}
