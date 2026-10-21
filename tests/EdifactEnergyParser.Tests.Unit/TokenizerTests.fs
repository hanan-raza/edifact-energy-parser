module TokenizerTests

open Xunit
open EdifactEnergyParser.Core

let private tokenizeOk input =
    match Tokenizer.tokenize input with
    | Ok result -> result
    | Error e -> failwithf "Expected Ok but got error at %d:%d: %s" e.Line e.Column e.Message

let private tokenizeError input =
    match Tokenizer.tokenize input with
    | Ok result -> failwithf "Expected Error but got %A" result.Tokens
    | Error e -> e

let private minimalMessage = "UNH+1+MSCONS:D:04B:UN:2.4c'BGM+7+MSI5422+9'UNT+3+1'"

let private minimalTokens =
    [
        SegmentTag "UNH"
        DataElement "1"
        DataElement "MSCONS"
        ComponentElement "D"
        ComponentElement "04B"
        ComponentElement "UN"
        ComponentElement "2.4c"
        SegmentTerminator
        SegmentTag "BGM"
        DataElement "7"
        DataElement "MSI5422"
        DataElement "9"
        SegmentTerminator
        SegmentTag "UNT"
        DataElement "3"
        DataElement "1"
        SegmentTerminator
    ]

[<Fact>]
let ``three segments with UNA produce the expected token sequence`` () =
    let result = tokenizeOk ("UNA:+.? '" + minimalMessage)

    Assert.Equal<Token list>(minimalTokens, result.Tokens)
    Assert.Equal(Delimiters.Default, result.Delimiters)

[<Fact>]
let ``three segments without UNA fall back to default delimiters`` () =
    let result = tokenizeOk minimalMessage

    Assert.Equal<Token list>(minimalTokens, result.Tokens)
    Assert.Equal(Delimiters.Default, result.Delimiters)

[<Fact>]
let ``custom UNA delimiters are applied to the rest of the interchange`` () =
    let custom = "UNA|*,# ~UNH*1*MSCONS|D|04B|UN|2.4c~BGM*7*MSI5422*9~UNT*3*1~"

    let result = tokenizeOk custom

    Assert.Equal<Token list>(minimalTokens, result.Tokens)
    Assert.Equal(
        {
            ComponentSeparator = '|'
            DataElementSeparator = '*'
            DecimalMark = ','
            ReleaseCharacter = '#'
            Reserved = ' '
            SegmentTerminator = '~'
        },
        result.Delimiters
    )

[<Fact>]
let ``release character makes separators and itself literal`` () =
    let result = tokenizeOk "FTX+AAI+Price ?+ VAT?: 19 ?' ok??'"

    Assert.Equal<Token list>(
        [ SegmentTag "FTX"; DataElement "AAI"; DataElement "Price + VAT: 19 ' ok?"; SegmentTerminator ],
        result.Tokens
    )

[<Fact>]
let ``omitted data elements and components are kept as empty values`` () =
    let result = tokenizeOk "NAD+MS++::293+'"

    Assert.Equal<Token list>(
        [
            SegmentTag "NAD"
            DataElement "MS"
            DataElement ""
            DataElement ""
            ComponentElement ""
            ComponentElement "293"
            DataElement ""
            SegmentTerminator
        ],
        result.Tokens
    )

[<Fact>]
let ``line breaks between segments are ignored`` () =
    let result = tokenizeOk "UNA:+.? '\r\nUNH+1+MSCONS:D:04B:UN:2.4c'\r\nBGM+7+MSI5422+9'\nUNT+3+1'\r\n"

    Assert.Equal<Token list>(minimalTokens, result.Tokens)

[<Fact>]
let ``empty input yields no tokens`` () =
    let result = tokenizeOk ""

    Assert.Empty(result.Tokens)

[<Fact>]
let ``missing segment terminator is an error`` () =
    let error = tokenizeError "UNH+1+MSCONS'BGM+7"

    Assert.Equal(1L, error.Line)
    Assert.Equal(19L, error.Column)

[<Fact>]
let ``release character at end of input is an error`` () =
    let error = tokenizeError "FTX+AAI+broken?"

    Assert.Contains("character after release character", error.Message)

[<Fact>]
let ``invalid segment tag reports its line and column`` () =
    let error = tokenizeError "UNH+1'\nbgm+7'"

    Assert.Equal(2L, error.Line)
    Assert.Equal(1L, error.Column)

[<Fact>]
let ``UNA with duplicate structural characters is rejected`` () =
    let error = tokenizeError "UNA++.? 'UNH+1'"

    Assert.Contains("must be distinct", error.Message)

[<Fact>]
let ``truncated UNA is rejected`` () =
    let error = tokenizeError "UNA:+."

    Assert.Contains("six service characters after UNA", error.Message)
