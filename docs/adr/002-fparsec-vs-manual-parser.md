# ADR 002: FParsec parser combinators instead of a hand-written parser

- **Status:** Accepted, amended on Day 28 for one hot path
- **Date:** 2026-10-21 (Day 16), written up on Day 29

## Context

Reading EDIFACT has two levels:

1. **Syntax** (ISO 9735): an optional `UNA` sets the six delimiters; segments end with `'`, data elements are split by `+`, components by `:`, and `?` releases the next delimiter. Line breaks between segments are tolerated.
2. **Message structure**: the order and repetition of segments. For MSCONS: `UNH BGM DTM… NAD… UNS (NAD+DP (LOC [RFF] (LIN [PIA] (QTY DTM…)…)…)…) UNT`. For UTILMD: a header followed by transactions `IDE DTM… STS LOC… (SEQ CAV…)… NAD…`. Optional segments, nested repetitions and "a header ends where the first `IDE` starts" are the norm.

A rejected message must be answered with a position the sender can find (line/column for syntax errors, APERAK segment position for structure errors) and a readable reason (`Expected LOC segment, found LIN`).

## Decision

Use **FParsec** parser combinators for both levels:

- The tokenizer is an FParsec parser that reads `UNA` first and then builds the segment parser from those delimiters.
- The envelope grammar (`UNB (UNH segment* UNT)+ UNZ`) and the MSCONS and UTILMD grammars are FParsec combinators (`many`, `opt`, `many1`, `>>.`, `pipe`) that read like the structure diagrams in the message guides.
- Parsing is split into **grammar** (structure, with FParsec) and **typed conversion** (plain F# with `Result`), so the grammar stays small and the conversion can report every invalid element.

Amendment (Day 28, see [benchmarks](../benchmarks.md)): profiling showed that the innermost segment primitive, built from `many`/`sepBy1`/`manyStrings`, was the main CPU cost. `Tokenizer.segment` is now one hand-written function over FParsec's `CharStream`, keeping the combinator contract (no input consumed when no tag is ahead, the same error positions). The message grammars run over the segment tags only, with the tokenized segments as FParsec user state. Everything above the single-segment level is still combinators.

## Why combinators

- **The code has the shape of the specification.** The MSCONS grammar (`MsconsParser.fs`, module `Grammar`) is about 40 lines and can be checked line by line against the segment order. A hand-written recursive-descent parser for the same structure is a set of loops with lookahead flags, where an optional `RFF` or `PIA` is an `if` hidden in the middle.
- **Error messages for free.** FParsec tracks the position and the set of expected alternatives, so a failure becomes `Expecting: 'LOC'` at line/column without any error-reporting code of our own. In the grammar-over-tags setup, the line number *is* the APERAK segment position.
- **Delimiters as data.** Because parsers are values, the segment parser is built at runtime from the `UNA` delimiters. The FsCheck round-trip properties run with random delimiters and need no special path.
- **Composable and testable in pieces.** `seg "QTY"`, the value group and the metering-point group can each be run alone in a test.
- **Mature library.** FParsec has been stable for over a decade, is fast for a combinator library and has no transitive dependencies.

## Alternatives considered

- **Hand-written parser throughout** (state machine over characters, then recursive descent over segments). Fastest and with no dependency, but every optional segment and repetition becomes control flow, and error positions and "expected …" lists have to be built by hand. The Day 28 hot path shows the trade-off: one hand-written function was worth it for speed, a whole hand-written grammar would not be worth it for 40 lines of combinators.
- **Regular expressions / `string.Split`.** Splitting on `'`, `+` and `:` breaks on released delimiters (`?+`) and gives no positions. Regexes cannot express nested repetition such as metering point → series → value.
- **Parser generator** (ANTLR, FsYacc). Needs a separate grammar file and a build step, and the generated code is hard to debug. EDIFACT's delimiters change per message (`UNA`), which does not fit a fixed lexer well.
- **An existing EDIFACT library** (e.g. a generic EDIFACT-to-XML/JSON converter). These give untyped segment trees, not MSCONS/UTILMD models, and the energy-market message guides are not part of them, so the structure and typed conversion would still have to be written. Building the parser was also the point of this project.

## Consequences

- Positive: the grammars are short, close to the message guides and easy to extend (UTILMD reused the MSCONS grammar runner `SegmentGrammar.fs` and DTM handling).
- Positive: every syntax and structure error carries a position without extra code, which the APERAK generator and the explainer endpoint use directly.
- Negative: FParsec has a learning curve (operators such as `.>>`, `>>.`, `|>>`, `pipe3`, user state, and when a failed parser has consumed input so that `<|>` no longer backtracks without `attempt`), and its default error text needs trimming before it reaches a sender (the API drops the caret drawing).
- Negative: combinators allocate closures and `Reply` values per step. This cost was visible in the innermost loop and was fixed by one hand-written primitive; the grammar layer was not a hotspot after the Day 28 changes.
- Negative: FParsec normalizes `\r\n` to `\n`. A `\r` in a value therefore cannot survive a round trip, so the serializer refuses it (found by FsCheck).
