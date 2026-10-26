namespace EdifactEnergyParser.Core

open System
open System.Globalization

/// <summary>Unit of a metered quantity (Maßeinheit, QTY 6411) as a UN/ECE Recommendation 20 code.</summary>
type MeasurementUnit =
    /// <summary>Kilowatt hour (KWH): energy, e.g. quarter-hour consumption.</summary>
    | KilowattHour
    /// <summary>Megawatt hour (MWH): energy at grid or balancing level.</summary>
    | MegawattHour
    /// <summary>Kilowatt (KWT): average power over the interval.</summary>
    | Kilowatt
    /// <summary>Megawatt (MAW): average power at grid or balancing level.</summary>
    | Megawatt
    /// <summary>Cubic metre (MTQ): gas volume.</summary>
    | CubicMetre

/// <summary>A metered amount with its unit (Menge).</summary>
type Quantity =
    {
        /// <summary>The amount; decimal so that sums and rounding stay exact.</summary>
        Value: decimal
        /// <summary>The unit the sender reported.</summary>
        Unit: MeasurementUnit
    }

/// <summary>Quality of a metered value (Messwertqualität), taken from the QTY qualifier (6063).</summary>
type ValueStatus =
    /// <summary>True value (Wahrer Wert, 220): read from the meter.</summary>
    | TrueValue
    /// <summary>Substitute value (Ersatzwert, 67): formed by the metering operator for a missing or implausible reading.</summary>
    | SubstituteValue
    /// <summary>Proposed value (Vorschlagswert, 201): a suggestion the recipient may accept or replace.</summary>
    | ProposedValue
    /// <summary>Unusable value (Nicht verwendbarer Wert, 20): transmitted but must not be used for billing.</summary>
    | UnusableValue

/// <summary>One value of a load profile (Lastgang) for the interval [Start, End).</summary>
type IntervalValue =
    {
        /// <summary>Interval start (DTM+163).</summary>
        Start: DateTimeOffset
        /// <summary>Interval end, exclusive (DTM+164).</summary>
        End: DateTimeOffset
        /// <summary>The metered quantity.</summary>
        Quantity: Quantity
        /// <summary>How the value came about.</summary>
        Status: ValueStatus
    }

/// <summary>The values of one register (Zeitreihe, LIN group), identified by its OBIS code.</summary>
type TimeSeries =
    {
        /// <summary>OBIS code (OBIS-Kennzahl, PIA), e.g. "1-1:1.29.0" for active energy consumed per interval.</summary>
        ObisCode: string option
        /// <summary>Values in message order.</summary>
        Values: IntervalValue list
    }

/// <summary>A metering point (Messlokation / Zählpunkt, LOC+172) and its time series.</summary>
type MeteringPoint =
    {
        /// <summary>Metering point ID (Messlokations-ID), 33 characters starting with the country code.</summary>
        Id: string
        /// <summary>Meter number (Gerätenummer, RFF+MG), if given.</summary>
        MeterNumber: string option
        /// <summary>One series per register.</summary>
        TimeSeries: TimeSeries list
    }

/// <summary>A metered services consumption report (MSCONS) with its metering points.</summary>
type MsconsMessage =
    {
        /// <summary>Message reference number from UNH.</summary>
        ReferenceNumber: string
        /// <summary>Document name code (Dokumentenname, BGM 1001), e.g. "7".</summary>
        DocumentType: string
        /// <summary>Document number (Dokumentennummer, BGM 1004).</summary>
        DocumentNumber: string
        /// <summary>Document date (Nachrichtendatum, DTM+137).</summary>
        DocumentDate: DateTimeOffset
        /// <summary>Sending market partner (NAD+MS).</summary>
        Sender: PartyIdentification
        /// <summary>Receiving market partner (NAD+MR).</summary>
        Recipient: PartyIdentification
        /// <summary>Metering points in message order; at least one.</summary>
        MeteringPoints: MeteringPoint list
    }

/// <summary>Why an MSCONS interchange or message was rejected.</summary>
type MsconsError =
    /// <summary>The interchange envelope itself is invalid.</summary>
    | EnvelopeInvalid of EnvelopeError
    /// <summary>The message is of another type, e.g. UTILMD.</summary>
    | NotMscons of messageReference: string * messageType: string
    /// <summary>The segment order does not match the MSCONS structure; the position counts UNH as 1.</summary>
    | StructureError of messageReference: string * segmentPosition: int * message: string
    /// <summary>A segment is in the right place but has a missing or invalid element.</summary>
    | InvalidSegment of messageReference: string * segmentPosition: int * segment: string * reason: string

