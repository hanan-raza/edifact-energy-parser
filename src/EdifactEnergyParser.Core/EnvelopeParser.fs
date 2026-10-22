namespace EdifactEnergyParser.Core

open System
open System.Globalization

/// <summary>Syntax identifier and version (Syntaxkennung, UNB S001), e.g. UNOC version 3.</summary>
type SyntaxIdentifier =
    {
        /// <summary>Character repertoire, e.g. "UNOC" (ISO 8859-1).</summary>
        Identifier: string
        /// <summary>Syntax version number, "3" or "4".</summary>
        Version: string
    }

/// <summary>
/// A sender or recipient of an interchange (Absender/Empfänger, UNB S002/S003): a market-partner ID
/// plus the code list it comes from.
/// </summary>
type PartyIdentification =
    {
        /// <summary>Market-partner ID (Marktpartner-ID), e.g. a 13-digit BDEW code number or GLN.</summary>
        Id: string
        /// <summary>Code list qualifier, e.g. "14" (GS1), "500" (BDEW), "502" (DVGW).</summary>
        CodeQualifier: string option
    }

/// <summary>Message type, version and guide release (Nachrichten-Kennung, UNH S009).</summary>
type MessageIdentifier =
    {
        /// <summary>Message type, e.g. "MSCONS" or "UTILMD".</summary>
        Type: string
        /// <summary>Directory version, e.g. "D".</summary>
        Version: string
        /// <summary>Directory release, e.g. "04B".</summary>
        Release: string
        /// <summary>Controlling agency, e.g. "UN".</summary>
        ControllingAgency: string
        /// <summary>Association-assigned code; in the German market the guide version, e.g. "2.4c".</summary>
        AssociationCode: string option
    }

/// <summary>A message (Nachricht) between its UNH header and UNT trailer.</summary>
type EdifactMessage =
    {
        /// <summary>Message reference number (Nachrichten-Referenznummer), unique within the interchange.</summary>
        ReferenceNumber: string
        /// <summary>The UNH message identifier.</summary>
        Identifier: MessageIdentifier
        /// <summary>The segments between UNH and UNT, left for the message-type parsers.</summary>
        Segments: Segment list
    }

/// <summary>A complete interchange (Übertragungsdatei) from UNB to UNZ with its messages.</summary>
type InterchangeEnvelope =
    {
        /// <summary>Delimiters from the UNA segment, or the ISO 9735 defaults.</summary>
        Delimiters: Delimiters
        /// <summary>Syntax identifier and version from UNB.</summary>
        Syntax: SyntaxIdentifier
        /// <summary>Interchange sender (Absender).</summary>
        Sender: PartyIdentification
        /// <summary>Interchange recipient (Empfänger).</summary>
        Recipient: PartyIdentification
        /// <summary>Date and time of preparation (Erstellungszeitpunkt).</summary>
        Prepared: DateTimeOffset
        /// <summary>Interchange control reference (Datenaustauschreferenz), repeated in UNZ.</summary>
        ControlReference: string
        /// <summary>Application reference (Anwendungsreferenz), if given.</summary>
        ApplicationReference: string option
        /// <summary>The type shared by all messages, e.g. "MSCONS".</summary>
        MessageType: string
        /// <summary>The messages in input order; at least one.</summary>
        Messages: EdifactMessage list
    }

/// <summary>Why an interchange envelope was rejected.</summary>
type EnvelopeError =
    /// <summary>The text is not well-formed EDIFACT or the UNB/UNH/UNT/UNZ nesting is broken.</summary>
    | SyntaxError of message: string * line: int64 * column: int64
    /// <summary>A service segment has a missing or malformed data element.</summary>
    | InvalidElement of segment: string * line: int64 * column: int64 * reason: string
    /// <summary>UNT declares a different number of segments than the message contains (UNH and UNT included).</summary>
    | SegmentCountMismatch of messageReference: string * declared: int * actual: int
    /// <summary>UNT repeats a different message reference than its UNH.</summary>
    | MessageReferenceMismatch of header: string * trailer: string
    /// <summary>UNZ declares a different number of messages than the interchange contains.</summary>
    | MessageCountMismatch of declared: int * actual: int
    /// <summary>UNZ repeats a different control reference than UNB.</summary>
    | ControlReferenceMismatch of header: string * trailer: string
    /// <summary>The interchange mixes message types, which market-communication rules do not allow.</summary>
    | MixedMessageTypes of types: string list

