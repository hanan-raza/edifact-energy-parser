using System.Diagnostics.CodeAnalysis;
using EdifactEnergyParser.Api.Contracts;
using EdifactEnergyParser.Core;

namespace EdifactEnergyParser.Api.Adapters;

/// <summary>Calls the F# <see cref="SegmentExplainer"/> and maps its records and lists to the API contract.</summary>
public sealed class ExplainerAdapter
{
    /// <summary>Explains <paramref name="content"/> segment by segment, with local times in German time.</summary>
    /// <param name="content">Raw EDIFACT text; it only has to be tokenizable, not a valid MSCONS or UTILMD.</param>
    /// <param name="segments">The explanation, one entry per segment (UNA included when present).</param>
    /// <param name="error">Where and why the text could not be split into segments.</param>
    /// <returns><c>true</c> if the text could be explained.</returns>
    public bool TryExplain(
        string content,
        [NotNullWhen(true)] out IReadOnlyList<SegmentExplanationResponse>? segments,
        [NotNullWhen(false)] out string? error)
    {
        ArgumentNullException.ThrowIfNull(content);

        var result = SegmentExplainer.explain(content);
        if (result.IsError)
        {
            var e = result.ErrorValue;
            segments = null;
            error = $"Not readable as EDIFACT at line {e.Line}, column {e.Column}: {FParsecMessage.Expectation(e.Message)}";
            return false;
        }

        segments = [.. result.ResultValue.Select(ToResponse)];
        error = null;
        return true;
    }

    private static SegmentExplanationResponse ToResponse(SegmentExplanation s) =>
        new(s.Segment, s.Description, [.. s.Fields.Select(f => new FieldExplanationResponse(f.Name, f.Value, f.Meaning))]);
}
