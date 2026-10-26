module MsconsParserTests

open System
open System.IO
open Xunit
open EdifactEnergyParser.Core

let private parseOk input =
    match MsconsParser.parse input with
    | Ok messages -> messages
    | Error errors -> failwithf "Expected Ok but got %A" errors

let private parseErrors input =
    match MsconsParser.parse input with
    | Ok messages -> failwithf "Expected Error but got %A" messages
    | Error errors -> errors

let private sample () =
    File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "samples", "mscons_example.edi"))

let private header =
    [
        "BGM+7+MSI1+9'"
        "DTM+137:202601160630?+01:303'"
        "NAD+MS+9900000000003::293'"
        "NAD+MR+9900000000010::293'"
        "UNS+D'"
        "NAD+DP'"
    ]

/// Wraps body segments in an envelope with a correct UNT count.
let private interchangeWith (una: string) (body: string list) =
    una
    + "UNB+UNOC:3+9900000000003:500+9900000000010:500+260116:0630+R1'"
    + "UNH+1+MSCONS:D:04B:UN:2.4c'"
    + String.concat "" body
    + $"UNT+{body.Length + 2}+1'UNZ+1+R1'"

let private interchange body = interchangeWith "" body

let private point (values: string list) =
    [ "LOC+172+DE0001234567800000000000000000001'"; "LIN+1'" ] @ values

let private value qty start ``end`` =
    [ qty; $"DTM+163:{start}:303'"; $"DTM+164:{``end``}:303'" ]

let private singleValue (input: string) =
    let message = parseOk input |> Assert.Single
    let point = Assert.Single message.MeteringPoints
    let series = Assert.Single point.TimeSeries
    Assert.Single series.Values

let private time (y, mo, d, h, mi) (offset: float) =
    DateTimeOffset(y, mo, d, h, mi, 0, TimeSpan.FromHours offset)

[<Fact>]
let ``sample MSCONS parses into three metering points`` () =
    let message = parseOk (sample ()) |> Assert.Single

    Assert.Equal("1", message.ReferenceNumber)
    Assert.Equal("7", message.DocumentType)
    Assert.Equal("MSI0000001", message.DocumentNumber)
    Assert.Equal(time (2026, 1, 16, 6, 30) 1.0, message.DocumentDate)
    Assert.Equal({ Id = "9900000000003"; CodeQualifier = Some "293" }, message.Sender)
    Assert.Equal({ Id = "9900000000010"; CodeQualifier = Some "293" }, message.Recipient)

    Assert.Equal<string list>(
        [
            "DE0001234567800000000000000000001"
            "DE0001234567800000000000000000002"
            "DE0001234567800000000000000000003"
        ],
        message.MeteringPoints |> List.map _.Id
    )

    Assert.All(message.MeteringPoints, fun p -> Assert.Equal(33, p.Id.Length))
    Assert.Equal<int list>([ 1; 1; 2 ], message.MeteringPoints |> List.map _.TimeSeries.Length)

    Assert.Equal<int list>(
        [ 4; 2; 1; 1 ],
        message.MeteringPoints |> List.collect _.TimeSeries |> List.map _.Values.Length
    )

[<Fact>]
let ``sample household series keeps OBIS code, meter number, units and statuses`` () =
    let household = (parseOk (sample ()) |> List.head).MeteringPoints.Head
    let series = Assert.Single household.TimeSeries

    Assert.Equal(Some "1EMH0012345678", household.MeterNumber)
    Assert.Equal(Some "1-1:1.29.0", series.ObisCode)
    Assert.Equal(0.884m, series.Values |> List.sumBy _.Quantity.Value)
    Assert.All(series.Values, fun v -> Assert.Equal(KilowattHour, v.Quantity.Unit))

    Assert.Equal<ValueStatus list>(
        [ TrueValue; TrueValue; SubstituteValue; TrueValue ],
        series.Values |> List.map _.Status
    )

    let first = series.Values.Head
    Assert.Equal(time (2026, 1, 15, 0, 0) 1.0, first.Start)
    Assert.Equal(time (2026, 1, 15, 0, 15) 1.0, first.End)

    // Each interval ends where the next starts.
    for a, b in List.pairwise series.Values do
        Assert.Equal(a.End, b.Start)

[<Fact>]
let ``sample keeps proposed values and missing meter number`` () =
    let pv = (parseOk (sample ()) |> List.head).MeteringPoints[1]
    let series = Assert.Single pv.TimeSeries

    Assert.Equal(None, pv.MeterNumber)
    Assert.Equal(Some "1-1:2.29.0", series.ObisCode)
    Assert.Equal<ValueStatus list>([ TrueValue; ProposedValue ], series.Values |> List.map _.Status)

