module EnvelopeParserTests

open System
open Xunit
open EdifactEnergyParser.Core

let private parseOk input =
    match EnvelopeParser.parse input with
    | Ok envelope -> envelope
    | Error errors -> failwithf "Expected Ok but got %A" errors

let private parseErrors input =
    match EnvelopeParser.parse input with
    | Ok envelope -> failwithf "Expected Error but got %A" envelope
    | Error errors -> errors

let private unb = "UNB+UNOC:3+9900000000003:500+9900000000010:500+260115:1200+REF00001'"

let private msconsMessage reference =
    $"UNH+{reference}+MSCONS:D:04B:UN:2.4c'BGM+7+MSI5422+9'UNT+3+{reference}'"

let private minimalInterchange =
    "UNA:+.? '" + unb + msconsMessage "1" + "UNZ+1+REF00001'"

[<Fact>]
let ``minimal valid interchange parses into an envelope`` () =
    let envelope = parseOk minimalInterchange

    Assert.Equal({ Identifier = "UNOC"; Version = "3" }, envelope.Syntax)
    Assert.Equal({ Id = "9900000000003"; CodeQualifier = Some "500" }, envelope.Sender)
    Assert.Equal({ Id = "9900000000010"; CodeQualifier = Some "500" }, envelope.Recipient)
    Assert.Equal(DateTimeOffset(2026, 1, 15, 12, 0, 0, TimeSpan.FromHours 1.0), envelope.Prepared)
    Assert.Equal("REF00001", envelope.ControlReference)
    Assert.Equal(None, envelope.ApplicationReference)
    Assert.Equal("MSCONS", envelope.MessageType)

    let message = Assert.Single(envelope.Messages)
    Assert.Equal("1", message.ReferenceNumber)

    Assert.Equal(
        {
            Type = "MSCONS"
            Version = "D"
            Release = "04B"
            ControllingAgency = "UN"
            AssociationCode = Some "2.4c"
        },
        message.Identifier
    )

    Assert.Equal<Segment list>([ { Tag = "BGM"; Elements = [ [ "7" ]; [ "MSI5422" ]; [ "9" ] ] } ], message.Segments)

[<Fact>]
let ``valid envelope round-trips to identical text`` () =
    let envelope = parseOk minimalInterchange

    Assert.Equal(minimalInterchange, EnvelopeWriter.write envelope)

[<Fact>]
let ``envelope with custom delimiters, escapes and several messages round-trips to the same model`` () =
    let input =
        "UNA|*,# ~\r\n"
        + "UNB*UNOC|3*9900000000003|500*4012345000023|14*260115|1200*REF#*2**TL~\r\n"
        + "UNH*A1*MSCONS|D|04B|UN|2.4c~BGM*7*MSI#~42*9~NAD*MS**||293*~UNT*4*A1~\r\n"
        + "UNH*A2*MSCONS|D|04B|UN|2.4c~UNT*2*A2~\r\n"
        + "UNZ*2*REF#*2~"

    let envelope = parseOk input
    let reparsed = parseOk (EnvelopeWriter.write envelope)

    Assert.Equal(envelope, reparsed)
    Assert.Equal("REF*2", envelope.ControlReference)
    Assert.Equal(Some "TL", envelope.ApplicationReference)
    Assert.Equal(2, envelope.Messages.Length)

[<Fact>]
let ``mismatched segment count in UNT is an error`` () =
    let input = "UNA:+.? '" + unb + "UNH+1+MSCONS:D:04B:UN:2.4c'BGM+7+MSI5422+9'UNT+4+1'UNZ+1+REF00001'"

    let errors = parseErrors input

    Assert.Equal<EnvelopeError list>([ SegmentCountMismatch("1", 4, 3) ], errors)

[<Fact>]
let ``mismatched message count in UNZ is an error`` () =
    let errors = parseErrors (unb + msconsMessage "1" + "UNZ+2+REF00001'")

    Assert.Equal<EnvelopeError list>([ MessageCountMismatch(2, 1) ], errors)

[<Fact>]
let ``mismatched references in trailers are errors`` () =
    let input = unb + "UNH+1+MSCONS:D:04B:UN:2.4c'UNT+2+9'UNZ+1+OTHER'"

    let errors = parseErrors input

    Assert.Equal<EnvelopeError list>(
        [ MessageReferenceMismatch("1", "9"); ControlReferenceMismatch("REF00001", "OTHER") ],
        errors
    )

