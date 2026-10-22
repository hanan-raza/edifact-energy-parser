namespace EdifactEnergyParser.Core

/// <summary>
/// The six service characters of an interchange (Trennzeichenvorgabe, UNA).
/// </summary>
type Delimiters =
    {
        /// <summary>Separates components of a composite data element (Gruppendatenelement-Trennzeichen), default ':'.</summary>
        ComponentSeparator: char
        /// <summary>Separates data elements within a segment (Datenelement-Trennzeichen), default '+'.</summary>
        DataElementSeparator: char
        /// <summary>Decimal mark used in numeric values (Dezimalzeichen), default '.'.</summary>
        DecimalMark: char
        /// <summary>Escapes the next character so it is read literally (Freigabezeichen), default '?'.</summary>
        ReleaseCharacter: char
        /// <summary>Repetition separator in syntax version 4; unused (space) in version 3, default ' '.</summary>
        Reserved: char
        /// <summary>Ends a segment (Segment-Endezeichen), default '\''.</summary>
        SegmentTerminator: char
    }

    /// <summary>The ISO 9735 default service characters, which apply when no UNA is present.</summary>
    static member Default =
        {
            ComponentSeparator = ':'
            DataElementSeparator = '+'
            DecimalMark = '.'
            ReleaseCharacter = '?'
            Reserved = ' '
            SegmentTerminator = '\''
        }

/// <summary>
/// A lexical unit of an EDIFACT stream. Values are already unescaped, so a released
/// separator such as "?+" arrives as a literal '+'.
/// </summary>
type Token =
    /// <summary>The three-character segment tag (Segmentkennung), e.g. "UNH".</summary>
    | SegmentTag of string
    /// <summary>A data element (Datenelement) — the value after a data element separator; empty if the element is omitted.</summary>
    | DataElement of string
    /// <summary>A further component (Komponente) of the preceding composite data element.</summary>
    | ComponentElement of string
    /// <summary>The end of a segment (Segment-Endezeichen).</summary>
    | SegmentTerminator

/// <summary>The result of tokenizing an interchange: the effective delimiters and the token stream.</summary>
type TokenizedInterchange =
    {
        /// <summary>Delimiters from the UNA segment, or <see cref="Delimiters.Default"/> if UNA is absent.</summary>
        Delimiters: Delimiters
        /// <summary>Tokens in input order. The UNA segment itself is not emitted.</summary>
        Tokens: Token list
    }

/// <summary>A segment (Segment) split into its data elements, each holding its components.</summary>
type Segment =
    {
        /// <summary>The three-character segment tag (Segmentkennung), e.g. "UNB".</summary>
        Tag: string
        /// <summary>
        /// Data elements in order; each is the list of its components (a simple data element has one).
        /// Values are unescaped; omitted ones are "" so positions stay stable.
        /// </summary>
        Elements: string list list
    }

    /// <summary>The value at a 0-based data element and component position, or None if absent or empty.</summary>
    member s.TryGet(element: int, ``component``: int) : string option =
        s.Elements
        |> List.tryItem element
        |> Option.bind (List.tryItem ``component``)
        |> Option.filter (fun v -> v <> "")

/// <summary>A tokenizer failure with a 1-based position in the input.</summary>
type TokenizeError =
    {
        /// <summary>Human-readable description of what was expected.</summary>
        Message: string
        /// <summary>1-based line of the failure.</summary>
        Line: int64
        /// <summary>1-based column of the failure.</summary>
        Column: int64
    }

/// <summary>Turns raw EDIFACT text into a flat <see cref="Token"/> stream.</summary>
module Tokenizer =
    open FParsec

    type internal P<'T> = Parser<'T, unit>

    let private validate (d: Delimiters) : P<Delimiters> =
        // Decimal mark and reserved char may legitimately coincide with data characters,
        // but the four structural characters must be unique or the stream is ambiguous.
        let structural =
            [ d.ComponentSeparator; d.DataElementSeparator; d.ReleaseCharacter; d.SegmentTerminator ]

        if List.distinct structural |> List.length = structural.Length then
            preturn d
        else
            fail "UNA component separator, data element separator, release character and segment terminator must be distinct"

    let private serviceStringAdvice: P<Delimiters> =
        pstring "UNA" >>. (anyString 6 <?> "six service characters after UNA")
        >>= fun s ->
            validate
                {
                    ComponentSeparator = s[0]
                    DataElementSeparator = s[1]
                    DecimalMark = s[2]
                    ReleaseCharacter = s[3]
                    Reserved = s[4]
                    SegmentTerminator = s[5]
                }

    let private value (d: Delimiters) : P<string> =
        let isPlain c =
            c <> d.ComponentSeparator
            && c <> d.DataElementSeparator
            && c <> d.SegmentTerminator
            && c <> d.ReleaseCharacter

        let plain = many1Satisfy isPlain
        let released = pchar d.ReleaseCharacter >>. (anyChar <?> "character after release character") |>> string
        manyStrings (plain <|> released)

    let private segmentTag: P<string> =
        manyMinMaxSatisfyL 3 3 (fun c -> isAsciiUpper c || isDigit c) "three-character segment tag"

    let private dataElement (d: Delimiters) : P<string list> =
        pchar d.DataElementSeparator >>. sepBy1 (value d) (pchar d.ComponentSeparator)

    /// One segment including its terminator, read with the given delimiters.
    let internal segment (d: Delimiters) : P<Segment> =
        pipe2 segmentTag (many (dataElement d) .>> pchar d.SegmentTerminator) (fun tag elements ->
            { Tag = tag; Elements = elements })

    let private toTokens (s: Segment) : Token list =
        let element =
            function
            | first :: components -> DataElement first :: List.map ComponentElement components
            | [] -> []

        SegmentTag s.Tag :: List.collect element s.Elements @ [ SegmentTerminator ]

    // Line breaks between segments are not part of the syntax but almost every real-world
    // file has them, so they are tolerated outside segments only.
    let internal lineBreaks: P<unit> = skipMany (anyOf "\r\n")

    /// The optional UNA segment, falling back to the ISO 9735 defaults.
    let internal delimiters: P<Delimiters> =
        opt serviceStringAdvice |>> Option.defaultValue Delimiters.Default .>> lineBreaks

    let private interchange: P<TokenizedInterchange> =
        delimiters
        >>= fun d ->
            many (segment d .>> lineBreaks) .>> eof
            |>> fun segments -> { Delimiters = d; Tokens = List.collect toTokens segments }

    /// <summary>
    /// Tokenizes an EDIFACT interchange. A leading UNA segment sets the delimiters;
    /// otherwise the ISO 9735 defaults are used.
    /// </summary>
    let tokenize (input: string) : Result<TokenizedInterchange, TokenizeError> =
        // FParsec's ReplyStatus shadows Ok/Error, hence the qualified Result cases.
        match run interchange input with
        | Success(result, _, _) -> Result.Ok result
        | Failure(message, error, _) ->
            Result.Error
                {
                    Message = message
                    Line = error.Position.Line
                    Column = error.Position.Column
                }
