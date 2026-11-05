module EdifactSerializerTests

open System
open System.IO
open Xunit
open EdifactEnergyParser.Core

let private sample () =
    File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "samples", "mscons_example.edi"))

let private parseSingle input =
    match MsconsParser.parse input with
    | Ok [ message ] -> message
    | other -> failwithf "Expected one message but got %A" other

let private time (y, mo, d, h, mi) (offset: float) =
    DateTimeOffset(y, mo, d, h, mi, 0, TimeSpan.FromHours offset)

let private oneValue start ``end`` quantity : MsconsMessage =
    {
        ReferenceNumber = "1"
        DocumentType = "7"
        DocumentNumber = "MSI1"
        DocumentDate = time (2026, 10, 26, 6, 30) 1.0
        Sender = { Id = "9900000000003"; CodeQualifier = Some "293" }
        Recipient = { Id = "9900000000010"; CodeQualifier = Some "293" }
        MeteringPoints =
            [
                {
                    Id = "DE0001234567800000000000000000001"
                    MeterNumber = None
                    TimeSeries =
                        [
                            {
                                ObisCode = None
                                Values =
                                    [
                                        {
                                            Start = start
                                            End = ``end``
                                            Quantity = quantity
                                            Status = TrueValue
                                        }
                                    ]
                            }
                        ]
                }
            ]
    }

let private withoutLineBreaks (text: string) = text.Replace("\r", "").Replace("\n", "")

[<Fact>]
let ``sample MSCONS survives parse, serialize and parse again`` () =
    let parsed = parseSingle (sample ())

    Assert.Equal(parsed, parseSingle (EdifactSerializer.serialize parsed))

[<Fact>]
let ``serialized message body is identical to the sample from UNH to UNT`` () =
    let text = withoutLineBreaks (sample ())
    let unh = text.IndexOf "UNH+"
    let unz = text.IndexOf "UNZ+"
    let expectedMessage = text.Substring(unh, unz - unh)

    let written = EdifactSerializer.serialize (parseSingle (sample ()))

    Assert.Contains(expectedMessage, written)

[<Fact>]
let ``interchange header maps NAD code list 293 to UNB code list 500`` () =
    let written = EdifactSerializer.serialize (parseSingle (sample ()))

    Assert.StartsWith("UNA:+.? 'UNB+UNOC:3+9900000000003:500+9900000000010:500+260116:0630+1'", written)

[<Fact>]
let ``both quarter hours of the repeated October hour keep their own offset`` () =
    let message =
        oneValue (time (2026, 10, 25, 2, 45) 2.0) (time (2026, 10, 25, 2, 0) 1.0) { Value = 0.3m; Unit = KilowattHour }

    let written = EdifactSerializer.serialize message

    Assert.Contains("DTM+163:202610250245?+02:303'DTM+164:202610250200?+01:303'", written)
    Assert.Equal(message, parseSingle written)

[<Fact>]
let ``decimal comma from UNA is used for quantities`` () =
    let delimiters = { Delimiters.Default with DecimalMark = ',' }

    let message =
        oneValue (time (2026, 1, 15, 0, 0) 1.0) (time (2026, 1, 15, 0, 15) 1.0) { Value = 1.125m; Unit = MegawattHour }

    let written = EdifactSerializer.serializeWith delimiters message

    Assert.StartsWith("UNA:+,? '", written)
    Assert.Contains("QTY+220:1,125:MWH'", written)
    Assert.Equal(message, parseSingle written)

[<Fact>]
let ``separators inside values are released`` () =
    let message =
        { oneValue (time (2026, 1, 15, 0, 0) 1.0) (time (2026, 1, 15, 0, 15) 1.0) { Value = 1m; Unit = KilowattHour } with
            DocumentNumber = "A+B:C?D'E"
        }

    let written = EdifactSerializer.serialize message

    Assert.Contains("BGM+7+A?+B?:C??D?'E+9'", written)
    Assert.Equal(message, parseSingle written)
