module ValidationRulesTests

open System
open System.IO
open Xunit
open EdifactEnergyParser.Core

let private time (y, mo, d, h, mi) (offset: float) =
    DateTimeOffset(y, mo, d, h, mi, 0, TimeSpan.FromHours offset)

let private value start ``end`` amount =
    {
        Start = start
        End = ``end``
        Quantity = { Value = amount; Unit = KilowattHour }
        Status = TrueValue
    }

let private pointWith (values: IntervalValue list) =
    {
        Id = "DE0001234567800000000000000000001"
        MeterNumber = None
        TimeSeries = [ { ObisCode = Some "1-1:1.29.0"; Values = values } ]
    }

let private valid =
    {
        ReferenceNumber = "1"
        DocumentType = "7"
        DocumentNumber = "MSI1"
        DocumentDate = time (2026, 1, 16, 6, 30) 1.0
        Sender = { Id = "9900000000003"; CodeQualifier = Some "293" }
        Recipient = { Id = "9900000000010"; CodeQualifier = Some "293" }
        MeteringPoints =
            [
                pointWith
                    [
                        value (time (2026, 1, 15, 0, 0) 1.0) (time (2026, 1, 15, 0, 15) 1.0) 0.25m
                        value (time (2026, 1, 15, 0, 15) 1.0) (time (2026, 1, 15, 0, 30) 1.0) 0.198m
                    ]
            ]
    }

let private withValues values =
    { valid with MeteringPoints = [ pointWith values ] }

let private segments (errors: ValidationError list) = errors |> List.map _.Segment

[<Fact>]
let ``valid message returns no errors`` () =
    Assert.Empty(ValidationRules.validate valid)
    Assert.Equal(Ok valid, ValidationRules.check valid)

[<Fact>]
let ``parsed sample MSCONS including a zero reading is valid`` () =
    let input =
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "samples", "mscons_example.edi"))

    match MsconsParser.parse input with
    | Ok [ message ] -> Assert.Empty(ValidationRules.validate message)
    | other -> failwithf "Unexpected %A" other

[<Fact>]
let ``message with three violations returns exactly three errors`` () =
    let message =
        { valid with
            Sender = { Id = "99000000003"; CodeQualifier = Some "293" }
            Recipient = { Id = "990000000001X"; CodeQualifier = Some "293" }
            MeteringPoints =
                [ pointWith [ value (time (2026, 1, 15, 0, 0) 1.0) (time (2026, 1, 15, 0, 15) 1.0) -0.5m ] ] }

    let errors = ValidationRules.validate message

    Assert.Equal(3, errors.Length)
    Assert.Equal<string list>([ "NAD"; "NAD"; "QTY" ], segments errors)
    Assert.Contains("Sender (NAD+MS) '99000000003'", errors[0].Message)
    Assert.Contains("Recipient (NAD+MR) '990000000001X'", errors[1].Message)
    Assert.Contains("-0.5", errors[2].Message)
    Assert.Equal(Error errors, ValidationRules.check message)

[<Fact>]
let ``every violation of one rule is reported, not just the first`` () =
    let message =
        withValues
            [
                value (time (2026, 1, 15, 0, 0) 1.0) (time (2026, 1, 15, 0, 15) 1.0) -1m
                value (time (2026, 1, 15, 0, 15) 1.0) (time (2026, 1, 15, 0, 30) 1.0) 2m
                value (time (2026, 1, 15, 0, 30) 1.0) (time (2026, 1, 15, 0, 45) 1.0) -3m
            ]

    let errors = ValidationRules.quantitiesNotNegative message

    Assert.Equal<string list>([ "QTY"; "QTY" ], segments errors)
    Assert.Contains("2026-01-15 00:00+01:00", errors[0].Message)
    Assert.Contains("2026-01-15 00:30+01:00", errors[1].Message)

