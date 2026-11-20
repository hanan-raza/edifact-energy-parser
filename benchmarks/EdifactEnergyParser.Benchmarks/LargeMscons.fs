namespace EdifactEnergyParser.Benchmarks

open System
open System.Globalization

/// <summary>
/// Builds MSCONS interchanges of a chosen size, shaped like a real daily load-profile delivery:
/// many metering points (Messlokationen), each with one day of quarter-hour values (96 per day).
/// </summary>
module LargeMscons =

    /// <summary>Quarter hours in a normal 24-hour day.</summary>
    [<Literal>]
    let ValuesPerPoint = 96

    let private inv = CultureInfo.InvariantCulture

    // A winter day, so every stamp has the same +01 offset and the size per point is constant.
    let private day = DateTimeOffset(2026, 1, 15, 0, 0, 0, TimeSpan.FromHours 1.0)

    let private stamp (t: DateTimeOffset) = t.ToString("yyyyMMddHHmm", inv) + "?+01"

    let private body (meteringPoints: int) : string list =
        [
            yield "BGM+7+MSI0000001+9"
            yield "DTM+137:202601160630?+01:303"
            yield "NAD+MS+9900000000003::293"
            yield "NAD+MR+9900000000010::293"
            yield "UNS+D"
            yield "NAD+DP"

            for p in 1..meteringPoints do
                yield String.Format(inv, "LOC+172+DE{0:D31}", p)
                yield "LIN+1"
                yield "PIA+5+1-1?:1.29.0:SRW"

                for i in 0 .. ValuesPerPoint - 1 do
                    let start = day.AddMinutes(15.0 * float i)
                    // Deterministic but varied amounts, so number parsing is not trivially cached.
                    let amount = decimal ((p * 7919 + i * 104729) % 100000) / 1000m
                    yield String.Format(inv, "QTY+220:{0:F3}:KWH", amount)
                    yield $"DTM+163:{stamp start}:303"
                    yield $"DTM+164:{stamp (start.AddMinutes 15.0)}:303"
        ]

    /// <summary>An interchange with one MSCONS message holding <paramref name="meteringPoints"/> metering points.</summary>
    let build (meteringPoints: int) : string =
        let segments = body meteringPoints

        [
            yield "UNA:+.? '"
            yield "UNB+UNOC:3+9900000000003:500+9900000000010:500+260116:0630+BENCH00001'"
            yield "UNH+1+MSCONS:D:04B:UN:2.4c'"
            for s in segments do
                yield s + "'"
            yield $"UNT+{segments.Length + 2}+1'"
            yield "UNZ+1+BENCH00001'"
        ]
        |> List.map (fun line -> line + "\n")
        |> String.concat ""

    /// <summary>The number of metering points that gives roughly <paramref name="bytes"/> of EDIFACT text.</summary>
    let pointsForSize (bytes: int) : int =
        let perPoint = (build 2).Length - (build 1).Length
        let overhead = (build 1).Length - perPoint
        max 1 (int (Math.Round(float (bytes - overhead) / float perPoint)))