/// <summary>Parses MSCONS messages into the typed <see cref="MsconsMessage"/> model.</summary>
module MsconsParser =
    open Results

    // Segment positions follow the APERAK convention: UNH is 1, so the first body segment is 2.
    type private Located = { Segment: Segment; Position: int }

    type private RawValue = { Quantity: Located; Dates: Located list }

    type private RawSeries =
        {
            Line: Located
            Product: Located option
            Values: RawValue list
        }

    type private RawPoint =
        {
            Location: Located
            Reference: Located option
            Series: RawSeries list
        }

    type private RawMessage =
        {
            Header: Located
            Dates: Located list
            Parties: Located list
            Points: RawPoint list
        }

    // Stage 1: FParsec checks the segment order. FParsec reads characters, so the body is
    // rendered one segment per line; the line number then gives the segment position.
    module private Grammar =
        open FParsec

        type private P<'T> = Parser<'T, unit>

        let private seg (d: Delimiters) (tag: string) : P<Located> =
            pipe2 getPosition (followedByString tag <?> $"{tag} segment" >>. Tokenizer.segment d) (fun pos s ->
                { Segment = s; Position = int pos.Line + 1 })
            .>> skipNewline

        // Contact details (SG4 CTA/COM) are allowed but not modelled.
        let private party d : P<Located> =
            seg d "NAD" .>> opt (seg d "CTA" >>. skipMany (seg d "COM"))

        let private value d : P<RawValue> =
            pipe2 (seg d "QTY") (many1 (seg d "DTM")) (fun q dates -> { Quantity = q; Dates = dates })

        let private series d : P<RawSeries> =
            pipe3 (seg d "LIN") (opt (seg d "PIA")) (many1 (value d)) (fun line product values ->
                {
                    Line = line
                    Product = product
                    Values = values
                })

        let private point d : P<RawPoint> =
            pipe3 (seg d "LOC") (opt (seg d "RFF")) (many1 (series d)) (fun location reference series ->
                {
                    Location = location
                    Reference = reference
                    Series = series
                })

        // UNS separates the header from the detail section; each delivery party (NAD+DP)
        // groups metering points but carries no data of its own.
        let private message d : P<RawMessage> =
            pipe4
                (seg d "BGM")
                (many1 (seg d "DTM"))
                (many1 (party d))
                (seg d "UNS" >>. many1 (seg d "NAD" >>. many1 (point d)) |>> List.concat)
                (fun header dates parties points ->
                    {
                        Header = header
                        Dates = dates
                        Parties = parties
                        Points = points
                    })
            .>> (eof <?> "end of message")

        let private expected (error: ParserError) : string =
            let labels =
                ErrorMessageList.ToSortedArray error.Messages
                |> Array.choose (function
                    | :? ErrorMessage.Expected as e -> Some e.Label
                    | _ -> None)

            if labels.Length = 0 then "Unexpected segment" else "Expected " + String.Join(" or ", labels)

        let parse (d: Delimiters) (body: Segment list) : Result<RawMessage, int * string> =
            let text =
                body |> List.map (fun s -> EnvelopeWriter.writeSegment d s + "\n") |> String.concat ""

            match run (message d) text with
            | Success(raw, _, _) -> Result.Ok raw
            | Failure(_, error, _) ->
                let position = int error.Position.Line + 1

                // Position past the body means the message ended early, i.e. UNT was found.
                let found =
                    body |> List.tryItem (position - 2) |> Option.map _.Tag |> Option.defaultValue "UNT"

                Result.Error(position, $"{expected error}, found {found}")

    // Stage 2: typed conversion of the located segments.
    type private Context =
        {
            Reference: string
            Zone: TimeZoneInfo
        }

    let private invalid ctx (loc: Located) reason =
        Error(InvalidSegment(ctx.Reference, loc.Position, loc.Segment.Tag, reason))

    let private required ctx (loc: Located) (element, ``component``) name =
        match loc.Segment.TryGet(element, ``component``) with
        | Some v -> Ok v
        | None -> invalid ctx loc $"{name} is missing"

    // Format 303 carries its own UTC offset ("+01"); 102 and 203 are local time in the market zone.
    let private dateTime ctx (loc: Located) : Result<string * DateTimeOffset, MsconsError> =
        let parseExact (text: string) (pattern: string) : DateTime option =
            match DateTime.TryParseExact(text, pattern, CultureInfo.InvariantCulture, DateTimeStyles.None) with
            | true, local -> Some local
            | _ -> None

        let local text pattern =
            match parseExact text pattern with
            | Some value ->
                LocalTime.resolve ctx.Zone value
                |> Result.mapError (fun reason -> InvalidSegment(ctx.Reference, loc.Position, loc.Segment.Tag, reason))
            | None -> invalid ctx loc $"Date/time '{text}' does not match its format"

        let withOffset (text: string) =
            let offset =
                if text.Length = 15 && (text[12] = '+' || text[12] = '-') then
                    match Int32.TryParse(text.Substring 13, NumberStyles.None, CultureInfo.InvariantCulture) with
                    | true, hours when hours <= 14 ->
                        let sign = if text[12] = '-' then -1.0 else 1.0
                        Some(TimeSpan.FromHours(sign * float hours))
                    | _ -> None
                else
                    None

            match parseExact (text.Substring(0, min 12 text.Length)) "yyyyMMddHHmm", offset with
            | Some value, Some offset -> Ok(DateTimeOffset(value, offset))
            | _ -> invalid ctx loc $"Date/time '{text}' does not match format 303 (CCYYMMDDHHMMZZZ)"

        result {
            let! qualifier = required ctx loc (0, 0) "Date/time qualifier"
            let! text = required ctx loc (0, 1) "Date/time"
            let! format = required ctx loc (0, 2) "Date/time format"

            let! value =
                match format with
                | "102" -> local text "yyyyMMdd"
                | "203" -> local text "yyyyMMddHHmm"
                | "303" -> withOffset text
                | other -> invalid ctx loc $"Date/time format '{other}' is not supported"

            return qualifier, value
        }

    let private dateWith ctx (anchor: Located) qualifier name (dates: (string * DateTimeOffset) list) =
        match List.tryFind (fun (q, _) -> q = qualifier) dates with
        | Some(_, value) -> Ok value
        | None -> invalid ctx anchor $"{name} (DTM+{qualifier}) is missing"

    let private quantity ctx (loc: Located) =
        result {
            let! qualifier = required ctx loc (0, 0) "Quantity qualifier"
            let! text = required ctx loc (0, 1) "Quantity"
            let! unitCode = required ctx loc (0, 2) "Measurement unit"

            let! status =
                match qualifier with
                | "220" -> Ok TrueValue
                | "67" -> Ok SubstituteValue
                | "201" -> Ok ProposedValue
                | "20" -> Ok UnusableValue
                | other -> invalid ctx loc $"Quantity qualifier '{other}' is not supported"

            let! unit =
                match unitCode with
                | "KWH" -> Ok KilowattHour
                | "MWH" -> Ok MegawattHour
                | "KWT" -> Ok Kilowatt
                | "MAW" -> Ok Megawatt
                | "MTQ" -> Ok CubicMetre
                | other -> invalid ctx loc $"Measurement unit '{other}' is not supported"

            // ISO 9735 lets the sender use comma or point whatever UNA says, and EDIFACT has no
            // thousands separators, so both can be read as a decimal point. Negative values are
            // kept here; whether they are allowed is a validation rule.
            let! value =
                match
                    Decimal.TryParse(
                        text.Replace(',', '.'),
                        NumberStyles.AllowLeadingSign ||| NumberStyles.AllowDecimalPoint,
                        CultureInfo.InvariantCulture
                    )
                with
                | true, v -> Ok v
                | _ -> invalid ctx loc $"Quantity '{text}' is not a decimal number"

            return status, { Value = value; Unit = unit }
        }

    let private intervalValue ctx (raw: RawValue) =
        result {
            let! status, amount = quantity ctx raw.Quantity
            let! dates = traverse (dateTime ctx) raw.Dates
            let! start = dateWith ctx raw.Quantity "163" "Interval start" dates
            let! ``end`` = dateWith ctx raw.Quantity "164" "Interval end" dates

            if ``end`` <= start then
                return! invalid ctx raw.Quantity "Interval end is not after interval start"
            else
                return
                    {
                        Start = start
                        End = ``end``
                        Quantity = amount
                        Status = status
                    }
        }

    let private timeSeries ctx (raw: RawSeries) =
        result {
            let! values = traverse (intervalValue ctx) raw.Values

            let! obis =
                match raw.Product with
                | Some pia -> required ctx pia (1, 0) "OBIS code" |> Result.map Some
                | None -> Ok None

            return { ObisCode = obis; Values = values }
        }

    let private meteringPoint ctx (raw: RawPoint) =
        result {
            let loc = raw.Location
            let! qualifier = required ctx loc (0, 0) "Location qualifier"

            if qualifier <> "172" then
                return! invalid ctx loc $"Location qualifier '{qualifier}' is not a metering point (172)"
            else
                let! id = required ctx loc (1, 0) "Metering point ID"
                let! series = traverse (timeSeries ctx) raw.Series

                let meterNumber =
                    raw.Reference
                    |> Option.filter (fun rff -> rff.Segment.TryGet(0, 0) = Some "MG")
                    |> Option.bind (fun rff -> rff.Segment.TryGet(0, 1))

                return
                    {
                        Id = id
                        MeterNumber = meterNumber
                        TimeSeries = series
                    }
        }

    let private party ctx (loc: Located) =
        result {
            let! qualifier = required ctx loc (0, 0) "Party qualifier"
            let! id = required ctx loc (1, 0) "Party ID"

            return
                qualifier,
                {
                    Id = id
                    CodeQualifier = loc.Segment.TryGet(1, 2)
                }
        }

    let private partyWith ctx (anchor: Located) qualifier name parties =
        match List.tryFind (fun (q, _) -> q = qualifier) parties with
        | Some(_, p) -> Ok p
        | None -> invalid ctx anchor $"{name} (NAD+{qualifier}) is missing"

    let private convert ctx (raw: RawMessage) =
        result {
            let! documentType = required ctx raw.Header (0, 0) "Document name code"
            let! documentNumber = required ctx raw.Header (1, 0) "Document number"
            let! dates = traverse (dateTime ctx) raw.Dates
            let! documentDate = dateWith ctx raw.Dates.Head "137" "Document date" dates
            let! parties = traverse (party ctx) raw.Parties
            let! sender = partyWith ctx raw.Parties.Head "MS" "Sender" parties
            let! recipient = partyWith ctx raw.Parties.Head "MR" "Recipient" parties
            let! points = traverse (meteringPoint ctx) raw.Points

            return
                {
                    ReferenceNumber = ctx.Reference
                    DocumentType = documentType
                    DocumentNumber = documentNumber
                    DocumentDate = documentDate
                    Sender = sender
                    Recipient = recipient
                    MeteringPoints = points
                }
        }

    /// <summary>
    /// Parses one message of an already parsed envelope. Local times (formats 102/203) are read
    /// in <paramref name="zone"/>; format 303 carries its own offset.
    /// </summary>
    let parseMessageIn
        (zone: TimeZoneInfo)
        (delimiters: Delimiters)
        (message: EdifactMessage)
        : Result<MsconsMessage, MsconsError> =
        let ctx = { Reference = message.ReferenceNumber; Zone = zone }

        if message.Identifier.Type <> "MSCONS" then
            Error(NotMscons(message.ReferenceNumber, message.Identifier.Type))
        else
            Grammar.parse delimiters message.Segments
            |> Result.mapError (fun (position, text) -> StructureError(ctx.Reference, position, text))
            |> Result.bind (convert ctx)

    /// <summary>
    /// Parses an MSCONS interchange. Envelope errors are returned as they are; otherwise every
    /// message is parsed and the first error of each failing message is reported.
    /// </summary>
    let parseIn (zone: TimeZoneInfo) (input: string) : Result<MsconsMessage list, MsconsError list> =
        match EnvelopeParser.parseIn zone input with
        | Error errors -> Error(List.map EnvelopeInvalid errors)
        | Ok envelope ->
            let results = envelope.Messages |> List.map (parseMessageIn zone envelope.Delimiters)

            match results |> List.choose (function Error e -> Some e | Ok _ -> None) with
            | [] -> Ok(results |> List.choose (function Ok m -> Some m | Error _ -> None))
            | errors -> Error errors

    /// <summary>Parses an MSCONS interchange with local times in German time (Europe/Berlin).</summary>
    let parse (input: string) : Result<MsconsMessage list, MsconsError list> =
        parseIn EnvelopeParser.marketTimeZone input
