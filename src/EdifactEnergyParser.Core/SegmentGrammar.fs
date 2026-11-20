namespace EdifactEnergyParser.Core

open System

// Segment positions follow the APERAK convention: UNH is 1, so the first body segment is 2.
type internal Located = { Segment: Segment; Position: int }

/// FParsec building blocks that check the segment order of a message body.
module internal SegmentGrammar =
    open FParsec

    /// The user state is the already tokenized body, indexed by line.
    type P<'T> = Parser<'T, Segment[]>

    // FParsec reads characters, so the grammar runs over the body's tags, one per line. The line
    // number then indexes the tokenized segment and gives the segment position. Rendering whole
    // segments here would tokenize every segment a second time, which cost more than the
    // envelope parse itself (see docs/benchmarks.md).
    let seg (tag: string) : P<Located> =
        getPosition .>> (skipString tag <?> $"{tag} segment") .>> skipNewline .>>. getUserState
        |>> fun (pos, body) ->
            let index = int pos.Line - 1
            { Segment = body[index]; Position = index + 2 }

    let private expected (error: ParserError) : string =
        let labels =
            ErrorMessageList.ToSortedArray error.Messages
            |> Array.choose (function
                | :? ErrorMessage.Expected as e -> Some e.Label
                | _ -> None)

        if labels.Length = 0 then "Unexpected segment" else "Expected " + String.Join(" or ", labels)

    /// Runs a message grammar over the body; an error carries the segment position and a description.
    let parse (grammar: P<'T>) (body: Segment list) : Result<'T, int * string> =
        let segments = List.toArray body
        let tags = segments |> Array.map (fun s -> s.Tag + "\n") |> String.concat ""

        match runParserOnString (grammar .>> (eof <?> "end of message")) segments "" tags with
        | Success(raw, _, _) -> Result.Ok raw
        | Failure(_, error, _) ->
            let position = int error.Position.Line + 1

            // Position past the body means the message ended early, i.e. UNT was found.
            let found =
                segments |> Array.tryItem (position - 2) |> Option.map _.Tag |> Option.defaultValue "UNT"

            Result.Error(position, $"{expected error}, found {found}")
