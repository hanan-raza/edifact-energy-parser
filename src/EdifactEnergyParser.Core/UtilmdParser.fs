namespace EdifactEnergyParser.Core

open System

/// <summary>Why a transaction was sent (Transaktionsgrund, STS+7 9013).</summary>
type TransactionReason =
    /// <summary>Supplier switch (Wechsel, E03): the customer stays and changes supplier.</summary>
    | SupplierChange
    /// <summary>Move in or out (Ein-/Auszug, E01): supply starts or ends because the customer moves.</summary>
    | MoveInOrOut
    /// <summary>Any other reason code, kept as sent.</summary>
    | OtherReason of code: string

/// <summary>A characteristic (Merkmal, CAV) of a master data group.</summary>
type Characteristic =
    {
        /// <summary>Characteristic value code (CAV 7111).</summary>
        Code: string
        /// <summary>Free-text value (CAV 7110), if given.</summary>
        Value: string option
    }

/// <summary>One master data group (Stammdaten, SEQ group) of a transaction, e.g. the data of a market location.</summary>
type MasterDataRecord =
    {
        /// <summary>Group code (SEQ 1229) that says what the characteristics describe.</summary>
        GroupCode: string
        /// <summary>Characteristics in message order; may be empty.</summary>
        Characteristics: Characteristic list
    }

/// <summary>A party named in a transaction (Beteiligter, NAD), e.g. the end customer (Endkunde, Z09).</summary>
type TransactionParty =
    {
        /// <summary>Party function qualifier (NAD 3035).</summary>
        Role: string
        /// <summary>Market-partner ID, if the party is a market participant.</summary>
        Id: string option
        /// <summary>Party name components (NAD C080) joined with ", ", e.g. "Mustermann, Max".</summary>
        Name: string option
    }

/// <summary>One transaction (Vorgang, IDE group) of a supplier switch: who is supplied where and from when.</summary>
type SupplierSwitchTransaction =
    {
        /// <summary>Transaction ID (Vorgangsnummer, IDE+24), echoed in the answer.</summary>
        TransactionId: string
        /// <summary>Why the transaction was sent.</summary>
        Reason: TransactionReason
        /// <summary>Start of supply (Lieferbeginn, DTM+92).</summary>
        StartOfSupply: DateTimeOffset
        /// <summary>End of supply (Lieferende, DTM+93), if already known.</summary>
        EndOfSupply: DateTimeOffset option
        /// <summary>Market location ID (Marktlokations-ID, LOC+Z16), 11 digits.</summary>
        MarketLocationId: string
        /// <summary>Metering location IDs (Messlokations-IDs, LOC+Z17) belonging to the market location.</summary>
        MeteringLocationIds: string list
        /// <summary>Master data groups in message order.</summary>
        MasterData: MasterDataRecord list
        /// <summary>Parties in message order.</summary>
        Parties: TransactionParty list
    }

/// <summary>A utility master data message (UTILMD) carrying supplier switch transactions.</summary>
type UtilmdMessage =
    {
        /// <summary>Message reference number from UNH.</summary>
        ReferenceNumber: string
        /// <summary>Document name code (Dokumentenname, BGM 1001).</summary>
        DocumentType: string
        /// <summary>Document number (Dokumentennummer, BGM 1004).</summary>
        DocumentNumber: string
        /// <summary>Document date (Nachrichtendatum, DTM+137).</summary>
        DocumentDate: DateTimeOffset
        /// <summary>Sending market partner (NAD+MS).</summary>
        Sender: PartyIdentification
        /// <summary>Receiving market partner (NAD+MR).</summary>
        Recipient: PartyIdentification
        /// <summary>Transactions in message order; at least one.</summary>
        Transactions: SupplierSwitchTransaction list
    }

/// <summary>Why a UTILMD interchange or message was rejected.</summary>
// Qualified access keeps the case names from shadowing the identical MsconsError cases.
[<RequireQualifiedAccess>]
type UtilmdError =
    /// <summary>The interchange envelope itself is invalid.</summary>
    | EnvelopeInvalid of EnvelopeError
    /// <summary>The message is of another type, e.g. MSCONS.</summary>
    | NotUtilmd of messageReference: string * messageType: string
    /// <summary>The segment order does not match the UTILMD structure; the position counts UNH as 1.</summary>
    | StructureError of messageReference: string * segmentPosition: int * message: string
    /// <summary>A segment is in the right place but has a missing or invalid element.</summary>
    | InvalidSegment of messageReference: string * segmentPosition: int * segment: string * reason: string

