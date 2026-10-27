namespace EdifactEnergyParser.Core

open System

// Segment positions follow the APERAK convention: UNH is 1, so the first body segment is 2.
type internal Located = { Segment: Segment; Position: int }

/// FParsec building blocks that check the segment order of a message body.
module internal SegmentGrammar =
    open FParsec

    type P<'T> = Parser<'T, unit>

    // FParsec reads characters, so the body is rendered one segment per line; the line number
    // then gives the segment position.
    let seg (d: Delimiters) (tag: string) : P<Located> =
        pipe2 getPosition (followedByString tag <?> $"{tag} segment" >>. Tokenizer.segment d) (fun pos s ->
            { Segment = s; Position = int pos.Line + 1 })
        .>> skipNewline

    let private expected (error: ParserError) : string =
        let labels =
            ErrorMessageList.ToSortedArray error.Messages
            |> Array.choose (function
                | :? ErrorMessage.Expected as e -> Some e.Label
                | _ -> None)

        if labels.Length = 0 then "Unexpected segment" else "Expected " + String.Join(" or ", labels)

    /// Runs a message grammar over the body; an error carries the segment position and a description.
    let parse (grammar: P<'T>) (d: Delimiters) (body: Segment list) : Result<'T, int * string> =
        let text =
            body |> List.map (fun s -> EnvelopeWriter.writeSegment d s + "\n") |> String.concat ""

        match run (grammar .>> (eof <?> "end of message")) text with
        | Success(raw, _, _) -> Result.Ok raw
        | Failure(_, error, _) ->
            let position = int error.Position.Line + 1

            // Position past the body means the message ended early, i.e. UNT was found.
            let found =
                body |> List.tryItem (position - 2) |> Option.map _.Tag |> Option.defaultValue "UNT"

            Result.Error(position, $"{expected error}, found {found}")
