namespace EdifactEnergyParser.Core

open System
open System.Globalization

/// <summary>One value of a segment: what the position is called and what this value means.</summary>
type FieldExplanation =
    {
        /// <summary>Name of the data element or component, e.g. "Sender identification".</summary>
        Name: string
        /// <summary>The value as sent, with release characters already removed.</summary>
        Value: string
        /// <summary>What the value means here, e.g. the text behind a code or a date with its offset.</summary>
        Meaning: string
    }

/// <summary>A human-readable description of one segment (Segment) and its values.</summary>
type SegmentExplanation =
    {
        /// <summary>The segment tag (Segmentkennung), e.g. "UNB".</summary>
        Segment: string
        /// <summary>What the segment is for; for qualified segments (DTM, NAD, LOC, …) including the qualifier's meaning.</summary>
        Description: string
        /// <summary>Every non-empty value of the segment in input order.</summary>
        Fields: FieldExplanation list
    }

/// <summary>
/// Explains EDIFACT text segment by segment ("message explainer"). Covers the envelope and every
/// segment the MSCONS, UTILMD and APERAK code in this project reads or writes.
/// </summary>
module SegmentExplainer =

    // Counts needed to check the UNT and UNZ control values against what was actually sent.
    type private Context =
        {
            Zone: TimeZoneInfo
            SegmentsInMessage: int
            MessagesInInterchange: int
        }

    type private Field =
        {
            Label: string
            Explain: Context -> Segment -> string -> string
        }

    type private Spec =
        {
            Description: string
            // When true, the meaning of the first value (the qualifier) is appended to the description.
            Qualified: bool
            Fields: Map<int * int, Field>
        }

    let private inv = CultureInfo.InvariantCulture

    let private field label explain = { Label = label; Explain = explain }

    let private fixedText (meaning: string) = fun (_: Context) (_: Segment) (_: string) -> meaning

    let private lookup (entries: (string * string) list) (what: string) =
        let table = Map.ofList entries

        fun (_: Context) (_: Segment) (value: string) ->
            match Map.tryFind value table with
            | Some meaning -> meaning
            | None -> $"{what} '{value}' is not in the explainer's code list"

    // Depends on the segment's qualifier (first value), e.g. what a LOC ID or RFF value stands for.
    let private byQualifier (entries: (string * (string -> string)) list) (fallback: string -> string) =
        let table = Map.ofList entries

        fun (_: Context) (s: Segment) (value: string) ->
            match s.TryGet(0, 0) |> Option.bind (fun q -> Map.tryFind q table) with
            | Some explain -> explain value
            | None -> fallback value

    let private spec description qualified fields =
        {
            Description = description
            Qualified = qualified
            Fields = Map.ofList fields
        }

    // UNB uses the ISO 6523 codes (14/500/502), NAD the UNCL 3055 codes (9/293/332) for the same lists.
    let private codeList =
        lookup
            [
                "14", "GS1: the ID is a GLN (Global Location Number, formerly ILN)"
                "9", "GS1: the ID is a GLN (Global Location Number, formerly ILN)"
                "500", "BDEW: the ID is a BDEW code number (BDEW-Codenummer) for electricity"
                "293", "BDEW: the ID is a BDEW code number (BDEW-Codenummer) for electricity"
                "502", "DVGW: the ID is a DVGW code number (DVGW-Codenummer) for gas"
                "332", "DVGW: the ID is a DVGW code number (DVGW-Codenummer) for gas"
            ]
            "Code list"

    let private partyRoles =
        [
            "MS", "Message sender (Absender)"
            "MR", "Message recipient (Empfänger)"
            "DP", "Delivery party (Lieferort): groups the metering points that follow"
            "Z09", "End customer (Endkunde) at the market location"
        ]

    let private dateQualifiers =
        [
            "137", "Document date (Nachrichtendatum): when the message was created"
            "163", "Interval start (Beginn des Messzeitraums), inclusive"
            "164", "Interval end (Ende des Messzeitraums), exclusive"
            "92", "Start of supply (Lieferbeginn)"
            "93", "End of supply (Lieferende)"
        ]

    let private units =
        Map.ofList [ "KWH", "kWh"; "MWH", "MWh"; "KWT", "kW"; "MAW", "MW"; "MTQ", "m³" ]

    let private valueStatuses =
        [
            "220", "True value (Wahrer Wert): read from the meter"
            "67", "Substitute value (Ersatzwert): formed by the metering operator for a missing or implausible reading"
            "201", "Proposed value (Vorschlagswert): a suggestion the recipient may accept or replace"
            "20", "Unusable value (Nicht verwendbarer Wert): must not be used for billing"
        ]

    let private dateValue (ctx: Context) (s: Segment) (value: string) =
        match s.TryGet(0, 2) with
        | None -> "Date/time without a format code (DTM 2379), so it cannot be read"
        | Some format ->
            match DateTimeFormat.parse ctx.Zone format value with
            | Ok instant ->
                let shown = instant.ToString("yyyy-MM-dd HH:mm 'UTC'zzz", inv)

                if format = "303" then
                    $"{shown} (UTC offset as sent)"
                else
                    $"{shown} (local time in {ctx.Zone.Id})"
            | Error reason -> $"Cannot be read: {reason}"

    let private unbDate _ _ (value: string) =
        let pattern = if value.Length = 8 then "yyyyMMdd" else "yyMMdd"

        match DateTime.TryParseExact(value, pattern, inv, DateTimeStyles.None) with
        | true, date -> date.ToString("yyyy-MM-dd", inv) + " (date the sender prepared the interchange)"
        | _ -> $"Not a valid date in {pattern} format"

    let private unbTime _ _ (value: string) =
        match DateTime.TryParseExact(value, "HHmm", inv, DateTimeStyles.None) with
        | true, time -> time.ToString("HH:mm", inv) + " sender's local time (UNB carries no UTC offset)"
        | _ -> "Not a valid time in HHmm format"

    let private quantityValue _ (s: Segment) (value: string) =
        let unit =
            s.TryGet(0, 2)
            |> Option.bind (fun code -> Map.tryFind code units)
            |> Option.defaultValue "(unit not recognised)"

        $"Metered amount {value} {unit}"

    let private locationId =
        let meteringPoint (value: string) =
            if value.Length = 33 then
                $"Metering point ID (Zählpunktbezeichnung), 33 characters, country code {value.Substring(0, 2)}"
            else
                $"Metering point ID (Zählpunktbezeichnung); expected 33 characters, got {value.Length}"

        byQualifier
            [
                "172", meteringPoint
                "Z17", meteringPoint
                "Z16",
                fun value ->
                    if value.Length = 11 && Seq.forall Char.IsAsciiDigit value then
                        "Market location ID (Marktlokations-ID), 11 digits"
                    else
                        $"Market location ID (Marktlokations-ID); expected 11 digits, got '{value}'"
            ]
            (fun _ -> "Location ID for an unrecognised location qualifier")

    let private referenceValue =
        byQualifier
            [
                "MG", fun _ -> "Meter number (Gerätenummer) of the device at this metering point"
                "ACW", fun _ -> "Interchange control reference (UNB) of the message this one answers"
                "ACE", fun _ -> "Message reference (UNH) of the message this one answers"
            ]
            (fun _ -> "Reference value for an unrecognised reference qualifier")

    let private partyId =
        byQualifier
            [
                "MS", fun _ -> "Market-partner ID (Marktpartner-ID) of the message sender"
                "MR", fun _ -> "Market-partner ID (Marktpartner-ID) of the message recipient"
                "Z09", fun _ -> "ID of the end customer"
            ]
            (fun _ -> "Market-partner ID of this party")

    let private characteristicValue =
        let profiles =
            Map.ofList
                [
                    "H0", "household"
                    "G0", "commercial, general"
                    "L0", "agriculture, general"
                ]

        byQualifier
            [
                "Z15",
                fun value ->
                    match Map.tryFind value profiles with
                    | Some kind -> $"Standard load profile {value}: {kind}"
                    | None -> $"Standard load profile {value}"
                "Z16", fun value -> $"{value} kWh per year"
            ]
            (fun _ -> "Value of a characteristic the explainer does not know")

    let private specs: Map<string, Spec> =
        Map.ofList
            [
                "UNB",
                spec
                    "Interchange header (Nutzdaten-Kopfsegment): opens the transmission and names sender and recipient"
                    false
                    [
                        (0, 0),
                        field
                            "Syntax identifier"
                            (lookup
                                [
                                    "UNOA", "Character set A: upper-case letters, digits and a few signs"
                                    "UNOB", "Character set B: upper- and lower-case letters"
                                    "UNOC", "Character set C: Latin-1 (ISO 8859-1), required in the German energy market"
                                    "UNOD", "Character set D: Latin-2 (ISO 8859-2)"
                                ]
                                "Character set")
                        (0, 1),
                        field
                            "Syntax version"
                            (lookup [ "3", "ISO 9735 version 3"; "4", "ISO 9735 version 4" ] "Syntax version")
                        (1, 0), field "Sender identification" (fixedText "Market-partner ID (Marktpartner-ID) of the sender, e.g. a GLN/ILN or BDEW code number")
                        (1, 1), field "Sender code list" codeList
                        (2, 0), field "Recipient identification" (fixedText "Market-partner ID (Marktpartner-ID) of the recipient, e.g. a GLN/ILN or BDEW code number")
                        (2, 1), field "Recipient code list" codeList
                        (3, 0), field "Date of preparation" unbDate
                        (3, 1), field "Time of preparation" unbTime
                        (4, 0), field "Interchange control reference" (fixedText "Sender-assigned ID of this interchange (Datenaustauschreferenz); repeated in UNZ and quoted by the answer (APERAK)")
                        (6, 0), field "Application reference" (fixedText "Tells the recipient which application or process the interchange is for")
                        (10, 0), field "Test indicator" (lookup [ "1", "Test interchange: must not be processed productively" ] "Test indicator")
                    ]

                "UNH",
                spec
                    "Message header (Nachrichten-Kopfsegment): starts one message and says which type and version it is"
                    false
                    [
                        (0, 0), field "Message reference number" (fixedText "Sender-assigned number of this message, unique within the interchange; repeated in UNT")
                        (1, 0),
                        field
                            "Message type"
                            (lookup
                                [
                                    "MSCONS", "Metered services consumption report: meter readings and load profiles"
                                    "UTILMD", "Utilities master data: supplier switch, move in/out and master data changes"
                                    "APERAK", "Application error and acknowledgement: answer to a received message"
                                    "CONTRL", "Syntax and service report: answer about the syntax of an interchange"
                                ]
                                "Message type")
                        (1, 1), field "Message version" (lookup [ "D", "Draft directory (UN/EDIFACT directory version D)" ] "Message version")
                        (1, 2), field "Message release" (fun _ _ value -> $"UN/EDIFACT directory release {value}")
                        (1, 3), field "Controlling agency" (lookup [ "UN", "UN/CEFACT maintains the message definition" ] "Controlling agency")
                        (1, 4), field "Association assigned code" (fun _ _ value -> $"Message guide version {value} that the content follows")
                    ]

                "BGM",
                spec
                    "Beginning of message (Beginn der Nachricht): document type and number"
                    false
                    [
                        (0, 0),
                        field
                            "Document name code"
                            (lookup
                                [
                                    "7", "Process data report (Prozessdatenbericht), e.g. load profiles"
                                    "E01", "Registration (Anmeldung), e.g. a new supplier at a market location"
                                    "312", "Acknowledgement message: the referenced message was accepted"
                                    "313", "Application error message: the referenced message was rejected"
                                ]
                                "Document name code")
                        (1, 0), field "Document number" (fixedText "Sender-assigned number of this business document (Dokumentennummer)")
                        (2, 0),
                        field
                            "Message function"
                            (lookup
                                [
                                    "9", "Original: first transmission of this document"
                                    "1", "Cancellation (Storno) of a previously sent document"
                                    "5", "Replacement of a previously sent document"
                                ]
                                "Message function")
                    ]

                "DTM",
                spec
                    "Date/time/period (Datum/Uhrzeit/Zeitspanne)"
                    true
                    [
                        (0, 0), field "Date/time qualifier" (lookup dateQualifiers "Date/time qualifier")
                        (0, 1), field "Date/time" dateValue
                        (0, 2),
                        field
                            "Date/time format"
                            (lookup
                                [
                                    "102", "CCYYMMDD: a date, read as local time"
                                    "203", "CCYYMMDDHHMM: date and time, read as local time"
                                    "303", "CCYYMMDDHHMMZZZ: date and time with UTC offset in hours"
                                ]
                                "Date/time format")
                    ]

                "NAD",
                spec
                    "Name and address (Name und Adresse)"
                    true
                    [
                        (0, 0), field "Party qualifier" (lookup partyRoles "Party qualifier")
                        (1, 0), field "Party identification" partyId
                        (1, 2), field "Code list" codeList
                        (3, 0), field "Name" (fixedText "Surname or company name")
                        (3, 1), field "First name" (fixedText "First name of a person")
                        (3, 2), field "Name (continued)" (fixedText "Further part of the name")
                    ]

                "CTA",
                spec
                    "Contact information (Ansprechpartner)"
                    true
                    [
                        (0, 0), field "Contact function" (lookup [ "IC", "Information contact for questions about the message" ] "Contact function")
                        (1, 0), field "Contact ID" (fixedText "Identifier of the contact person or department")
                        (1, 1), field "Contact name" (fixedText "Name of the contact person or department")
                    ]

                "COM",
                spec
                    "Communication contact (Kommunikationsverbindung) of the preceding CTA"
                    false
                    [
                        (0, 0), field "Communication number" (fixedText "Telephone number, e-mail address or similar")
                        (0, 1),
                        field
                            "Communication channel"
                            (lookup [ "TE", "Telephone"; "EM", "E-mail"; "FX", "Fax" ] "Communication channel")
                    ]

                "UNS",
                spec
                    "Section control (Abschnitts-Kontrollsegment): separates header and detail section"
                    false
                    [ (0, 0), field "Section identification" (lookup [ "D", "Detail section starts: the metering data follows" ] "Section identification") ]

                "LOC",
                spec
                    "Place/location identification (Ortsangabe)"
                    true
                    [
                        (0, 0),
                        field
                            "Location qualifier"
                            (lookup
                                [
                                    "172", "Metering point (Messlokation / Zählpunkt): the values below belong to it"
                                    "Z16", "Market location (Marktlokation): where energy is supplied and billed"
                                    "Z17", "Metering location (Messlokation) belonging to the market location"
                                ]
                                "Location qualifier")
                        (1, 0), field "Location ID" locationId
                    ]

                "RFF",
                spec
                    "Reference (Referenzangabe)"
                    true
                    [
                        (0, 0),
                        field
                            "Reference qualifier"
                            (lookup
                                [
                                    "MG", "Meter number (Gerätenummer)"
                                    "ACW", "Reference to a previous interchange"
                                    "ACE", "Reference to a previous message"
                                ]
                                "Reference qualifier")
                        (0, 1), field "Reference" referenceValue
                    ]

                "LIN",
                spec
                    "Line item (Positionsdaten): starts one register (time series) of the metering point"
                    false
                    [ (0, 0), field "Line item number" (fixedText "Position of this register within the metering point") ]

                "PIA",
                spec
                    "Additional product ID (Produktidentifikation): which quantity the register measures"
                    false
                    [
                        (0, 0), field "Product ID function" (lookup [ "5", "Product identification: the OBIS code of the register" ] "Product ID function")
                        (1, 0),
                        field
                            "OBIS code"
                            (lookup
                                [
                                    "1-1:1.29.0", "Active energy consumed (A+) per interval: load profile of consumption"
                                    "1-1:2.29.0", "Active energy fed into the grid (A-) per interval: load profile of feed-in"
                                    "1-1:1.8.0", "Active energy consumed (A+): meter reading"
                                    "1-1:2.8.0", "Active energy fed into the grid (A-): meter reading"
                                ]
                                "OBIS code")
                        (1, 1), field "Item type" (lookup [ "SRW", "The ID is an OBIS code (OBIS-Kennzahl)" ] "Item type")
                    ]

                "QTY",
                spec
                    "Quantity (Mengenangabe)"
                    true
                    [
                        (0, 0), field "Quantity qualifier" (lookup valueStatuses "Quantity qualifier")
                        (0, 1), field "Quantity" quantityValue
                        (0, 2),
                        field
                            "Measurement unit"
                            (lookup
                                [
                                    "KWH", "Kilowatt hour: energy"
                                    "MWH", "Megawatt hour: energy"
                                    "KWT", "Kilowatt: average power over the interval"
                                    "MAW", "Megawatt: average power over the interval"
                                    "MTQ", "Cubic metre: gas volume"
                                ]
                                "Measurement unit")
                    ]

                "IDE",
                spec
                    "Identity (Vorgang): starts one transaction"
                    false
                    [
                        (0, 0), field "Object type" (lookup [ "24", "Transaction (Vorgang)" ] "Object type")
                        (1, 0), field "Transaction ID" (fixedText "Transaction ID (Vorgangsnummer), echoed in the answer")
                    ]

                "STS",
                spec
                    "Status (Status)"
                    true
                    [
                        (0, 0), field "Status category" (lookup [ "7", "Transaction reason (Transaktionsgrund)" ] "Status category")
                        (2, 0),
                        field
                            "Status reason"
                            (lookup
                                [
                                    "E01", "Move in or out (Ein-/Auszug): supply starts or ends because the customer moves"
                                    "E03", "Supplier change (Lieferantenwechsel): the customer stays and changes supplier"
                                ]
                                "Status reason")
                    ]

                "SEQ",
                spec
                    "Sequence details (Stammdatengruppe): starts a group of master data characteristics"
                    false
                    [ (0, 0), field "Group code" (lookup [ "Z01", "Master data of the market location" ] "Group code") ]

                "CAV",
                spec
                    "Characteristic value (Merkmal) of the preceding SEQ group"
                    true
                    [
                        (0, 0),
                        field
                            "Characteristic code"
                            (lookup
                                [
                                    "Z15", "Standard load profile (Standardlastprofil) used to estimate consumption"
                                    "Z16", "Forecast annual consumption (Jahresverbrauchsprognose)"
                                ]
                                "Characteristic code")
                        (0, 3), field "Characteristic value" characteristicValue
                    ]

                "ERC",
                spec
                    "Application error information (Fehlercode)"
                    false
                    [
                        (0, 0),
                        field
                            "Error code"
                            (lookup
                                [
                                    "A00", "Accepted: no error"
                                    "E10", "Envelope or syntax problem"
                                    "E20", "Business-rule violation in the message content"
                                ]
                                "Error code")
                    ]

                "FTX",
                spec
                    "Free text (Freitext)"
                    true
                    [
                        (0, 0), field "Text subject" (lookup [ "AAO", "Error description for the preceding ERC" ] "Text subject")
                        (3, 0), field "Text" (fixedText "Free text")
                        (3, 1), field "Text (continued)" (fixedText "Continuation of the free text")
                        (3, 2), field "Text (continued)" (fixedText "Continuation of the free text")
                        (3, 3), field "Text (continued)" (fixedText "Continuation of the free text")
                        (3, 4), field "Text (continued)" (fixedText "Continuation of the free text")
                    ]

                "UNT",
                spec
                    "Message trailer (Nachrichten-Endsegment): ends the message and counts its segments"
                    false
                    [
                        (0, 0),
                        field "Number of segments" (fun ctx _ value ->
                            $"Segments in the message, UNH and UNT included: declared {value}, counted {ctx.SegmentsInMessage}")
                        (1, 0), field "Message reference number" (fixedText "Repeats the UNH message reference number")
                    ]

                "UNZ",
                spec
                    "Interchange trailer (Nutzdaten-Endsegment): ends the transmission and counts its messages"
                    false
                    [
                        (0, 0),
                        field "Interchange control count" (fun ctx _ value ->
                            $"Messages in the interchange: declared {value}, counted {ctx.MessagesInInterchange}")
                        (1, 0), field "Interchange control reference" (fixedText "Repeats the UNB interchange control reference")
                    ]
            ]

    let private notInterpreted = "Not interpreted by the explainer"

    let private explainSegment (ctx: Context) (s: Segment) : SegmentExplanation =
        let spec = Map.tryFind s.Tag specs

        let fields =
            [
                for element, components in List.indexed s.Elements do
                    for ``component``, value in List.indexed components do
                        if value <> "" then
                            match spec |> Option.bind (fun sp -> Map.tryFind (element, ``component``) sp.Fields) with
                            | Some f ->
                                {
                                    Name = f.Label
                                    Value = value
                                    Meaning = f.Explain ctx s value
                                }
                            | None ->
                                {
                                    Name = $"Data element {element + 1}, component {``component`` + 1}"
                                    Value = value
                                    Meaning = notInterpreted
                                }
            ]

        let description =
            match spec with
            | None -> $"Segment {s.Tag} is not described by the explainer"
            | Some sp ->
                match s.TryGet(0, 0), Map.tryFind (0, 0) sp.Fields with
                | Some qualifier, Some f when sp.Qualified -> $"{sp.Description}: {f.Explain ctx s qualifier}"
                | _ -> sp.Description

        {
            Segment = s.Tag
            Description = description
            Fields = fields
        }

    let private explainServiceStringAdvice (d: Delimiters) : SegmentExplanation =
        let separator name (c: char) meaning =
            {
                Name = name
                Value = string c
                Meaning = meaning
            }

        {
            Segment = "UNA"
            Description = "Service string advice (Trennzeichenvorgabe): the six delimiters used in this interchange"
            Fields =
                [
                    separator "Component separator" d.ComponentSeparator "Separates the components of a composite data element"
                    separator "Data element separator" d.DataElementSeparator "Separates the data elements of a segment"
                    separator "Decimal mark" d.DecimalMark "Decimal mark in numbers"
                    separator "Release character" d.ReleaseCharacter "Makes the next character literal, e.g. a '+' inside a value"
                    separator "Reserved" d.Reserved "Repetition separator in syntax version 4; unused (space) in version 3"
                    separator "Segment terminator" d.SegmentTerminator "Ends a segment"
                ]
        }

    /// <summary>
    /// Explains EDIFACT text segment by segment. Local times (DTM formats 102/203) are shown in
    /// <paramref name="zone"/>. The text only has to be tokenizable: fragments and messages that the
    /// parsers reject are explained too, which is the point when looking at a rejected message.
    /// </summary>
    let explainIn (zone: TimeZoneInfo) (input: string) : Result<SegmentExplanation list, TokenizeError> =
        Tokenizer.segments input
        |> Result.map (fun (delimiters, segments) ->
            let start =
                {
                    Zone = zone
                    SegmentsInMessage = 0
                    MessagesInInterchange = 0
                }

            let advance ctx (s: Segment) =
                match s.Tag with
                | "UNB" ->
                    { ctx with
                        SegmentsInMessage = 0
                        MessagesInInterchange = 0
                    }
                | "UNH" ->
                    { ctx with
                        SegmentsInMessage = 1
                        MessagesInInterchange = ctx.MessagesInInterchange + 1
                    }
                | _ -> { ctx with SegmentsInMessage = ctx.SegmentsInMessage + 1 }

            let explained =
                segments
                |> List.mapFold (fun ctx s -> let next = advance ctx s in explainSegment next s, next) start
                |> fst

            if input.StartsWith("UNA", StringComparison.Ordinal) then
                explainServiceStringAdvice delimiters :: explained
            else
                explained)

    /// <summary>Explains EDIFACT text with local times in German time (Europe/Berlin).</summary>
    let explain (input: string) : Result<SegmentExplanation list, TokenizeError> =
        explainIn EnvelopeParser.marketTimeZone input
