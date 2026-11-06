module RoundTripProperties

open System
open FsCheck
open FsCheck.FSharp
open FsCheck.Xunit
open EdifactEnergyParser.Core

/// Generators for the MSCONS model. Lists are kept short on purpose: 10 000 cases with a few
/// points, series and values each exercise far more combinations than a few huge messages.
module Gens =

    // Delimiter characters, the release character itself, both decimal marks, a space and
    // non-ASCII Latin-1 letters, so escaping and the UNOC repertoire are both exercised.
    let private unocChar: Gen<char> =
        Gen.frequency
            [
                6, Gen.elements ([ 'A' .. 'Z' ] @ [ 'a' .. 'z' ] @ [ '0' .. '9' ])
                2, Gen.elements [ ':'; '+'; '?'; '\''; '.'; ','; '-'; ' '; '*'; '|'; '!'; '#'; '~' ]
                1, Gen.elements [ 'ä'; 'ö'; 'ü'; 'ß'; 'é'; '§' ]
            ]

    // Characters EDIFACT (UNOC) cannot carry: line breaks, tab, a C1 control, euro sign, CJK.
    let private nonUnocChar: Gen<char> =
        Gen.elements [ '\r'; '\n'; '\t'; '\u0085'; '€'; '中' ]

    let private textOf (char: Gen<char>) : Gen<string> =
        Gen.sized (fun size ->
            gen {
                let! length = Gen.choose (1, max 1 (min size 20))
                let! chars = Gen.arrayOfLength length char
                return String chars
            })

    /// Non-empty texts that EDIFACT can represent.
    let text: Gen<string> = textOf unocChar

    /// Mostly representable texts, sometimes empty or with characters outside UNOC.
    let anyText: Gen<string> =
        Gen.frequency
            [
                // A message has ~20 texts, so rare bad ones still spoil about half the messages.
                60, text
                1, Gen.constant ""
                1, textOf (Gen.frequency [ 4, unocChar; 1, nonUnocChar ])
            ]

    let private offset: Gen<TimeSpan> =
        Gen.frequency
            [
                // Berlin winter and summer time dominate real traffic.
                8, Gen.elements [ TimeSpan.FromHours 1.0; TimeSpan.FromHours 2.0 ]
                2, Gen.elements [ TimeSpan.Zero; TimeSpan.FromHours -5.0; TimeSpan(5, 30, 0); TimeSpan.FromHours 14.0 ]
            ]

    let private dateTimeWith (seconds: Gen<int>) : Gen<DateTimeOffset> =
        gen {
            // 2000-01-01 plus up to ~50 years.
            let! minutes = Gen.choose (0, 50 * 525_600)
            let! seconds = seconds
            let! offset = offset
            let utc = DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMinutes(float minutes).AddSeconds(float seconds)
            return DateTimeOffset(utc).ToOffset offset
        }

    /// Times on whole minutes, as format 303 can carry them.
    let dateTime: Gen<DateTimeOffset> = dateTimeWith (Gen.constant 0)

    /// Mostly whole minutes, sometimes with seconds.
    let anyDateTime: Gen<DateTimeOffset> =
        dateTimeWith (Gen.frequency [ 60, Gen.constant 0; 1, Gen.choose (1, 59) ])

    /// What the free-form parts of a message are drawn from.
    type Content =
        {
            Text: Gen<string>
            DateTime: Gen<DateTimeOffset>
        }

    /// Only content EDIFACT can represent.
    let representable = { Text = text; DateTime = dateTime }

    /// Also empty texts, characters outside UNOC and times with seconds.
    let unrestricted = { Text = anyText; DateTime = anyDateTime }

    let private amount: Gen<decimal> =
        Gen.oneof
            [
                // Meter-like values with three decimals, including zero and negatives.
                Gen.choose (-100_000, 10_000_000) |> Gen.map (fun n -> decimal n / 1000m)
                ArbMap.defaults |> ArbMap.generate<decimal>
                Gen.elements [ Decimal.MaxValue; Decimal.MinValue; 0.0000000000000000000000000001m; 1.50m ]
            ]

    let private quantity: Gen<Quantity> =
        Gen.map2
            (fun value unit -> { Value = value; Unit = unit })
            amount
            (Gen.elements [ KilowattHour; MegawattHour; Kilowatt; Megawatt; CubicMetre ])

    let private intervalValue (c: Content) : Gen<IntervalValue> =
        gen {
            let! start = c.DateTime
            let! minutes = Gen.elements [ 15; 60; 1440 ]
            let! quantity = quantity
            let! status = Gen.elements [ TrueValue; SubstituteValue; ProposedValue; UnusableValue ]

            return
                {
                    Start = start
                    End = start.AddMinutes(float minutes)
                    Quantity = quantity
                    Status = status
                }
        }

    let private between lo hi (g: Gen<'T>) : Gen<'T list> =
        Gen.choose (lo, hi) |> Gen.bind (fun n -> Gen.listOfLength n g)

    let private timeSeries (c: Content) : Gen<TimeSeries> =
        Gen.map2
            (fun obis values -> { ObisCode = obis; Values = values })
            (Gen.optionOf c.Text)
            (between 1 4 (intervalValue c))

    let private meteringPoint (c: Content) : Gen<MeteringPoint> =
        Gen.map3
            (fun id meter series ->
                {
                    Id = id
                    MeterNumber = meter
                    TimeSeries = series
                })
            c.Text
            (Gen.optionOf c.Text)
            (between 1 3 (timeSeries c))

    let private party (c: Content) : Gen<PartyIdentification> =
        Gen.map2 (fun id codeList -> { Id = id; CodeQualifier = codeList }) c.Text (Gen.optionOf c.Text)

    let message (c: Content) : Gen<MsconsMessage> =
        gen {
            let! reference = c.Text
            let! documentType = c.Text
            let! documentNumber = c.Text
            let! documentDate = c.DateTime
            let! sender = party c
            let! recipient = party c
            let! points = between 1 4 (meteringPoint c)

            return
                {
                    ReferenceNumber = reference
                    DocumentType = documentType
                    DocumentNumber = documentNumber
                    DocumentDate = documentDate
                    Sender = sender
                    Recipient = recipient
                    MeteringPoints = points
                }
        }

    let delimiters: Gen<Delimiters> =
        gen {
            let! structural = Gen.shuffle [| ':'; '+'; '?'; '\''; '*'; '|'; '!'; '#'; '~' |]
            let! decimalMark = Gen.elements [ '.'; ',' ]

            return
                {
                    ComponentSeparator = structural[0]
                    DataElementSeparator = structural[1]
                    DecimalMark = decimalMark
                    ReleaseCharacter = structural[2]
                    Reserved = ' '
                    SegmentTerminator = structural[3]
                }
        }

/// Shrinkers that make a failing message smaller while it keeps failing: fewer list items,
/// shorter texts, absent options and plain values, so the reported counterexample is minimal.
module Shrink =

    let private removeEach (xs: 'T list) : seq<'T list> =
        seq {
            if xs.Length > 1 then
                for i in 0 .. xs.Length - 1 -> List.removeAt i xs
        }

    let private each (shrink: 'T -> seq<'T>) (xs: 'T list) : seq<'T list> =
        seq {
            for i in 0 .. xs.Length - 1 do
                for x in shrink xs[i] -> List.updateAt i x xs
        }

    let text (s: string) : seq<string> =
        seq {
            if s <> "A" then
                yield "A"

            if s.Length > 1 then
                for i in 0 .. s.Length - 1 -> s.Remove(i, 1)
        }

    let private option (shrink: 'T -> seq<'T>) (o: 'T option) : seq<'T option> =
        seq {
            match o with
            | Some v ->
                yield None
                yield! shrink v |> Seq.map Some
            | None -> ()
        }

    let private simpleTime = DateTimeOffset(2026, 1, 15, 0, 0, 0, TimeSpan.FromHours 1.0)

    let private intervalValue (v: IntervalValue) : seq<IntervalValue> =
        seq {
            if v.Start <> simpleTime || v.End <> simpleTime.AddMinutes 15.0 then
                { v with
                    Start = simpleTime
                    End = simpleTime.AddMinutes 15.0
                }

            if v.Quantity <> { Value = 1m; Unit = KilowattHour } then
                { v with Quantity = { Value = 1m; Unit = KilowattHour } }

            if v.Status <> TrueValue then
                { v with Status = TrueValue }
        }

    let private timeSeries (s: TimeSeries) : seq<TimeSeries> =
        seq {
            for values in removeEach s.Values -> { s with Values = values }
            for values in each intervalValue s.Values -> { s with Values = values }
            for obis in option text s.ObisCode -> { s with ObisCode = obis }
        }

    let private meteringPoint (p: MeteringPoint) : seq<MeteringPoint> =
        seq {
            for series in removeEach p.TimeSeries -> { p with TimeSeries = series }
            for series in each timeSeries p.TimeSeries -> { p with TimeSeries = series }
            for id in text p.Id -> { p with Id = id }
            for meter in option text p.MeterNumber -> { p with MeterNumber = meter }
        }

    let private party (p: PartyIdentification) : seq<PartyIdentification> =
        seq {
            for id in text p.Id -> { p with Id = id }
            for codeList in option text p.CodeQualifier -> { p with CodeQualifier = codeList }
        }

    let message (m: MsconsMessage) : seq<MsconsMessage> =
        seq {
            for points in removeEach m.MeteringPoints -> { m with MeteringPoints = points }
            for points in each meteringPoint m.MeteringPoints -> { m with MeteringPoints = points }
            for reference in text m.ReferenceNumber -> { m with ReferenceNumber = reference }
            for documentType in text m.DocumentType -> { m with DocumentType = documentType }
            for documentNumber in text m.DocumentNumber -> { m with DocumentNumber = documentNumber }

            if m.DocumentDate <> simpleTime then
                yield { m with DocumentDate = simpleTime }

            for sender in party m.Sender -> { m with Sender = sender }
            for recipient in party m.Recipient -> { m with Recipient = recipient }
        }

    let delimiters (d: Delimiters) : seq<Delimiters> =
        seq {
            if d <> Delimiters.Default then
                Delimiters.Default
        }

/// Messages EDIFACT can represent, with random delimiters.
type Representable =
    static member Message() : Arbitrary<MsconsMessage> =
        Arb.fromGenShrink (Gens.message Gens.representable, Shrink.message)

    static member Delimiters() : Arbitrary<Delimiters> = Arb.fromGenShrink (Gens.delimiters, Shrink.delimiters)

/// Messages that may contain empty texts, characters outside UNOC or times with seconds.
type Unrestricted =
    static member Message() : Arbitrary<MsconsMessage> =
        Arb.fromGenShrink (Gens.message Gens.unrestricted, Shrink.message)

    static member Delimiters() : Arbitrary<Delimiters> = Arb.fromGenShrink (Gens.delimiters, Shrink.delimiters)

let private assertRoundTrip (text: string) (message: MsconsMessage) =
    match MsconsParser.parse text with
    | Ok [ parsed ] when parsed = message -> ()
    | other -> failwithf "Round trip failed.%sText: %s%sParsed: %A" Environment.NewLine text Environment.NewLine other

[<Property(MaxTest = 10000, Arbitrary = [| typeof<Representable> |])>]
let ``parse (serialize msg) = Ok msg for any delimiters`` (delimiters: Delimiters) (message: MsconsMessage) =
    assertRoundTrip (EdifactSerializer.serializeWith delimiters message) message

// The serializer may refuse a message, but whatever it writes must read back unchanged:
// silent corruption (a value that parses to something else) is the failure this guards against.
[<Property(MaxTest = 10000, Arbitrary = [| typeof<Unrestricted> |])>]
let ``serialize either refuses a message or writes text that parses back to it``
    (delimiters: Delimiters)
    (message: MsconsMessage)
    =
    let written =
        try
            Some(EdifactSerializer.serializeWith delimiters message)
        with :? ArgumentException ->
            None

    written |> Option.iter (fun text -> assertRoundTrip text message)

    true
    |> Prop.classify written.IsNone "refused"
    |> Prop.classify written.IsSome "written and read back"
