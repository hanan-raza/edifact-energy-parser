namespace EdifactEnergyParser.Workflow;

/// <summary>
/// Origin of a forwarded value (Messwertstatus), named as meter-data-service expects it in its
/// <c>status</c> column.
/// </summary>
public enum MeterReadingStatus
{
    /// <summary>Read from the meter (wahrer Wert).</summary>
    Measured = 1,

    /// <summary>Not yet confirmed, e.g. a proposed value (Vorschlagswert).</summary>
    Estimated = 2,

    /// <summary>Formed by the metering operator for a missing or implausible reading (Ersatzwert).</summary>
    Replaced = 3,
}

/// <summary>One quarter-hour energy value as meter-data-service imports it.</summary>
/// <param name="IntervalStart">Start of the 15-minute interval, with the offset the sender used.</param>
/// <param name="KilowattHours">Energy in the interval, always in kWh.</param>
/// <param name="Status">How the value came about.</param>
public sealed record MeterReading(DateTimeOffset IntervalStart, decimal KilowattHours, MeterReadingStatus Status);

/// <summary>The values for one market location (Marktlokation, MaLo), sent as one import file.</summary>
/// <param name="MarketLocationId">11-digit MaLo-ID the values belong to.</param>
/// <param name="FileName">Name of the uploaded file, so an import job can be traced back to the message.</param>
/// <param name="Readings">Values in message order.</param>
public sealed record MeterReadingBatch(string MarketLocationId, string FileName, IReadOnlyList<MeterReading> Readings);
