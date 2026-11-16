namespace EdifactEnergyParser.Workflow;

/// <summary>Where processed MSCONS values go: the <c>MeterDataService</c> configuration section.</summary>
public sealed class MeterDataServiceOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "MeterDataService";

    /// <summary>Base URL of meter-data-service; <c>null</c> turns forwarding off.</summary>
    public Uri? BaseUrl { get; set; }

    /// <summary>
    /// Metering point ID (Messlokations-ID, 33 characters) → market location ID (Marktlokations-ID, 11 digits).
    /// </summary>
    /// <remarks>
    /// meter-data-service keeps series per market location, but an MSCONS for a metering point names
    /// only the metering point. In the market this assignment comes from master data (UTILMD); until
    /// that is imported here it is configured. An MSCONS that already carries an 11-digit MaLo-ID in
    /// LOC+172 needs no entry.
    /// </remarks>
    public Dictionary<string, string> MarketLocations { get; } = new(StringComparer.Ordinal);
}