[<Fact>]
let ``all trailer inconsistencies are reported together`` () =
    let input = unb + "UNH+1+MSCONS:D:04B:UN:2.4c'UNT+5+1'UNH+2+MSCONS:D:04B:UN:2.4c'UNT+1+2'UNZ+3+REF00001'"

    let errors = parseErrors input

    Assert.Equal<EnvelopeError list>(
        [ SegmentCountMismatch("1", 5, 2); SegmentCountMismatch("2", 1, 2); MessageCountMismatch(3, 2) ],
        errors
    )

[<Fact>]
let ``messages of different types in one interchange are rejected`` () =
    let input =
        unb + msconsMessage "1" + "UNH+2+UTILMD:D:11A:UN:5.2e'UNT+2+2'UNZ+2+REF00001'"

    let errors = parseErrors input

    Assert.Equal<EnvelopeError list>([ MixedMessageTypes [ "MSCONS"; "UTILMD" ] ], errors)

[<Fact>]
let ``missing UNT is a syntax error at the next service segment`` () =
    let errors = parseErrors (unb + "UNH+1+MSCONS:D:04B:UN:2.4c'BGM+7'\nUNZ+1+REF00001'")

    match errors with
    | [ SyntaxError(message, line, column) ] ->
        Assert.Contains("UNT segment", message)
        Assert.Equal(2L, line)
        Assert.Equal(1L, column)
    | other -> failwithf "Unexpected %A" other

[<Fact>]
let ``missing UNZ is a syntax error`` () =
    match parseErrors (unb + msconsMessage "1") with
    | [ SyntaxError(message, _, _) ] -> Assert.Contains("UNZ segment", message)
    | other -> failwithf "Unexpected %A" other

[<Fact>]
let ``interchange without messages is a syntax error`` () =
    match parseErrors (unb + "UNZ+0+REF00001'") with
    | [ SyntaxError(message, _, _) ] -> Assert.Contains("UNH segment", message)
    | other -> failwithf "Unexpected %A" other

[<Fact>]
let ``non-numeric segment count is an invalid element with its position`` () =
    let errors = parseErrors (unb + "UNH+1+MSCONS:D:04B:UN:2.4c'\nUNT+x+1'UNZ+1+REF00001'")

    Assert.Equal<EnvelopeError list>(
        [ InvalidElement("UNT", 2L, 1L, "Number of segments 'x' is not a number") ],
        errors
    )

[<Fact>]
let ``missing sender is an invalid element`` () =
    let errors = parseErrors ("UNB+UNOC:3++9900000000010:500+260115:1200+R1'" + msconsMessage "1" + "UNZ+1+R1'")

    Assert.Equal<EnvelopeError list>([ InvalidElement("UNB", 1L, 1L, "Sender identification is missing") ], errors)

[<Fact>]
let ``syntax version 4 date with century is accepted`` () =
    let input =
        "UNB+UNOC:4+9900000000003:500+9900000000010:500+20260115:1200+R1'" + msconsMessage "1" + "UNZ+1+R1'"

    let envelope = parseOk input

    Assert.Equal(DateTimeOffset(2026, 1, 15, 12, 0, 0, TimeSpan.FromHours 1.0), envelope.Prepared)
    Assert.Equal(input, (EnvelopeWriter.write envelope).Substring "UNA:+.? '".Length)

let private withPreparation dateTime =
    $"UNB+UNOC:3+9900000000003:500+9900000000010:500+{dateTime}+R1'" + msconsMessage "1" + "UNZ+1+R1'"

[<Fact>]
let ``preparation time in summer is read with daylight offset`` () =
    let envelope = parseOk (withPreparation "260715:0800")

    Assert.Equal(TimeSpan.FromHours 2.0, envelope.Prepared.Offset)

[<Fact>]
let ``preparation time in the repeated October hour is read as daylight time`` () =
    let envelope = parseOk (withPreparation "261025:0230")

    Assert.Equal(DateTimeOffset(2026, 10, 25, 0, 30, 0, TimeSpan.Zero), envelope.Prepared.ToUniversalTime())
    Assert.Equal(TimeSpan.FromHours 2.0, envelope.Prepared.Offset)

[<Fact>]
let ``preparation time in the skipped March hour is an invalid element`` () =
    match parseErrors (withPreparation "260329:0230") with
    | [ InvalidElement("UNB", _, _, reason) ] -> Assert.Contains("DST gap", reason)
    | other -> failwithf "Unexpected %A" other

[<Fact>]
let ``preparation time is interpreted in the given zone`` () =
    match EnvelopeParser.parseIn TimeZoneInfo.Utc (withPreparation "260115:1200") with
    | Ok envelope -> Assert.Equal(DateTimeOffset(2026, 1, 15, 12, 0, 0, TimeSpan.Zero), envelope.Prepared)
    | Error errors -> failwithf "Unexpected %A" errors
