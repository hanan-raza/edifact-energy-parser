module UtilmdParserTests

open System
open System.IO
open Xunit
open EdifactEnergyParser.Core

let private parseOk input =
    match UtilmdParser.parse input with
    | Ok messages -> messages
    | Error errors -> failwithf "Expected Ok but got %A" errors

let private parseErrors input =
    match UtilmdParser.parse input with
    | Ok messages -> failwithf "Expected Error but got %A" messages
    | Error errors -> errors

let private sample () =
    File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "samples", "utilmd_example.edi"))

let private header =
    [
        "BGM+E01+UTI1+9'"
        "DTM+137:202610270900?+01:303'"
        "NAD+MS+9900000000027::293'"
        "NAD+MR+9900000000010::293'"
    ]

/// Wraps body segments in an envelope with a correct UNT count.
let private interchange (body: string list) =
    "UNB+UNOC:3+9900000000027:500+9900000000010:500+261027:0900+U1'"
    + "UNH+1+UTILMD:D:11A:UN:5.2e'"
    + String.concat "" body
    + $"UNT+{body.Length + 2}+1'UNZ+1+U1'"

let private transaction (dates: string list) (locations: string list) =
    [ "IDE+24+TX1'" ] @ dates @ [ "STS+7++E03'" ] @ locations

let private singleTransaction input =
    let message = parseOk input |> Assert.Single
    Assert.Single message.Transactions

let private time (y, mo, d, h, mi) (offset: float) =
    DateTimeOffset(y, mo, d, h, mi, 0, TimeSpan.FromHours offset)

[<Fact>]
let ``sample UTILMD parses into two supplier switch transactions`` () =
    let message = parseOk (sample ()) |> Assert.Single

    Assert.Equal("1", message.ReferenceNumber)
    Assert.Equal("E01", message.DocumentType)
    Assert.Equal("UTI0000001", message.DocumentNumber)
    Assert.Equal(time (2026, 10, 27, 9, 0) 1.0, message.DocumentDate)
    Assert.Equal({ Id = "9900000000027"; CodeQualifier = Some "293" }, message.Sender)
    Assert.Equal({ Id = "9900000000010"; CodeQualifier = Some "293" }, message.Recipient)

    Assert.Equal<string list>([ "TX0000001"; "TX0000002" ], message.Transactions |> List.map _.TransactionId)
    Assert.Equal<string list>([ "51238696781"; "41235678904" ], message.Transactions |> List.map _.MarketLocationId)

    Assert.Equal<TransactionReason list>(
        [ SupplierChange; MoveInOrOut ],
        message.Transactions |> List.map _.Reason
    )

[<Fact>]
let ``sample switch keeps metering location, master data and end customer`` () =
    let switch = (parseOk (sample ()) |> List.head).Transactions.Head

    Assert.Equal(time (2026, 12, 1, 0, 0) 1.0, switch.StartOfSupply)
    Assert.Equal(None, switch.EndOfSupply)
    Assert.Equal<string list>([ "DE0001234567800000000000000000001" ], switch.MeteringLocationIds)

    let record = Assert.Single switch.MasterData
    Assert.Equal("Z01", record.GroupCode)

    Assert.Equal<Characteristic list>(
        [ { Code = "Z15"; Value = Some "H0" }; { Code = "Z16"; Value = Some "3500" } ],
        record.Characteristics
    )

    Assert.Equal<TransactionParty list>(
        [ { Role = "Z09"; Id = None; Name = Some "Mustermann, Max" } ],
        switch.Parties
    )

[<Fact>]
let ``sample move-in reads local dates 102 in German time`` () =
    let moveIn = (parseOk (sample ()) |> List.head).Transactions[1]

    // Same instant as the 303 date of the first transaction, written as a local date.
    Assert.Equal(time (2026, 12, 1, 0, 0) 1.0, moveIn.StartOfSupply)
    Assert.Equal(Some(time (2027, 12, 1, 0, 0) 1.0), moveIn.EndOfSupply)
    Assert.Empty moveIn.MeteringLocationIds
    Assert.Equal(Some "Beispiel Baeckerei GmbH", (Assert.Single moveIn.Parties).Name)

[<Fact>]
let ``start of supply in summer gets the summer offset`` () =
    let t =
        singleTransaction (interchange (header @ transaction [ "DTM+92:20260701:102'" ] [ "LOC+Z16+51238696781'" ]))

    Assert.Equal(time (2026, 7, 1, 0, 0) 2.0, t.StartOfSupply)

