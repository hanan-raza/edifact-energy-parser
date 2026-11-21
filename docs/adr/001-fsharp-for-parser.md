# ADR 001: F# for the parser core, C# for the API

- **Status:** Accepted
- **Date:** 2026-10-21 (Day 16), written up on Day 29

## Context

The parser has to turn EDIFACT text into typed MSCONS and UTILMD models, check them against business rules and write them back. Three properties of the domain drive the choice of language:

1. **Messages are trees of alternatives.** A segment is one of a fixed set of tags; a QTY qualifier is a true value, a substitute value, a proposed value or unusable; a transaction reason is a supplier change, a move or something we do not model. Each of these is a closed set with a few cases, and code that handles them must not forget one.
2. **Errors are data, not exceptions.** A rejected message has to be answered with an APERAK that names *every* problem, with a segment position. Exceptions stop at the first problem and lose the position unless every caller catches and rewraps them.
3. **The model must be immutable.** The same parsed message is validated, serialized, explained and handed over to meter-data-service. If one step could change it, a round-trip test would no longer prove anything.

The rest of the system (HTTP, dependency injection, `HttpClient`, logging, the message state machine) is ordinary ASP.NET Core and is shared with the other portfolio projects, which are all C#.

## Decision

- The core library `EdifactEnergyParser.Core` (tokenizer, envelope, MSCONS/UTILMD parsers, validation rules, serializer, explainer) is written in **F#**.
- The workflow (state machine, APERAK generator, meter-data-service client) and the API are written in **C#**.
- Only adapter classes in the API (`MsconsAdapter`, `ExplainerAdapter`, `MeterReadingMapper`) touch the F# types. They translate `Result`, F# lists and discriminated unions into plain C# records (`MessageError(segment, text)`), so the workflow project has no F# dependency at all.

## Why F# for the core

| Need | F# | C# |
|------|----|-------|
| Closed sets of cases (`ValueStatus`, `TransactionReason`, `MsconsError`) | Discriminated unions; the compiler warns on a missing case in `match` | Records + `switch`; no exhaustiveness check for class hierarchies, so a new case compiles silently |
| Collect every error | `Result` and list functions (`List.collect` over rules, `traverse` over segments) are the default style | Possible, but against the grain of the language and its libraries, which throw |
| Immutability | Records and lists are immutable by default; structural equality comes free | `record` + `ImmutableList<T>`; equality of nested collections has to be written by hand |
| Parser combinators | FParsec is a mature F# library with good error messages (see [ADR 002](002-fparsec-vs-manual-parser.md)) | Sprache and Pidgin exist, but LINQ query syntax is a clumsier host for combinators than F# operators and computation expressions |
| Property-based tests | FsCheck is native F#; generators and shrinkers are short | FsCheck works from C# but is more verbose |

Structural equality matters more than it looks: the round-trip property is literally `parse (serialize msg) = Ok msg`. In C# that line would need a custom comparer for every nested list.

## Alternatives considered

- **Everything in C#.** One language is simpler to hire for and to build. It was rejected because the parser would lose exhaustive matching and structural equality, which are exactly the properties the tests rely on. The validation rules would also drift towards exceptions or mutable error lists.
- **Everything in F#, including the API.** Giraffe or Falco would work, but the state machine, `HttpClient` setup and `WebApplicationFactory` tests would then differ from the other three projects for no domain reason. ASP.NET Core minimal APIs and controllers are also what most .NET teams maintain.
- **F# with C#-friendly types in the public API** (e.g. `IReadOnlyList<T>`, no unions). That would remove the adapters, but it would push C# concerns into every F# function. Keeping the boundary in a few adapter classes is cheaper.

## Consequences

- Positive: the compiler catches a forgotten case (e.g. a new `ValueStatus`) in the parser, serializer, explainer and validation at once. FsCheck found two real bugs in the serializer (a `+05:30` offset and a `\r` in a party ID) with a few lines of generator code.
- Positive: the C# side stays idiomatic. Its tests (`Tests.Workflow`) do not reference F# except to read APERAK responses back.
- Negative: two languages in one solution. Contributors need to read F#, and the adapters are code that exists only because of the boundary.
- Negative: F# quirks that cost time while building: `Ok`/`Error` are shadowed by FParsec's `ReplyStatus` (we write `Result.Ok`), file order in the `.fsproj` matters, and .NET nullable annotations show up as F# nullness warnings.
- Neutral: performance is not a reason either way. The hot paths (tokenizer, grammar runner) were tuned within F# (see [benchmarks](../benchmarks.md)).