/// <summary>Parses the service segments UNB/UNH/UNT/UNZ into an <see cref="InterchangeEnvelope"/>.</summary>
module EnvelopeParser =

    type private Located =
        {
            Segment: Segment
            Line: int64
            Column: int64
        }

    type private RawMessage =
        {
            Header: Located
            Body: Segment list
            Trailer: Located
        }

    type private RawInterchange =
        {
            Delimiters: Delimiters
            Header: Located
            Messages: RawMessage list
            Trailer: Located
        }

    // Stage 1: FParsec checks only the nesting UNB (UNH body* UNT)+ UNZ. Element contents are
    // checked afterwards so those errors become typed values instead of parser messages.
    module private Grammar =
        open FParsec

        let private serviceTags = [ "UNB"; "UNH"; "UNT"; "UNZ" ]

        let private serviceSegment (tag: string) (d: Delimiters) : Parser<Located, unit> =
            pipe2 getPosition (followedByString tag <?> $"{tag} segment" >>. Tokenizer.segment d) (fun pos s ->
                {
                    Segment = s
                    Line = pos.Line
                    Column = pos.Column
                })
            .>> Tokenizer.lineBreaks

        let private bodySegment (d: Delimiters) : Parser<Segment, unit> =
            notFollowedBy (choice (List.map skipString serviceTags)) >>. Tokenizer.segment d
            .>> Tokenizer.lineBreaks

        let private message (d: Delimiters) : Parser<RawMessage, unit> =
            pipe3 (serviceSegment "UNH" d) (many (bodySegment d)) (serviceSegment "UNT" d) (fun h b t ->
                { Header = h; Body = b; Trailer = t })

        let private interchange: Parser<RawInterchange, unit> =
            Tokenizer.delimiters
            >>= fun d ->
                pipe3 (serviceSegment "UNB" d) (many1 (message d)) (serviceSegment "UNZ" d) (fun h m t ->
                    {
                        Delimiters = d
                        Header = h
                        Messages = m
                        Trailer = t
                    })
                .>> eof

        let parse (input: string) : Result<RawInterchange, EnvelopeError> =
            match run interchange input with
            | Success(raw, _, _) -> Result.Ok raw
            | Failure(message, error, _) ->
                Result.Error(SyntaxError(message, error.Position.Line, error.Position.Column))

    type private ResultBuilder() =
        member _.Bind(r, f) = Result.bind f r
        member _.Return v = Ok v
        member _.ReturnFrom r = r

    let private result = ResultBuilder()

    let private traverse (f: 'a -> Result<'b, 'e>) (xs: 'a list) : Result<'b list, 'e> =
        List.foldBack
            (fun x acc ->
                result {
                    let! y = f x
                    let! ys = acc
                    return y :: ys
                })
            xs
            (Ok [])

    // Stage 2: typed conversion of single segments.
    let private invalid (loc: Located) reason =
        Error(InvalidElement(loc.Segment.Tag, loc.Line, loc.Column, reason))

    let private required (loc: Located) (element, ``component``) name =
        match loc.Segment.TryGet(element, ``component``) with
        | Some v -> Ok v
        | None -> invalid loc $"{name} is missing"

    let private count (loc: Located) (element, ``component``) name =
        result {
            let! v = required loc (element, ``component``) name

            match Int32.TryParse(v, NumberStyles.None, CultureInfo.InvariantCulture) with
            | true, n -> return n
            | _ -> return! invalid loc $"{name} '{v}' is not a number"
        }

    let private party (loc: Located) element name =
        result {
            let! id = required loc (element, 0) name

            return
                {
                    Id = id
                    CodeQualifier = loc.Segment.TryGet(element, 1)
                }
        }

    // UNB carries no UTC offset; it is the sender's local time. An ambiguous time in the repeated
    // October hour is read as the first pass (daylight time); a time in the March gap cannot exist.
    let private toOffset (zone: TimeZoneInfo) (loc: Located) (local: DateTime) =
        if zone.IsInvalidTime local then
            let text = local.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)
            invalid loc $"Preparation time {text} does not exist in {zone.Id} (DST gap)"
        elif zone.IsAmbiguousTime local then
            Ok(DateTimeOffset(local, Array.max (zone.GetAmbiguousTimeOffsets local)))
        else
            Ok(DateTimeOffset(local, zone.GetUtcOffset local))

    let private prepared (zone: TimeZoneInfo) (loc: Located) =
        result {
            let! date = required loc (3, 0) "Date of preparation"
            let! time = required loc (3, 1) "Time of preparation"

            // Syntax version 3 uses YYMMDD, version 4 CCYYMMDD; accept both regardless of version.
            let dateFormat = if date.Length = 8 then "yyyyMMdd" else "yyMMdd"

            match
                DateTime.TryParseExact(
                    date + time,
                    dateFormat + "HHmm",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None
                )
            with
            | true, local -> return! toOffset zone loc local
            | _ -> return! invalid loc $"Date/time of preparation '{date}:{time}' is not valid"
        }

    let private messageIdentifier (loc: Located) =
        result {
            let! messageType = required loc (1, 0) "Message type"
            let! version = required loc (1, 1) "Message version"
            let! release = required loc (1, 2) "Message release"
            let! agency = required loc (1, 3) "Controlling agency"

            return
                {
                    Type = messageType
                    Version = version
                    Release = release
                    ControllingAgency = agency
                    AssociationCode = loc.Segment.TryGet(1, 4)
                }
        }

    // A message plus what its UNT trailer claims about it, checked later.
    let private message (raw: RawMessage) =
        result {
            let! reference = required raw.Header (0, 0) "Message reference number"
            let! identifier = messageIdentifier raw.Header
            let! declaredSegments = count raw.Trailer (0, 0) "Number of segments"
            let! trailerReference = required raw.Trailer (1, 0) "Message reference number"

            return
                {
                    ReferenceNumber = reference
                    Identifier = identifier
                    Segments = raw.Body
                },
                declaredSegments,
                trailerReference
        }

    // Stage 3: consistency between headers and trailers. All findings are reported at once
    // because a rejection (CONTRL/APERAK) should name every problem, not just the first.
    let private consistencyErrors
        (envelope: InterchangeEnvelope)
        (trailers: (int * string) list)
        (declaredMessages: int)
        (trailerControlReference: string)
        =
        [
            for m, (declaredSegments, trailerReference) in List.zip envelope.Messages trailers do
                // UNH and UNT count themselves.
                let actual = m.Segments.Length + 2

                if declaredSegments <> actual then
                    SegmentCountMismatch(m.ReferenceNumber, declaredSegments, actual)

                if trailerReference <> m.ReferenceNumber then
                    MessageReferenceMismatch(m.ReferenceNumber, trailerReference)

            if declaredMessages <> envelope.Messages.Length then
                MessageCountMismatch(declaredMessages, envelope.Messages.Length)

            if trailerControlReference <> envelope.ControlReference then
                ControlReferenceMismatch(envelope.ControlReference, trailerControlReference)

            let types = envelope.Messages |> List.map (fun m -> m.Identifier.Type) |> List.distinct

            if types.Length > 1 then
                MixedMessageTypes types
        ]

    let private envelope (zone: TimeZoneInfo) (raw: RawInterchange) =
        result {
            let unb = raw.Header
            let! syntaxIdentifier = required unb (0, 0) "Syntax identifier"
            let! syntaxVersion = required unb (0, 1) "Syntax version"
            let! sender = party unb 1 "Sender identification"
            let! recipient = party unb 2 "Recipient identification"
            let! preparedAt = prepared zone unb
            let! controlReference = required unb (4, 0) "Interchange control reference"
            let! messages = traverse message raw.Messages
            let! declaredMessages = count raw.Trailer (0, 0) "Interchange control count"
            let! trailerControlReference = required raw.Trailer (1, 0) "Interchange control reference"

            let parsedMessages = messages |> List.map (fun (m, _, _) -> m)

            return
                {
                    Delimiters = raw.Delimiters
                    Syntax =
                        {
                            Identifier = syntaxIdentifier
                            Version = syntaxVersion
                        }
                    Sender = sender
                    Recipient = recipient
                    Prepared = preparedAt
                    ControlReference = controlReference
                    ApplicationReference = unb.Segment.TryGet(6, 0)
                    MessageType = parsedMessages.Head.Identifier.Type
                    Messages = parsedMessages
                },
                messages |> List.map (fun (_, declared, reference) -> declared, reference),
                declaredMessages,
                trailerControlReference
        }

    /// <summary>The time zone of the German market, used for UNB times unless another is given.</summary>
    let marketTimeZone: TimeZoneInfo = TimeZoneInfo.FindSystemTimeZoneById "Europe/Berlin"

    /// <summary>
    /// Parses an interchange whose UNB preparation time is local time in <paramref name="zone"/>.
    /// Returns every header/trailer inconsistency found, or a single syntax or element error.
    /// </summary>
    let parseIn (zone: TimeZoneInfo) (input: string) : Result<InterchangeEnvelope, EnvelopeError list> =
        Grammar.parse input
        |> Result.bind (envelope zone)
        |> Result.mapError List.singleton
        |> Result.bind (fun (env, trailers, declaredMessages, trailerControlReference) ->
            match consistencyErrors env trailers declaredMessages trailerControlReference with
            | [] -> Ok env
            | errors -> Error errors)

    /// <summary>Parses an interchange whose UNB preparation time is German local time (Europe/Berlin).</summary>
    let parse (input: string) : Result<InterchangeEnvelope, EnvelopeError list> = parseIn marketTimeZone input