/// <summary>Parses UTILMD messages into the typed <see cref="UtilmdMessage"/> model.</summary>
module UtilmdParser =
    open Results

    type private RawRecord = { Sequence: Located; Values: Located list }

    type private RawTransaction =
        {
            Ident: Located
            Dates: Located list
            Status: Located
            Locations: Located list
            Records: RawRecord list
            Parties: Located list
        }

    type private RawMessage =
        {
            Header: Located
            Dates: Located list
            Parties: Located list
            Transactions: RawTransaction list
        }

    // Stage 1: FParsec checks the segment order.
    module private Grammar =
        open FParsec
        open SegmentGrammar

        // Contact details (CTA/COM) are allowed but not modelled.
        let private party d : P<Located> =
            seg d "NAD" .>> opt (seg d "CTA" >>. skipMany (seg d "COM"))

        let private record d : P<RawRecord> =
            pipe2 (seg d "SEQ") (many (seg d "CAV")) (fun sequence values -> { Sequence = sequence; Values = values })

        // UTILMD has no UNS: the header parties end where the first IDE starts.
        let private transaction d : P<RawTransaction> =
            pipe5
                (seg d "IDE")
                (many (seg d "DTM"))
                (seg d "STS")
                (many1 (seg d "LOC"))
                (tuple2 (many (record d)) (many (seg d "NAD")))
                (fun ident dates status locations (records, parties) ->
                    {
                        Ident = ident
                        Dates = dates
                        Status = status
                        Locations = locations
                        Records = records
                        Parties = parties
                    })

        let private message d : P<RawMessage> =
            pipe4 (seg d "BGM") (many1 (seg d "DTM")) (many1 (party d)) (many1 (transaction d)) (fun header dates parties transactions ->
                {
                    Header = header
                    Dates = dates
                    Parties = parties
                    Transactions = transactions
                })

        let parse (d: Delimiters) (body: Segment list) : Result<RawMessage, int * string> =
            SegmentGrammar.parse (message d) d body

    // Stage 2: typed conversion of the located segments.
    type private Context =
        {
            Reference: string
            Zone: TimeZoneInfo
        }

    let private invalid ctx (loc: Located) reason =
        Error(UtilmdError.InvalidSegment(ctx.Reference, loc.Position, loc.Segment.Tag, reason))

    let private required ctx (loc: Located) (element, ``component``) name =
        match loc.Segment.TryGet(element, ``component``) with
        | Some v -> Ok v
        | None -> invalid ctx loc $"{name} is missing"

    let private dateTime ctx (loc: Located) : Result<string * DateTimeOffset, UtilmdError> =
        result {
            let! qualifier = required ctx loc (0, 0) "Date/time qualifier"
            let! text = required ctx loc (0, 1) "Date/time"
            let! format = required ctx loc (0, 2) "Date/time format"

            let! value =
                DateTimeFormat.parse ctx.Zone format text
                |> Result.mapError (fun reason ->
                    UtilmdError.InvalidSegment(ctx.Reference, loc.Position, loc.Segment.Tag, reason))

            return qualifier, value
        }

    let private dateWith ctx (anchor: Located) qualifier name (dates: (string * DateTimeOffset) list) =
        match List.tryFind (fun (q, _) -> q = qualifier) dates with
        | Some(_, value) -> Ok value
        | None -> invalid ctx anchor $"{name} (DTM+{qualifier}) is missing"

    let private reason ctx (sts: Located) =
        result {
            let! category = required ctx sts (0, 0) "Status category"

            if category <> "7" then
                return! invalid ctx sts $"Status category '{category}' is not a transaction reason (7)"
            else
                let! code = required ctx sts (2, 0) "Transaction reason"

                return
                    match code with
                    | "E03" -> SupplierChange
                    | "E01" -> MoveInOrOut
                    | other -> OtherReason other
        }

    let private location ctx (loc: Located) =
        result {
            let! qualifier = required ctx loc (0, 0) "Location qualifier"
            let! id = required ctx loc (1, 0) "Location ID"

            match qualifier with
            | "Z16"
            | "Z17" -> return qualifier, id
            | other -> return! invalid ctx loc $"Location qualifier '{other}' is not supported"
        }

    let private masterData ctx (raw: RawRecord) =
        result {
            let! groupCode = required ctx raw.Sequence (0, 0) "Group code"

            let! characteristics =
                raw.Values
                |> traverse (fun cav ->
                    required ctx cav (0, 0) "Characteristic code"
                    |> Result.map (fun code -> { Code = code; Value = cav.Segment.TryGet(0, 3) }))

            return
                {
                    GroupCode = groupCode
                    Characteristics = characteristics
                }
        }

    let private transactionParty ctx (loc: Located) =
        result {
            let! role = required ctx loc (0, 0) "Party qualifier"

            let name =
                loc.Segment.Elements
                |> List.tryItem 3
                |> Option.map (List.filter (fun c -> c <> ""))
                |> Option.filter (not << List.isEmpty)
                |> Option.map (String.concat ", ")

            return
                {
                    Role = role
                    Id = loc.Segment.TryGet(1, 0)
                    Name = name
                }
        }

    let private transaction ctx (raw: RawTransaction) =
        result {
            let! id = required ctx raw.Ident (1, 0) "Transaction ID"
            let! reason = reason ctx raw.Status
            let! dates = traverse (dateTime ctx) raw.Dates
            let! start = dateWith ctx raw.Ident "92" "Start of supply" dates
            let ``end`` = dates |> List.tryFind (fun (q, _) -> q = "93") |> Option.map snd
            let! locations = traverse (location ctx) raw.Locations
            let! records = traverse (masterData ctx) raw.Records
            let! parties = traverse (transactionParty ctx) raw.Parties

            let idsWith qualifier =
                locations |> List.filter (fun (q, _) -> q = qualifier) |> List.map snd

            match idsWith "Z16" with
            | [] -> return! invalid ctx raw.Ident "Market location (LOC+Z16) is missing"
            | _ :: _ :: _ -> return! invalid ctx raw.Ident "More than one market location (LOC+Z16)"
            | [ marketLocation ] ->
                if ``end`` |> Option.exists (fun e -> e <= start) then
                    return! invalid ctx raw.Ident "End of supply is not after start of supply"
                else
                    return
                        {
                            TransactionId = id
                            Reason = reason
                            StartOfSupply = start
                            EndOfSupply = ``end``
                            MarketLocationId = marketLocation
                            MeteringLocationIds = idsWith "Z17"
                            MasterData = records
                            Parties = parties
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
            let! transactions = traverse (transaction ctx) raw.Transactions

            return
                {
                    ReferenceNumber = ctx.Reference
                    DocumentType = documentType
                    DocumentNumber = documentNumber
                    DocumentDate = documentDate
                    Sender = sender
                    Recipient = recipient
                    Transactions = transactions
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
        : Result<UtilmdMessage, UtilmdError> =
        let ctx = { Reference = message.ReferenceNumber; Zone = zone }

        if message.Identifier.Type <> "UTILMD" then
            Error(UtilmdError.NotUtilmd(message.ReferenceNumber, message.Identifier.Type))
        else
            Grammar.parse delimiters message.Segments
            |> Result.mapError (fun (position, text) -> UtilmdError.StructureError(ctx.Reference, position, text))
            |> Result.bind (convert ctx)

    /// <summary>
    /// Parses a UTILMD interchange. Envelope errors are returned as they are; otherwise every
    /// message is parsed and the first error of each failing message is reported.
    /// </summary>
    let parseIn (zone: TimeZoneInfo) (input: string) : Result<UtilmdMessage list, UtilmdError list> =
        match EnvelopeParser.parseIn zone input with
        | Error errors -> Error(List.map UtilmdError.EnvelopeInvalid errors)
        | Ok envelope ->
            let results = envelope.Messages |> List.map (parseMessageIn zone envelope.Delimiters)

            match results |> List.choose (function Error e -> Some e | Ok _ -> None) with
            | [] -> Ok(results |> List.choose (function Ok m -> Some m | Error _ -> None))
            | errors -> Error errors

    /// <summary>Parses a UTILMD interchange with local times in German time (Europe/Berlin).</summary>
    let parse (input: string) : Result<UtilmdMessage list, UtilmdError list> =
        parseIn EnvelopeParser.marketTimeZone input