[<Fact>]
let ``transaction without master data or parties is accepted`` () =
    let t =
        singleTransaction (
            interchange (header @ transaction [ "DTM+92:202612010000?+01:303'" ] [ "LOC+Z16+51238696781'" ])
        )

    Assert.Empty t.MasterData
    Assert.Empty t.Parties

[<Fact>]
let ``unknown reason code is kept`` () =
    let body =
        header
        @ [ "IDE+24+TX1'"; "DTM+92:202612010000?+01:303'"; "STS+7++Z99'"; "LOC+Z16+51238696781'" ]

    Assert.Equal(OtherReason "Z99", (singleTransaction (interchange body)).Reason)

[<Fact>]
let ``missing STS is a structure error at the segment position`` () =
    let body = header @ [ "IDE+24+TX1'"; "DTM+92:202612010000?+01:303'"; "LOC+Z16+51238696781'" ]

    // UNH is 1, so the four header segments are 2–5, IDE is 6, DTM 7 and LOC 8.
    match parseErrors (interchange body) with
    | [ UtilmdError.StructureError("1", 8, text) ] ->
        Assert.Contains("STS segment", text)
        Assert.EndsWith("found LOC", text)
    | other -> failwithf "Unexpected %A" other

[<Fact>]
let ``message without transaction is a structure error at UNT`` () =
    match parseErrors (interchange header) with
    | [ UtilmdError.StructureError("1", 6, text) ] -> Assert.EndsWith("found UNT", text)
    | other -> failwithf "Unexpected %A" other

[<Fact>]
let ``missing start of supply is an invalid IDE segment`` () =
    let errors = parseErrors (interchange (header @ transaction [] [ "LOC+Z16+51238696781'" ]))

    Assert.Equal<UtilmdError list>(
        [ UtilmdError.InvalidSegment("1", 6, "IDE", "Start of supply (DTM+92) is missing") ],
        errors
    )

[<Fact>]
let ``end of supply before start is an invalid IDE segment`` () =
    let dates = [ "DTM+92:20261201:102'"; "DTM+93:20261101:102'" ]
    let errors = parseErrors (interchange (header @ transaction dates [ "LOC+Z16+51238696781'" ]))

    Assert.Equal<UtilmdError list>(
        [ UtilmdError.InvalidSegment("1", 6, "IDE", "End of supply is not after start of supply") ],
        errors
    )

[<Fact>]
let ``transaction with only a metering location lacks its market location`` () =
    let errors =
        parseErrors (
            interchange (
                header
                @ transaction [ "DTM+92:20261201:102'" ] [ "LOC+Z17+DE0001234567800000000000000000001'" ]
            )
        )

    Assert.Equal<UtilmdError list>(
        [ UtilmdError.InvalidSegment("1", 6, "IDE", "Market location (LOC+Z16) is missing") ],
        errors
    )

[<Fact>]
let ``unsupported location qualifier is an invalid LOC segment`` () =
    let errors =
        parseErrors (interchange (header @ transaction [ "DTM+92:20261201:102'" ] [ "LOC+172+DE1'" ]))

    Assert.Equal<UtilmdError list>(
        [ UtilmdError.InvalidSegment("1", 9, "LOC", "Location qualifier '172' is not supported") ],
        errors
    )

[<Fact>]
let ``start of supply in the March DST gap is an invalid DTM segment`` () =
    let errors =
        parseErrors (
            interchange (header @ transaction [ "DTM+92:202603290230:203'" ] [ "LOC+Z16+51238696781'" ])
        )

    match errors with
    | [ UtilmdError.InvalidSegment("1", 7, "DTM", reason) ] -> Assert.Contains("DST gap", reason)
    | other -> failwithf "Unexpected %A" other

[<Fact>]
let ``MSCONS message is rejected by the UTILMD parser`` () =
    let input =
        "UNB+UNOC:3+9900000000003:500+9900000000010:500+260116:0630+R1'"
        + "UNH+7+MSCONS:D:04B:UN:2.4c'UNT+2+7'UNZ+1+R1'"

    Assert.Equal<UtilmdError list>([ UtilmdError.NotUtilmd("7", "MSCONS") ], parseErrors input)

[<Fact>]
let ``MSCONS and UTILMD parsers each reject the other's sample`` () =
    let mscons =
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "samples", "mscons_example.edi"))

    Assert.True(Result.isError (UtilmdParser.parse mscons))
    Assert.True(Result.isError (MsconsParser.parse (sample ())))