[<Fact>]
let ``quarter hour across the October DST switch lasts fifteen minutes`` () =
    let v =
        singleValue (
            interchange (header @ point (value "QTY+220:0.3:KWH'" "202610250245?+02" "202610250200?+01"))
        )

    Assert.Equal(TimeSpan.FromMinutes 15.0, v.End - v.Start)
    Assert.Equal(DateTimeOffset(2026, 10, 25, 0, 45, 0, TimeSpan.Zero), v.Start.ToUniversalTime())

[<Fact>]
let ``local date format 102 is read in German time`` () =
    let input =
        interchange (
            [ "BGM+7+MSI1+9'"; "DTM+137:20260716:102'"; "NAD+MS+9900000000003::293'"; "NAD+MR+9900000000010::293'"; "UNS+D'"; "NAD+DP'" ]
            @ point (value "QTY+220:1:KWH'" "202607160000?+02" "202607160015?+02")
        )

    let message = parseOk input |> Assert.Single

    Assert.Equal(time (2026, 7, 16, 0, 0) 2.0, message.DocumentDate)

[<Fact>]
let ``optional PIA, RFF and contact segments may be present or absent`` () =
    let body =
        [
            "BGM+7+MSI1+9'"
            "DTM+137:202601160630?+01:303'"
            "NAD+MS+9900000000003::293'"
            "CTA+IC+:Netzbetrieb'"
            "COM+0241123456:TE'"
            "NAD+MR+9900000000010::293'"
            "UNS+D'"
            "NAD+DP'"
        ]
        @ point (value "QTY+220:1:KWH'" "202601150000?+01" "202601150015?+01")

    let message = parseOk (interchange body) |> Assert.Single
    let p = Assert.Single message.MeteringPoints

    Assert.Equal(None, p.MeterNumber)
    Assert.Equal(None, (Assert.Single p.TimeSeries).ObisCode)

[<Fact>]
let ``decimal comma from UNA is read exactly`` () =
    let v =
        singleValue (
            interchangeWith
                "UNA:+,? '"
                (header @ point (value "QTY+220:1,125:MWH'" "202601150000?+01" "202601150015?+01"))
        )

    Assert.Equal({ Value = 1.125m; Unit = MegawattHour }, v.Quantity)

[<Fact>]
let ``missing LOC is a structure error at the segment position`` () =
    let errors = parseErrors (interchange (header @ [ "LIN+1'" ]))

    // UNH is 1, so the seven body segments start at 2 and LIN is at 8.
    Assert.Equal<MsconsError list>([ StructureError("1", 8, "Expected LOC segment, found LIN") ], errors)

[<Fact>]
let ``message ending before any metering value is a structure error at UNT`` () =
    let errors = parseErrors (interchange (header @ [ "LOC+172+DE1'"; "LIN+1'" ]))

    match errors with
    | [ StructureError("1", 10, text) ] ->
        Assert.Contains("QTY segment", text)
        Assert.EndsWith("found UNT", text)
    | other -> failwithf "Unexpected %A" other

[<Fact>]
let ``unsupported unit is an invalid segment`` () =
    let errors =
        parseErrors (interchange (header @ point (value "QTY+220:1:XYZ'" "202601150000?+01" "202601150015?+01")))

    Assert.Equal<MsconsError list>([ InvalidSegment("1", 10, "QTY", "Measurement unit 'XYZ' is not supported") ], errors)

[<Fact>]
let ``quantity without interval end is an invalid segment`` () =
    let errors =
        parseErrors (interchange (header @ point [ "QTY+220:1:KWH'"; "DTM+163:202601150000?+01:303'" ]))

    Assert.Equal<MsconsError list>([ InvalidSegment("1", 10, "QTY", "Interval end (DTM+164) is missing") ], errors)

[<Fact>]
let ``malformed 303 offset is an invalid segment`` () =
    let errors =
        parseErrors (interchange (header @ point (value "QTY+220:1:KWH'" "202601150000" "202601150015?+01")))

    match errors with
    | [ InvalidSegment("1", 11, "DTM", reason) ] -> Assert.Contains("format 303", reason)
    | other -> failwithf "Unexpected %A" other

[<Fact>]
let ``non-MSCONS message is rejected`` () =
    let input =
        "UNB+UNOC:3+9900000000003:500+9900000000010:500+260116:0630+R1'"
        + "UNH+7+UTILMD:D:11A:UN:5.2e'UNT+2+7'UNZ+1+R1'"

    Assert.Equal<MsconsError list>([ NotMscons("7", "UTILMD") ], parseErrors input)

[<Fact>]
let ``envelope errors are passed through`` () =
    let input = (interchange (header @ point (value "QTY+220:1:KWH'" "202601150000?+01" "202601150015?+01"))).Replace("UNZ+1", "UNZ+2")

    Assert.Equal<MsconsError list>([ EnvelopeInvalid(MessageCountMismatch(2, 1)) ], parseErrors input)
