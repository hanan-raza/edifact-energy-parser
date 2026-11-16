using System.Globalization;
using System.Text;

namespace EdifactEnergyParser.Tests.Integration;

/// <summary>
/// Builds small MSCONS interchanges for tests: one metering point with four quarter-hour values.
/// Each knob breaks exactly one thing, so a test states which rule it is about.
/// </summary>
internal sealed record SyntheticMscons
{
    public string ControlReference { get; init; } = "TST0000001";
    public string SenderId { get; init; } = "9900000000003";
    public string RecipientId { get; init; } = "9900000000010";
    public decimal[] Quantities { get; init; } = [0.250m, 0.198m, 0.205m, 0.231m];
    public string MeteringPointId { get; init; } = "DE0001234567800000000000000000001";
    public string Unit { get; init; } = "KWH";

    /// <summary>Start of the first interval; 2026-01-15 00:00 CET by default.</summary>
    public DateTimeOffset Start { get; init; } = new(2026, 1, 15, 0, 0, 0, TimeSpan.FromHours(1));

    /// <summary>Added to the true segment count in UNT, to produce a broken trailer.</summary>
    public int SegmentCountOffset { get; init; }

    public override string ToString()
    {
        var zone = EdifactEnergyParser.Core.EnvelopeParser.marketTimeZone;
        List<string> body =
        [
            "BGM+7+MSI0000001+9",
            "DTM+137:202601160630?+01:303",
            $"NAD+MS+{SenderId}::293",
            $"NAD+MR+{RecipientId}::293",
            "UNS+D",
            "NAD+DP",
            $"LOC+172+{MeteringPointId}",
            "LIN+1",
            "PIA+5+1-1?:1.29.0:SRW",
        ];

        // 15-minute intervals in German time, so a series across a DST switch changes its offset.
        var start = TimeZoneInfo.ConvertTime(Start, zone);
        foreach (var quantity in Quantities)
        {
            var end = TimeZoneInfo.ConvertTime(start.AddMinutes(15), zone);
            body.Add($"QTY+220:{quantity.ToString(CultureInfo.InvariantCulture)}:{Unit}");
            body.Add($"DTM+163:{Format(start)}:303");
            body.Add($"DTM+164:{Format(end)}:303");
            start = end;
        }

        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder("UNA:+.? '\n");
        sb.Append(inv, $"UNB+UNOC:3+{SenderId}:500+{RecipientId}:500+260116:0630+{ControlReference}'\n");
        sb.Append("UNH+1+MSCONS:D:04B:UN:2.4c'\n");
        foreach (var segment in body)
        {
            sb.Append(segment).Append("'\n");
        }

        sb.Append(inv, $"UNT+{body.Count + 2 + SegmentCountOffset}+1'\n");
        sb.Append(inv, $"UNZ+1+{ControlReference}'\n");
        return sb.ToString();
    }

    // Format 303: local time plus the UTC offset in hours, with "+" released as "?+".
    private static string Format(DateTimeOffset local) =>
        string.Create(CultureInfo.InvariantCulture, $"{local:yyyyMMddHHmm}?+{local.Offset.Hours:00}");
}
