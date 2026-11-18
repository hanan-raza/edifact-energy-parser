module ValidationProperties

open System
open FsCheck
open FsCheck.FSharp
open FsCheck.Xunit
open EdifactEnergyParser.Core
open RoundTripProperties

/// A rule violation planted into an otherwise valid message.
type Fault =
    | InvalidSender
    | InvalidRecipient
    | NegativeQuantity
    | ForeignOffset
    | NoMeteringPoint

/// A message together with the faults planted into it; at least one.
type InvalidMessage = InvalidMessage of MsconsMessage * Fault list

module private ValidationGens =

    let private zone = EnvelopeParser.marketTimeZone

    let private toMarketTime (value: DateTimeOffset) = TimeZoneInfo.ConvertTime(value, zone)

    let private marketPartnerId: Gen<string> =
        Gen.arrayOfLength 13 (Gen.elements [ '0' .. '9' ]) |> Gen.map String

    let private notMarketPartnerId: Gen<string> =
        Gens.text |> Gen.filter (fun id -> not (id.Length = 13 && String.forall Char.IsAsciiDigit id))

    let private mapValues (f: IntervalValue -> IntervalValue) (m: MsconsMessage) =
        { m with
            MeteringPoints =
                m.MeteringPoints
                |> List.map (fun p ->
                    { p with
                        TimeSeries = p.TimeSeries |> List.map (fun s -> { s with Values = List.map f s.Values })
                    })
        }

    /// A representable message repaired until it passes every rule: 13-digit party IDs, every
    /// time in German time (same instant, Berlin offset) and no negative quantity.
    let valid: Gen<MsconsMessage> =
        gen {
            let! message = Gens.message Gens.representable
            let! senderId = marketPartnerId
            let! recipientId = marketPartnerId

            return
                { message with
                    Sender = { message.Sender with Id = senderId }
                    Recipient = { message.Recipient with Id = recipientId }
                    DocumentDate = toMarketTime message.DocumentDate
                }
                |> mapValues (fun v ->
                    { v with
                        Start = toMarketTime v.Start
                        End = toMarketTime v.End
                        Quantity = { v.Quantity with Value = abs v.Quantity.Value }
                    })
        }

    let private inject (message: MsconsMessage) (fault: Fault) : Gen<MsconsMessage> =
        match fault with
        | InvalidSender -> notMarketPartnerId |> Gen.map (fun id -> { message with Sender = { message.Sender with Id = id } })
        | InvalidRecipient ->
            notMarketPartnerId |> Gen.map (fun id -> { message with Recipient = { message.Recipient with Id = id } })
        | NegativeQuantity ->
            Gen.choose (1, 10_000_000)
            |> Gen.map (fun n ->
                let negative = -(decimal n / 1000m)
                // Every value goes negative, so the fault survives whichever point is kept.
                message |> mapValues (fun v -> { v with Quantity = { v.Quantity with Value = negative } }))
        // Berlin is never UTC+05, so the same instant written with +05 is always the wrong offset.
        | ForeignOffset -> Gen.constant { message with DocumentDate = message.DocumentDate.ToOffset(TimeSpan.FromHours 5.0) }
        | NoMeteringPoint -> Gen.constant { message with MeteringPoints = [] }

    let private faults: Gen<Fault list> =
        Gen.subListOf [ InvalidSender; InvalidRecipient; NegativeQuantity; ForeignOffset; NoMeteringPoint ]
        |> Gen.filter (not << List.isEmpty)
        // Without metering points there is no quantity left to be negative.
        |> Gen.filter (fun fs -> not (List.contains NoMeteringPoint fs && List.contains NegativeQuantity fs))

    let invalid: Gen<InvalidMessage> =
        gen {
            let! message = valid
            let! faults = faults

            let! broken =
                faults
                |> List.fold (fun acc fault -> acc |> Gen.bind (fun m -> inject m fault)) (Gen.constant message)

            return InvalidMessage(broken, faults)
        }

    /// Valid, invalid and unrestricted (possibly unrepresentable) messages in equal parts.
    let mixed: Gen<MsconsMessage> =
        Gen.oneof
            [
                valid
                invalid |> Gen.map (fun (InvalidMessage(m, _)) -> m)
                Gens.message Gens.unrestricted
            ]

