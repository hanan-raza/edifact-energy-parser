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

    /// <summary>Added to the true segment count in UNT, to produce a broken trailer.</summary>
    public int SegmentCountOffset { get; init; }

    public override string ToString()
    {
        List<string> body =
        [
            "BGM+7+MSI0000001+9",
            "DTM+137:202601160630?+01:303",
            $"NAD+MS+{SenderId}::293",
            $"NAD+MR+{RecipientId}::293",
            "UNS+D",
            "NAD+DP",
            "LOC+172+DE0001234567800000000000000000001",
            "LIN+1",
            "PIA+5+1-1?:1.29.0:SRW",
        ];

        // 15-minute intervals on 2026-01-15 from 00:00 (CET, +01).
        var start = new DateTimeOffset(2026, 1, 15, 0, 0, 0, TimeSpan.FromHours(1));
        foreach (var quantity in Quantities)
        {
            var end = start.AddMinutes(15);
            body.Add($"QTY+220:{quantity.ToString(CultureInfo.InvariantCulture)}:KWH");
            body.Add($"DTM+163:{start:yyyyMMddHHmm}?+01:303");
            body.Add($"DTM+164:{end:yyyyMMddHHmm}?+01:303");
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
}
