namespace EdifactEnergyParser.Api.Adapters;

/// <summary>Shortens FParsec error messages for API responses.</summary>
internal static class FParsecMessage
{
    /// <summary>
    /// FParsec messages repeat the position and draw a caret under the input line; the API already
    /// reports line and column, so only the "Expecting: …" part is kept.
    /// </summary>
    public static string Expectation(string fparsecMessage)
    {
        var lines = fparsecMessage
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(l => !l.StartsWith("Error in Ln", StringComparison.Ordinal))
            .ToList();
        var expecting = lines.FindIndex(l => l.StartsWith("Expecting", StringComparison.Ordinal));
        var relevant = expecting >= 0 ? lines.Skip(expecting) : lines;
        return string.Join(" ", relevant);
    }
}