type Valid =
    static member Message() : Arbitrary<MsconsMessage> = Arb.fromGen ValidationGens.valid

type Invalid =
    static member Message() : Arbitrary<InvalidMessage> = Arb.fromGen ValidationGens.invalid

    static member Delimiters() : Arbitrary<Delimiters> = Arb.fromGenShrink (Gens.delimiters, Shrink.delimiters)

type Mixed =
    static member Message() : Arbitrary<MsconsMessage> = Arb.fromGen ValidationGens.mixed

let private ruleSegment =
    function
    | InvalidSender
    | InvalidRecipient -> "NAD"
    | NegativeQuantity -> "QTY"
    | ForeignOffset -> "DTM"
    | NoMeteringPoint -> "LOC"

// Guards the generator below: if the repair missed a rule, the faults would not be the only
// cause of the errors and the next property would prove less than it claims.
[<Property(MaxTest = 10000, Arbitrary = [| typeof<Valid> |])>]
let ``repaired messages pass every rule`` (message: MsconsMessage) =
    let errors = ValidationRules.validate message

    if not errors.IsEmpty then
        failwithf "Expected no errors but got %A" errors

// A rejected message must still be writable: the APERAK and the audit trail refer to it, and
// tests build invalid messages as text. The serializer only refuses unrepresentable content.
[<Property(MaxTest = 10000, Arbitrary = [| typeof<Invalid> |])>]
let ``a message with validation errors serializes without crashing`` (InvalidMessage(message, faults)) (delimiters: Delimiters) =
    let errors = ValidationRules.validate message
    let written = EdifactSerializer.serializeWith delimiters message

    for fault in faults do
        if not (errors |> List.exists (fun e -> e.Segment = ruleSegment fault)) then
            failwithf "%A was planted but not reported: %A" fault errors

    // MSCONS needs at least one LOC group, so only messages with metering points parse back.
    if not message.MeteringPoints.IsEmpty then
        match MsconsParser.parse written with
        | Ok [ parsed ] when parsed = message -> ()
        | other -> failwithf "Invalid message did not read back.%sText: %s%sParsed: %A" Environment.NewLine written Environment.NewLine other

    true |> Prop.collect (List.length faults |> sprintf "%d fault(s)")

[<Property(MaxTest = 10000, Arbitrary = [| typeof<Mixed> |])>]
let ``validate is idempotent`` (message: MsconsMessage) =
    let first = ValidationRules.validate message
    let second = ValidationRules.validate message

    if first <> second then
        failwithf "First call: %A%sSecond call: %A" first Environment.NewLine second

    // The same rules fire across a serialize → parse round trip, so the APERAK sent for a
    // message names the problems the sender finds in their own copy. Only the segments are
    // compared: a +05:30 time is written in UTC (same instant), so its error text then shows
    // +00:00 instead of +05:30.
    let readBack =
        try
            match MsconsParser.parse (EdifactSerializer.serialize message) with
            | Ok [ parsed ] -> Some parsed
            | _ -> None
        with :? ArgumentException ->
            None

    readBack
    |> Option.iter (fun parsed ->
        let third = ValidationRules.validate parsed
        let segments = List.map _.Segment

        if segments third <> segments first then
            failwithf "Before round trip: %A%sAfter: %A" first Environment.NewLine third)

    // `check` is the Result-returning entry point; it must decide exactly as validate does.
    match ValidationRules.check message, first with
    | Ok m, [] when m = message -> ()
    | Error errors, _ :: _ when errors = first -> ()
    | result, _ -> failwithf "check disagrees with validate: %A vs %A" result first

    true
    |> Prop.classify first.IsEmpty "valid"
    |> Prop.classify (not first.IsEmpty) "invalid"
    |> Prop.classify readBack.IsSome "also validated after round trip"
