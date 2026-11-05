namespace EdifactEnergyParser.Core

open System
open System.Globalization

/// <summary>
/// Serializes the typed <see cref="MsconsMessage"/> model back to EDIFACT text, so that
/// <c>MsconsParser.parse (EdifactSerializer.serialize m) = Ok [ m ]</c>.
/// </summary>
module EdifactSerializer =

    let private segment tag (elements: string list list) : Segment = { Tag = tag; Elements = elements }

    // Format 303 (CCYYMMDDHHMMZZZ) keeps the sender's UTC offset, so no time zone is needed
    // to read the value back and both passes of the repeated October hour stay distinct.
    let private dateTime (qualifier: string) (value: DateTimeOffset) : Segment =
        let sign = if value.Offset < TimeSpan.Zero then "-" else "+"
        let hours = abs value.Offset.Hours

        let text =
            value.ToString("yyyyMMddHHmm", CultureInfo.InvariantCulture)
            + sign
            + hours.ToString("00", CultureInfo.InvariantCulture)

        segment "DTM" [ [ qualifier; text; "303" ] ]

    let private decimalText (d: Delimiters) (value: decimal) : string =
        let text = value.ToString(CultureInfo.InvariantCulture)
        if d.DecimalMark = ',' then text.Replace('.', ',') else text

    let private statusCode =
        function
        | TrueValue -> "220"
        | SubstituteValue -> "67"
        | ProposedValue -> "201"
        | UnusableValue -> "20"

    let private unitCode =
        function
        | KilowattHour -> "KWH"
        | MegawattHour -> "MWH"
        | Kilowatt -> "KWT"
        | Megawatt -> "MAW"
        | CubicMetre -> "MTQ"

    let private nad qualifier (party: PartyIdentification) : Segment =
        let id =
            match party.CodeQualifier with
            | Some codeList -> [ party.Id; ""; codeList ]
            | None -> [ party.Id ]

        segment "NAD" [ [ qualifier ]; id ]

    let private intervalValue d (v: IntervalValue) : Segment list =
        [
            segment "QTY" [ [ statusCode v.Status; decimalText d v.Quantity.Value; unitCode v.Quantity.Unit ] ]
            dateTime "163" v.Start
            dateTime "164" v.End
        ]

    let private timeSeries d (lineNumber: int) (s: TimeSeries) : Segment list =
        // PIA+5 = additional product ID; SRW = OBIS code list in the German MSCONS guide.
        let product =
            s.ObisCode
            |> Option.map (fun obis -> segment "PIA" [ [ "5" ]; [ obis; "SRW" ] ])
            |> Option.toList

        segment "LIN" [ [ string lineNumber ] ]
        :: product
        @ List.collect (intervalValue d) s.Values

    let private meteringPoint d (p: MeteringPoint) : Segment list =
        let reference =
            p.MeterNumber
            |> Option.map (fun number -> segment "RFF" [ [ "MG"; number ] ])
            |> Option.toList

        segment "LOC" [ [ "172" ]; [ p.Id ] ]
        :: reference
        @ (p.TimeSeries |> List.mapi (fun i s -> timeSeries d (i + 1) s) |> List.concat)

    /// <summary>
    /// The body segments between UNH and UNT: BGM, DTM+137, NAD+MS/MR, UNS+D, one delivery
    /// party (NAD+DP) and the metering points. Quantities use the decimal mark of <paramref name="d"/>.
    /// </summary>
    let msconsBody (d: Delimiters) (m: MsconsMessage) : Segment list =
        [
            // Message function 9 = original.
            segment "BGM" [ [ m.DocumentType ]; [ m.DocumentNumber ]; [ "9" ] ]
            dateTime "137" m.DocumentDate
            nad "MS" m.Sender
            nad "MR" m.Recipient
            segment "UNS" [ [ "D" ] ]
            segment "NAD" [ [ "DP" ] ]
        ]
        @ List.collect (meteringPoint d) m.MeteringPoints

    /// <summary>The message with its UNH identifier (MSCONS D.04B, guide version 2.4c).</summary>
    let msconsMessage (d: Delimiters) (m: MsconsMessage) : EdifactMessage =
        {
            ReferenceNumber = m.ReferenceNumber
            Identifier =
                {
                    Type = "MSCONS"
                    Version = "D"
                    Release = "04B"
                    ControllingAgency = "UN"
                    AssociationCode = Some "2.4c"
                }
            Segments = msconsBody d m
        }

    // NAD and UNB use different code lists for the same issuing agency.
    let private interchangeParty (p: PartyIdentification) : PartyIdentification =
        let codeList =
            match p.CodeQualifier with
            | Some "293" -> Some "500" // BDEW
            | Some "9" -> Some "14" // GS1
            | Some "332" -> Some "502" // DVGW
            | other -> other

        { p with CodeQualifier = codeList }

    /// <summary>
    /// Wraps messages in an interchange (UNOC, syntax version 3). Sender, recipient and
    /// preparation time come from the first message.
    /// </summary>
    let msconsInterchange
        (d: Delimiters)
        (controlReference: string)
        (messages: MsconsMessage list)
        : InterchangeEnvelope =
        let first = List.head messages

        {
            Delimiters = d
            Syntax = { Identifier = "UNOC"; Version = "3" }
            Sender = interchangeParty first.Sender
            Recipient = interchangeParty first.Recipient
            Prepared = first.DocumentDate
            ControlReference = controlReference
            ApplicationReference = None
            MessageType = "MSCONS"
            Messages = List.map (msconsMessage d) messages
        }

    /// <summary>
    /// Serializes one message as a complete interchange with the given delimiters. The message
    /// reference number doubles as interchange control reference.
    /// </summary>
    let serializeWith (d: Delimiters) (m: MsconsMessage) : string =
        EnvelopeWriter.write (msconsInterchange d m.ReferenceNumber [ m ])

    /// <summary>Serializes one message as a complete interchange with the ISO 9735 default delimiters.</summary>
    let serialize (m: MsconsMessage) : string = serializeWith Delimiters.Default m