[<Fact>]
let ``zero quantity is allowed`` () =
    let message =
        withValues [ value (time (2026, 1, 15, 0, 0) 1.0) (time (2026, 1, 15, 0, 15) 1.0) 0m ]

    Assert.Empty(ValidationRules.validate message)

[<Theory>]
[<InlineData("990000000000")>]
[<InlineData("99000000000031")>]
[<InlineData("99000000000A3")>]
[<InlineData("")>]
let ``sender that is not 13 digits is rejected`` (id: string) =
    let message = { valid with Sender = { Id = id; CodeQualifier = None } }

    let error = Assert.Single(ValidationRules.validate message)
    Assert.Equal("NAD", error.Segment)

[<Fact>]
let ``non-ASCII digits are not a market-partner ID`` () =
    // Arabic-Indic digits pass Char.IsDigit but cannot appear in an EDIFACT code number.
    let message = { valid with Recipient = { Id = String('٣', 13); CodeQualifier = None } }

    Assert.Equal<string list>([ "NAD" ], segments (ValidationRules.validate message))

[<Fact>]
let ``message without metering points is rejected`` () =
    let errors = ValidationRules.validate { valid with MeteringPoints = [] }

    let error = Assert.Single errors
    Assert.Equal("LOC", error.Segment)

[<Fact>]
let ``summer time sent with winter offset is not German time`` () =
    let message =
        withValues [ value (time (2026, 7, 15, 12, 0) 1.0) (time (2026, 7, 15, 12, 15) 2.0) 1m ]

    let error = Assert.Single(ValidationRules.validate message)
    Assert.Equal("DTM", error.Segment)
    Assert.Contains("Interval start (DTM+163)", error.Message)
    Assert.Contains("2026-07-15 13:00+02:00", error.Message)

[<Fact>]
let ``document date in UTC is not German time`` () =
    let error = Assert.Single(ValidationRules.validate { valid with DocumentDate = time (2026, 1, 16, 5, 30) 0.0 })

    Assert.Equal("DTM", error.Segment)
    Assert.Contains("Document date (DTM+137)", error.Message)

[<Fact>]
let ``both passes of the repeated October hour are German time`` () =
    // 02:00-03:00 occurs twice on 2026-10-25: first at +02, then at +01.
    let message =
        withValues
            [
                value (time (2026, 10, 25, 2, 45) 2.0) (time (2026, 10, 25, 2, 0) 1.0) 0.3m
                value (time (2026, 10, 25, 2, 0) 1.0) (time (2026, 10, 25, 2, 15) 1.0) 0.3m
            ]

    Assert.Empty(ValidationRules.validate message)

[<Fact>]
let ``the March switch is German time only with the new offset`` () =
    // At 2026-03-29 01:00 UTC clocks jump from 02:00+01 to 03:00+02.
    let valid =
        withValues [ value (time (2026, 3, 29, 1, 45) 1.0) (time (2026, 3, 29, 3, 0) 2.0) 0.3m ]

    let wrongOffset =
        withValues [ value (time (2026, 3, 29, 1, 45) 1.0) (time (2026, 3, 29, 2, 0) 1.0) 0.3m ]

    Assert.Empty(ValidationRules.validate valid)
    Assert.Equal<string list>([ "DTM" ], segments (ValidationRules.validate wrongOffset))

[<Fact>]
let ``custom rule lists compose and a different zone can be used`` () =
    let utc = TimeZoneInfo.Utc
    let message = { valid with DocumentDate = time (2026, 1, 16, 5, 30) 0.0 }

    let inUtc = ValidationRules.validateWith (ValidationRules.allIn utc) message

    // The interval values are at +01, so in UTC only they fail; the document date passes.
    Assert.Equal(4, inUtc.Length)
    Assert.All(inUtc, fun e -> Assert.Contains("Interval", e.Message))
    Assert.Empty(ValidationRules.validateWith [ ValidationRules.hasMeteringPoint ] message)
