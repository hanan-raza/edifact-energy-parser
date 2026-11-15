module SegmentExplainerTests

open System
open System.IO
open Xunit
open EdifactEnergyParser.Core

let private sample name =
    File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "samples", name))

let private explainOk input =
    match SegmentExplainer.explain input with
    | Ok explained -> explained
    | Error e -> failwithf "Expected Ok but got error at %d:%d: %s" e.Line e.Column e.Message

let private single input =
    match explainOk input with
    | [ only ] -> only
    | other -> failwithf "Expected one segment but got %d" other.Length

let private fieldNamed name (s: SegmentExplanation) =
    match s.Fields |> List.tryFind (fun f -> f.Name = name) with
    | Some f -> f
    | None -> failwithf "%s has no field '%s'; fields: %A" s.Segment name (s.Fields |> List.map _.Name)

// Phrases the explainer uses when it has nothing specific to say about a value.
let private isVague (text: string) =
    text.Contains "not interpreted" || text.Contains "Not interpreted" || text.Contains "not in the explainer"
    || text.Contains "not described" || text.Contains "not recognised" || text.Contains "Cannot be read"

let private assertFullyExplained (explained: SegmentExplanation list) =
    let vague =
        [
            for s in explained do
                if String.IsNullOrWhiteSpace s.Description || isVague s.Description then
                    $"{s.Segment}: {s.Description}"

                for f in s.Fields do
                    if String.IsNullOrWhiteSpace f.Meaning || isVague f.Meaning then
                        $"{s.Segment} {f.Name} = '{f.Value}': {f.Meaning}"
        ]

    Assert.True(List.isEmpty vague, String.concat Environment.NewLine vague)

[<Fact>]
let ``UNB explanation names the sender and recipient ILN`` () =
    let unb = single "UNB+UNOC:3+4012345000023:14+9900000000010:500+260116:0630+MSC0000001'"

    Assert.Equal("UNB", unb.Segment)
    let sender = fieldNamed "Sender identification" unb
    let recipient = fieldNamed "Recipient identification" unb
    Assert.Equal("4012345000023", sender.Value)
    Assert.Contains("sender", sender.Meaning)
    Assert.Equal("9900000000010", recipient.Value)
    Assert.Contains("recipient", recipient.Meaning)
    Assert.Contains("GLN", (fieldNamed "Sender code list" unb).Meaning)
    Assert.Contains("BDEW", (fieldNamed "Recipient code list" unb).Meaning)

[<Fact>]
let ``UNB date and time are shown readably and as local time without offset`` () =
    let unb = single "UNB+UNOC:3+4012345000023:14+9900000000010:500+260116:0630+MSC0000001'"

    Assert.StartsWith("2026-01-16", (fieldNamed "Date of preparation" unb).Meaning)
    Assert.StartsWith("06:30", (fieldNamed "Time of preparation" unb).Meaning)
    Assert.Contains("Latin-1", (fieldNamed "Syntax identifier" unb).Meaning)

[<Fact>]
let ``every segment and value of the MSCONS sample has a specific meaning`` () =
    let explained = explainOk (sample "mscons_example.edi")

    // UNA plus 47 segments from UNB to UNZ.
    Assert.Equal(48, explained.Length)
    Assert.Equal("UNA", explained.Head.Segment)
    Assert.Equal("UNZ", (List.last explained).Segment)
    assertFullyExplained explained

[<Fact>]
let ``every segment and value of the UTILMD sample has a specific meaning`` () =
    let explained = explainOk (sample "utilmd_example.edi")

    Assert.Equal(28, explained.Length)
    assertFullyExplained explained

[<Fact>]
let ``segments written by the APERAK generator are explained`` () =
    let aperak =
        String.concat
            "\n"
            [
                "UNA:+.? '"
                "UNB+UNOC:3+4012345000023:14+9900000000003:500+250701:1015+APK0001'"
                "UNH+1+APERAK:D:07B:UN'"
                "BGM+313+APK0001'"
                "DTM+137:202507011015?+02:303'"
                "RFF+ACW:MSC20251026'"
                "RFF+ACE:MSG0042'"
                "NAD+MS+4012345000023::9'"
                "NAD+MR+9900000000003::293'"
                "ERC+E20'"
                "FTX+AAO+++QTY?: Quantity -1 is negative.'"
                "UNT+10+1'"
                "UNZ+1+APK0001'"
            ]

    assertFullyExplained (explainOk aperak)

