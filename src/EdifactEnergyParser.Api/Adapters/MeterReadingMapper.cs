using EdifactEnergyParser.Core;
using EdifactEnergyParser.Workflow;

namespace EdifactEnergyParser.Api.Adapters;

/// <summary>A metering point whose values were not forwarded, and why.</summary>
/// <param name="MessageReference">UNH reference of the message it came in.</param>
/// <param name="MeteringPointId">The LOC+172 ID.</param>
/// <param name="Reason">Why it was left out, for the log.</param>
public sealed record SkippedMeteringPoint(string MessageReference, string MeteringPointId, string Reason);

/// <summary>What will be sent to meter-data-service and what will not.</summary>
public sealed record MeterReadingPlan(IReadOnlyList<MeterReadingBatch> Batches, IReadOnlyList<SkippedMeteringPoint> Skipped);

/// <summary>
/// Turns parsed MSCONS messages into one <see cref="MeterReadingBatch"/> per metering point.
/// Everything that cannot be forwarded faithfully is skipped with a reason instead of being bent to fit.
/// </summary>
/// <remarks>
/// meter-data-service stores one quarter-hour kWh series per market location, so a metering point is
/// forwarded only if it has exactly one series of 15-minute energy values. Power (kW) and gas volume
/// (m³) series have no place there, and two energy series (e.g. consumption 1.29.0 and feed-in 2.29.0)
/// would overwrite each other in the same market location series.
/// </remarks>
public static class MeterReadingMapper
{
    private const int MarketLocationIdLength = 11;
    private const decimal KilowattHoursPerMegawattHour = 1000m;
    private static readonly TimeSpan QuarterHour = TimeSpan.FromMinutes(15);

    /// <summary>Plans the forwarding of every metering point in <paramref name="messages"/>.</summary>
    /// <param name="messages">Messages that passed validation.</param>
    /// <param name="marketLocations">Metering point ID → MaLo-ID, see <see cref="MeterDataServiceOptions.MarketLocations"/>.</param>
    public static MeterReadingPlan Plan(IEnumerable<MsconsMessage> messages, IReadOnlyDictionary<string, string> marketLocations)
    {
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentNullException.ThrowIfNull(marketLocations);

        var batches = new List<MeterReadingBatch>();
        var skipped = new List<SkippedMeteringPoint>();

        foreach (var message in messages)
        {
            foreach (var point in message.MeteringPoints)
            {
                var reason = TryPlan(message, point, marketLocations, out var batch);
                if (batch is not null)
                {
                    batches.Add(batch);
                }
                else
                {
                    skipped.Add(new SkippedMeteringPoint(message.ReferenceNumber, point.Id, reason!));
                }
            }
        }

        return new MeterReadingPlan(batches, skipped);
    }

    private static string? TryPlan(
        MsconsMessage message, MeteringPoint point, IReadOnlyDictionary<string, string> marketLocations, out MeterReadingBatch? batch)
    {
        batch = null;

        if (!TryResolveMarketLocation(point.Id, marketLocations, out var marketLocationId))
        {
            return "No market location (MaLo-ID) is configured for this metering point.";
        }

        var candidates = point.TimeSeries.Where(IsQuarterHourEnergy).ToList();
        if (candidates.Count == 0)
        {
            return "No series of 15-minute energy values (kWh or MWh).";
        }

        if (candidates.Count > 1)
        {
            var registers = string.Join(", ", candidates.Select(s => s.ObisCode?.Value ?? "(no OBIS code)"));
            return $"{candidates.Count} energy series ({registers}); meter-data-service keeps one series per market location.";
        }

        // Unusable values (QTY+20) must not be billed; leaving them out makes them a gap that
        // meter-data-service fills and marks as such.
        var readings = candidates[0].Values
            .Where(v => !v.Status.IsUnusableValue)
            .Select(ToReading)
            .ToList();
        if (readings.Count == 0)
        {
            return "Every value is marked unusable.";
        }

        batch = new MeterReadingBatch(marketLocationId, $"{message.DocumentNumber}_{point.Id}.csv", readings);
        return null;
    }

    private static bool TryResolveMarketLocation(
        string meteringPointId, IReadOnlyDictionary<string, string> marketLocations, out string marketLocationId)
    {
        if (meteringPointId.Length == MarketLocationIdLength && meteringPointId.All(char.IsAsciiDigit))
        {
            marketLocationId = meteringPointId;
            return true;
        }

        return marketLocations.TryGetValue(meteringPointId, out marketLocationId!);
    }

    private static bool IsQuarterHourEnergy(TimeSeries series) =>
        series.Values.All(v =>
            v.End - v.Start == QuarterHour
            && (v.Quantity.Unit.IsKilowattHour || v.Quantity.Unit.IsMegawattHour));

    private static MeterReading ToReading(IntervalValue value)
    {
        var kilowattHours = value.Quantity.Unit.IsMegawattHour
            ? value.Quantity.Value * KilowattHoursPerMegawattHour
            : value.Quantity.Value;

        return new MeterReading(value.Start, kilowattHours, ToStatus(value.Status));
    }

    private static MeterReadingStatus ToStatus(ValueStatus status) =>
        status.Tag switch
        {
            ValueStatus.Tags.TrueValue => MeterReadingStatus.Measured,
            ValueStatus.Tags.SubstituteValue => MeterReadingStatus.Replaced,
            ValueStatus.Tags.ProposedValue => MeterReadingStatus.Estimated,
            _ => throw new ArgumentOutOfRangeException(nameof(status), status, "No meter-data-service status for this value."),
        };
}
