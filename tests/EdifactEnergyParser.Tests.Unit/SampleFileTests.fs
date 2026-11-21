/// Keeps the files in docs/samples honest: the README and the .http file point readers at them,
/// so each one must still parse (and be rejected) exactly the way the docs describe.
module SampleFileTests

open System
open System.IO
open Xunit
open EdifactEnergyParser.Core

let private samplesDirectory = Path.Combine(AppContext.BaseDirectory, "samples")

let private sample name =
    File.ReadAllText(Path.Combine(samplesDirectory, name))

let private time (y, mo, d, h, mi) (offset: float) =
    DateTimeOffset(y, mo, d, h, mi, 0, TimeSpan.FromHours offset)

let private msconsOk name =
    match MsconsParser.parse (sample name) with
    | Ok [ message ] -> message
    | other -> failwithf "%s: expected one MSCONS message but got %A" name other

let private utilmdOk name =
    match UtilmdParser.parse (sample name) with
    | Ok [ message ] -> message
    | other -> failwithf "%s: expected one UTILMD message but got %A" name other

let private sampleNames prefix =
    DirectoryInfo(samplesDirectory).GetFiles($"{prefix}*.edi")
    |> Array.map _.Name
    |> Array.sort
    |> Array.toList

[<Fact>]
let ``samples folder holds every documented sample`` () =
    Assert.Equal<string list>(
        [ "mscons_dst_october.edi"; "mscons_example.edi"; "mscons_rejected.edi" ],
        sampleNames "mscons_"
    )

    Assert.Equal<string list>([ "utilmd_example.edi"; "utilmd_move_in.edi" ], sampleNames "utilmd_")

[<Fact>]
let ``every MSCONS sample except the rejected one passes all validation rules`` () =
    for name in sampleNames "mscons_" |> List.filter ((<>) "mscons_rejected.edi") do
        let errors = ValidationRules.validate (msconsOk name)
        Assert.True(List.isEmpty errors, $"{name}: %A{errors}")

[<Fact>]
let ``every sample is explained without a single vague meaning`` () =
    for name in sampleNames "" do
        match SegmentExplainer.explain (sample name) with
        | Error e -> failwithf "%s: explainer failed at %d:%d: %s" name e.Line e.Column e.Message
        | Ok explained ->
            let vague =
                explained
                |> List.collect (fun s -> s.Fields |> List.map (fun f -> s.Segment, f))
                |> List.filter (fun (_, f) ->
                    String.IsNullOrWhiteSpace f.Meaning
                    || f.Meaning.Contains "not interpreted"
                    || f.Meaning.Contains "Not interpreted"
                    || f.Meaning.Contains "not in the explainer")

            Assert.True(List.isEmpty vague, $"{name}: %A{vague}")

[<Fact>]
let ``October DST sample covers the repeated hour with fifteen-minute intervals`` () =
    let message = msconsOk "mscons_dst_october.edi"
    let series = message.MeteringPoints |> Assert.Single |> _.TimeSeries |> Assert.Single
    let values = series.Values

    Assert.Equal(8, values.Length)
    Assert.Equal(0.828m, values |> List.sumBy _.Quantity.Value)
    Assert.All(values, fun v -> Assert.Equal(TimeSpan.FromMinutes 15.0, v.End - v.Start))

    for a, b in List.pairwise values do
        Assert.Equal(a.End, b.Start)

    // 02:00 local time occurs twice; the offset is what tells the two quarter hours apart.
    let atTwo = values |> List.filter (fun v -> v.Start.Hour = 2 && v.Start.Minute = 0)
    Assert.Equal<DateTimeOffset list>([ time (2026, 10, 25, 2, 0) 2.0; time (2026, 10, 25, 2, 0) 1.0 ], atTwo |> List.map _.Start)
    Assert.Equal(time (2026, 10, 25, 2, 0) 1.0, values[4].End)

[<Fact>]
let ``October DST sample survives serialize and parse`` () =
    let message = msconsOk "mscons_dst_october.edi"

    Assert.Equal(Ok [ message ], MsconsParser.parse (EdifactSerializer.serialize message))

[<Fact>]
let ``rejected sample parses but breaks three rules`` () =
    let errors = ValidationRules.validate (msconsOk "mscons_rejected.edi")

    Assert.Equal<string list>([ "DTM"; "NAD"; "QTY" ], errors |> List.map _.Segment |> List.distinct |> List.sort)
    Assert.Contains(errors, fun e -> e.Segment = "NAD" && e.Message.Contains "990000000003")
    Assert.Contains(errors, fun e -> e.Segment = "QTY" && e.Message.Contains "-0.412")
    Assert.Contains(errors, fun e -> e.Segment = "DTM" && e.Message.Contains "+01:00")

[<Fact>]
let ``UTILMD move-in sample starts supply on the March DST day and after it`` () =
    let message = utilmdOk "utilmd_move_in.edi"

    match message.Transactions with
    | [ moveIn; change ] ->
        Assert.Equal(MoveInOrOut, moveIn.Reason)
        // Format 102 is a local date: midnight on 28 March is still winter time.
        Assert.Equal(time (2027, 3, 28, 0, 0) 1.0, moveIn.StartOfSupply)
        Assert.Equal(None, moveIn.EndOfSupply)
        Assert.Equal("47110815002", moveIn.MarketLocationId)

        Assert.Equal<string list>(
            [ "DE0001234567800000000000000000004"; "DE0001234567800000000000000000005" ],
            moveIn.MeteringLocationIds
        )

        Assert.Equal(Some "Mueller + Soehne GbR", (Assert.Single moveIn.Parties).Name)

        Assert.Equal(SupplierChange, change.Reason)
        Assert.Equal(time (2027, 4, 1, 0, 0) 2.0, change.StartOfSupply)
        Assert.Empty(change.MeteringLocationIds)
        Assert.Equal(Some "Weber, Jonas", (Assert.Single change.Parties).Name)
    | other -> failwithf "Expected two transactions but got %A" other