[<Fact>]
let ``qualified segments carry the qualifier meaning in their description`` () =
    let dtm = single "DTM+163:202601150000?+01:303'"

    Assert.Equal("Date/time/period (Datum/Uhrzeit/Zeitspanne): Interval start (Beginn des Messzeitraums), inclusive", dtm.Description)

[<Fact>]
let ``DTM format 303 is shown with the offset as sent`` () =
    let dtm = single "DTM+163:202601150000?+01:303'"

    Assert.Equal("2026-01-15 00:00 UTC+01:00 (UTC offset as sent)", (fieldNamed "Date/time" dtm).Meaning)

[<Fact>]
let ``DTM format 102 is read as German local time with the summer offset`` () =
    let dtm = single "DTM+92:20260701:102'"

    Assert.Equal("2026-07-01 00:00 UTC+02:00 (local time in Europe/Berlin)", (fieldNamed "Date/time" dtm).Meaning)

[<Fact>]
let ``DTM in the March DST gap is explained as unreadable instead of failing`` () =
    let dtm = single "DTM+163:202603290230:203'"

    Assert.Contains("DST gap", (fieldNamed "Date/time" dtm).Meaning)

[<Fact>]
let ``released delimiters are shown unescaped`` () =
    let pia = single "PIA+5+1-1?:1.29.0:SRW'"

    let obis = fieldNamed "OBIS code" pia
    Assert.Equal("1-1:1.29.0", obis.Value)
    Assert.Contains("consumed", obis.Meaning)

[<Fact>]
let ``quantity is shown with its unit and status`` () =
    let qty = single "QTY+67:0.205:KWH'"

    Assert.Equal("Metered amount 0.205 kWh", (fieldNamed "Quantity" qty).Meaning)
    Assert.Contains("Substitute value", qty.Description)

[<Fact>]
let ``UNT and UNZ show declared and counted values so a mismatch is visible`` () =
    let explained =
        explainOk "UNB+UNOC:3+4012345000023:14+9900000000010:500+260116:0630+X1'UNH+1+MSCONS:D:04B:UN:2.4c'BGM+7+M1+9'UNT+5+1'UNZ+2+X1'"

    let unt = explained |> List.find (fun s -> s.Segment = "UNT")
    let unz = explained |> List.find (fun s -> s.Segment = "UNZ")
    Assert.Equal("Segments in the message, UNH and UNT included: declared 5, counted 3", (fieldNamed "Number of segments" unt).Meaning)
    Assert.Equal("Messages in the interchange: declared 2, counted 1", (fieldNamed "Interchange control count" unz).Meaning)

[<Fact>]
let ``metering point ID of the wrong length is pointed out`` () =
    let loc = single "LOC+172+DE00012345'"

    Assert.Contains("expected 33 characters, got 10", (fieldNamed "Location ID" loc).Meaning)

[<Fact>]
let ``unknown segments and positions are listed with a generic meaning`` () =
    let s = single "XYZ+A:B++C'"

    Assert.Equal("Segment XYZ is not described by the explainer", s.Description)
    Assert.Equal<string list>([ "A"; "B"; "C" ], s.Fields |> List.map _.Value)
    Assert.Equal("Data element 3, component 1", s.Fields[2].Name)
    Assert.All(s.Fields, fun f -> Assert.False(String.IsNullOrWhiteSpace f.Meaning))

[<Fact>]
let ``unknown codes are named as such`` () =
    let qty = single "QTY+999:1:KWH'"

    Assert.Equal("Quantity qualifier '999' is not in the explainer's code list", (fieldNamed "Quantity qualifier" qty).Meaning)

[<Fact>]
let ``without UNA no service string advice is listed`` () =
    let explained = explainOk "UNH+1+MSCONS:D:04B:UN:2.4c'BGM+7+M1+9'UNT+3+1'"

    Assert.Equal<string list>([ "UNH"; "BGM"; "UNT" ], explained |> List.map _.Segment)

[<Fact>]
let ``custom UNA delimiters are explained and used`` () =
    let explained = explainOk "UNA|*,# !UNH*1*MSCONS|D|04B|UN!"

    let una = explained.Head
    Assert.Equal("|", (fieldNamed "Component separator" una).Value)
    Assert.Equal("!", (fieldNamed "Segment terminator" una).Value)
    Assert.Equal("MSCONS", (fieldNamed "Message type" explained[1]).Value)

[<Fact>]
let ``text that is not EDIFACT is an error with its position`` () =
    match SegmentExplainer.explain "hello world" with
    | Ok explained -> failwithf "Expected an error but got %A" explained
    | Error e ->
        Assert.Equal(1L, e.Line)
        Assert.Equal(1L, e.Column)
