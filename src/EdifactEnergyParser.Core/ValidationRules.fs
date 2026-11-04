namespace EdifactEnergyParser.Core

open System
open System.Globalization

/// <summary>A business rule violation (Prüffehler) in an otherwise well-formed message.</summary>
type ValidationError =
    {
        /// <summary>Tag of the segment the rule is about, e.g. "NAD" or "QTY".</summary>
        Segment: string
        /// <summary>What is wrong and where, in words a market partner can act on.</summary>
        Message: string
    }

/// <summary>
/// Business rules for <see cref="MsconsMessage"/>. Parsing already guarantees structure and types;
/// these rules check content that is well-formed but not acceptable.
/// </summary>
module ValidationRules =

    /// <summary>A rule returns every violation it finds; an empty list means the message passes.</summary>
    type Rule = MsconsMessage -> ValidationError list

    let private error segment message = { Segment = segment; Message = message }

    let private timeText (value: DateTimeOffset) =
        value.ToString("yyyy-MM-dd HH:mmzzz", CultureInfo.InvariantCulture)

    let private isMarketPartnerId (id: string) =
        id.Length = 13 && String.forall Char.IsAsciiDigit id

    // Only the 13-digit format is checked: the GS1 check digit is not enforced because BDEW
    // test code numbers (99…) do not always carry a valid one.
    let private partyId role qualifier (party: PartyIdentification) =
        if isMarketPartnerId party.Id then
            []
        else
            [ error "NAD" $"{role} (NAD+{qualifier}) '{party.Id}' is not a 13-digit market-partner ID (ILN)" ]

    /// <summary>The sender (NAD+MS) must be a 13-digit market-partner ID (ILN/GLN or BDEW code).</summary>
    let senderIsMarketPartnerId: Rule = fun message -> partyId "Sender" "MS" message.Sender

    /// <summary>The recipient (NAD+MR) must be a 13-digit market-partner ID (ILN/GLN or BDEW code).</summary>
    let recipientIsMarketPartnerId: Rule =
        fun message -> partyId "Recipient" "MR" message.Recipient

    let private intervalValues (message: MsconsMessage) =
        [
            for point in message.MeteringPoints do
                for series in point.TimeSeries do
                    for value in series.Values -> point, value
        ]

    /// <summary>
    /// Every date/time must be a valid time in <paramref name="zone"/>: its UTC offset must be the
    /// one the zone has at that instant. Format 303 lets a sender write any offset, so "12:00+01"
    /// in July parses but names a time no German clock showed.
    /// </summary>
    let dateTimesInZone (zone: TimeZoneInfo) : Rule =
        fun message ->
            let check name (value: DateTimeOffset) =
                let expected = zone.GetUtcOffset value

                if value.Offset = expected then
                    None
                else
                    let local = TimeZoneInfo.ConvertTime(value, zone)

                    Some(
                        error
                            "DTM"
                            $"{name} {timeText value} is not {zone.Id} time; it would be {timeText local}"
                    )

            [
                yield check "Document date (DTM+137)" message.DocumentDate

                for point, value in intervalValues message do
                    yield check $"Interval start (DTM+163) of metering point {point.Id}" value.Start
                    yield check $"Interval end (DTM+164) of metering point {point.Id}" value.End
            ]
            |> List.choose id

    // Zero is a real reading (a PV feed-in register at night, an idle commercial site), so only
    // negative amounts are rejected. Direction is carried by the OBIS code, not by the sign.
    /// <summary>Every metered quantity must be zero or positive.</summary>
    let quantitiesNotNegative: Rule =
        fun message ->
            intervalValues message
            |> List.filter (fun (_, value) -> value.Quantity.Value < 0m)
            |> List.map (fun (point, value) ->
                error
                    "QTY"
                    $"Quantity {value.Quantity.Value.ToString(CultureInfo.InvariantCulture)} of metering point {point.Id} at {timeText value.Start} is negative")

    // The parser enforces this through the grammar, but a model built in code (e.g. by the API or
    // a test generator) can still be empty.
    /// <summary>A message must report at least one metering point (LOC+172).</summary>
    let hasMeteringPoint: Rule =
        fun message ->
            if List.isEmpty message.MeteringPoints then
                [ error "LOC" "Message contains no metering point (LOC+172)" ]
            else
                []

    /// <summary>The standard MSCONS rules with times checked in <paramref name="zone"/>.</summary>
    let allIn (zone: TimeZoneInfo) : Rule list =
        [
            senderIsMarketPartnerId
            recipientIsMarketPartnerId
            dateTimesInZone zone
            quantitiesNotNegative
            hasMeteringPoint
        ]

    /// <summary>The standard MSCONS rules with times checked in German time (Europe/Berlin).</summary>
    let all: Rule list = allIn EnvelopeParser.marketTimeZone

    /// <summary>
    /// Runs every rule and gathers all violations instead of stopping at the first, so a rejection
    /// (APERAK) can list every problem at once.
    /// </summary>
    let validateWith (rules: Rule list) (message: MsconsMessage) : ValidationError list =
        rules |> List.collect (fun rule -> rule message)

    /// <summary>Validates a message against <see cref="all"/>.</summary>
    let validate (message: MsconsMessage) : ValidationError list = validateWith all message

    /// <summary>Returns the message if it passes every rule, or all violations otherwise.</summary>
    let check (message: MsconsMessage) : Result<MsconsMessage, ValidationError list> =
        match validate message with
        | [] -> Ok message
        | errors -> Error errors
