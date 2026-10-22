namespace EdifactEnergyParser.Core

open System
open System.Globalization
open System.Text

/// <summary>Serializes an <see cref="InterchangeEnvelope"/> back to EDIFACT text.</summary>
module EnvelopeWriter =

    let private escape (d: Delimiters) (value: string) : string =
        let sb = StringBuilder(value.Length)

        for c in value do
            if
                c = d.ComponentSeparator
                || c = d.DataElementSeparator
                || c = d.ReleaseCharacter
                || c = d.SegmentTerminator
            then
                sb.Append(d.ReleaseCharacter) |> ignore

            sb.Append(c) |> ignore

        sb.ToString()

    /// <summary>Writes one segment. Empty elements are kept, so a parsed segment is reproduced exactly.</summary>
    let writeSegment (d: Delimiters) (segment: Segment) : string =
        let element (components: string list) =
            components |> List.map (escape d) |> String.concat (string d.ComponentSeparator)

        let elements =
            segment.Elements
            |> List.map (fun e -> string d.DataElementSeparator + element e)
            |> String.concat ""

        segment.Tag + elements + string d.SegmentTerminator

    let private serviceStringAdvice (d: Delimiters) : string =
        String(
            [|
                'U'
                'N'
                'A'
                d.ComponentSeparator
                d.DataElementSeparator
                d.DecimalMark
                d.ReleaseCharacter
                d.Reserved
                d.SegmentTerminator
            |]
        )

    let private party (p: PartyIdentification) : string list =
        p.Id :: Option.toList p.CodeQualifier

    let private unb (zone: TimeZoneInfo) (e: InterchangeEnvelope) : Segment =
        let local = TimeZoneInfo.ConvertTime(e.Prepared, zone)
        let dateFormat = if e.Syntax.Version = "4" then "yyyyMMdd" else "yyMMdd"

        let applicationReference =
            match e.ApplicationReference with
            | Some reference -> [ [ "" ]; [ reference ] ]
            | None -> []

        {
            Tag = "UNB"
            Elements =
                [
                    [ e.Syntax.Identifier; e.Syntax.Version ]
                    party e.Sender
                    party e.Recipient
                    [
                        local.ToString(dateFormat, CultureInfo.InvariantCulture)
                        local.ToString("HHmm", CultureInfo.InvariantCulture)
                    ]
                    [ e.ControlReference ]
                ]
                @ applicationReference
        }

    let private messageSegments (m: EdifactMessage) : Segment list =
        let id = m.Identifier

        let unh =
            {
                Tag = "UNH"
                Elements =
                    [
                        [ m.ReferenceNumber ]
                        [ id.Type; id.Version; id.Release; id.ControllingAgency ]
                        @ Option.toList id.AssociationCode
                    ]
            }

        let unt =
            {
                Tag = "UNT"
                Elements = [ [ string (m.Segments.Length + 2) ]; [ m.ReferenceNumber ] ]
            }

        unh :: m.Segments @ [ unt ]

    /// <summary>
    /// Writes the interchange with a UNA segment and no line breaks. UNT and UNZ counts are
    /// recomputed from the content; the preparation time is written as local time in <paramref name="zone"/>.
    /// </summary>
    let writeIn (zone: TimeZoneInfo) (envelope: InterchangeEnvelope) : string =
        let unz =
            {
                Tag = "UNZ"
                Elements = [ [ string envelope.Messages.Length ]; [ envelope.ControlReference ] ]
            }

        let segments =
            unb zone envelope :: List.collect messageSegments envelope.Messages @ [ unz ]

        serviceStringAdvice envelope.Delimiters
        + (segments |> List.map (writeSegment envelope.Delimiters) |> String.concat "")

    /// <summary>Writes the interchange with the preparation time in German local time (Europe/Berlin).</summary>
    let write (envelope: InterchangeEnvelope) : string =
        writeIn EnvelopeParser.marketTimeZone envelope
