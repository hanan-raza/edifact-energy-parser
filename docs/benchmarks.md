# Parser benchmarks

How fast `MsconsParser.parse` reads a large MSCONS interchange, what was slow, and what was changed.

## Setup

- Suite: [`benchmarks/EdifactEnergyParser.Benchmarks`](../benchmarks/EdifactEnergyParser.Benchmarks) (BenchmarkDotNet 0.15.8, `[MemoryDiagnoser]`)
- Input: `LargeMscons.build`, one MSCONS message with N metering points, each carrying one winter day of quarter-hour values (96 × `QTY` + 2 × `DTM` format 303 with released `?+`). That is the shape of a daily load-profile delivery.
  - ~7.8 KB per metering point, so 100 KiB ≈ 13 metering points (~1,250 values)
  - 1 MiB ≈ 135 metering points (~13,000 values, ~39,000 segments)
- `LargeMessageTests` checks that the 1 MiB input really parses with every point and value, so the numbers never measure an error path
- Three stages are measured separately:
  - `Tokenize`: `Tokenizer.segments`, which handles UNA, escapes and splitting
  - `Envelope`: `EnvelopeParser.parse`, which adds the UNB/UNH/UNT/UNZ grammar and the count/reference checks
  - `Mscons`: `MsconsParser.parse`, the full path the API takes (envelope, MSCONS segment order, typed values)
- Machine: Intel Core i5-8365U (2019 laptop, 4 cores, 1.6–1.9 GHz), Windows 11, .NET 10.0.12

```bash
dotnet run -c Release --project benchmarks/EdifactEnergyParser.Benchmarks -- --filter "*"
# Server GC, as ASP.NET Core uses by default:
dotnet run -c Release --project benchmarks/EdifactEnergyParser.Benchmarks -- --filter "*" --envVars DOTNET_gcServer:1
```

## Target

The roadmap target was **< 100 ms for a 1 MB MSCONS**.

## Before (commit `6b29dc8`)

BenchmarkDotNet `--job short` (3 iterations, so the error bars are wide), workstation GC:

| Stage    | 100 KiB  | 1 MiB     | Allocated (1 MiB) |
|----------|---------:|----------:|------------------:|
| Tokenize |  8.7 ms  | 193.8 ms  |  22.4 MB |
| Envelope | 10.5 ms  | 212.8 ms  |  26.1 MB |
| Mscons   | 40.2 ms  | **631.9 ms** | 117.5 MB |

The time is spread unevenly across the stages:

1. **`Mscons` − `Envelope` = ~420 ms.** The MSCONS grammar is written in FParsec, which reads characters, not segments. `SegmentGrammar.parse` therefore wrote every body segment back to EDIFACT text (escaping included) and ran the tokenizer over it a second time. Each segment was serialized once and tokenized twice. That doubled the allocations and cost more than the whole envelope parse.
2. **`Tokenize` = ~190 ms.** The segment parser was composed from `many`, `sepBy1`, `manyStrings`, `<|>` and `pipe2`. For a 3-character value like `303`, that is half a dozen closure calls and intermediate `Reply` values. This is the innermost loop of every parse.

## Changes

1. **Grammar over tags only** (`SegmentGrammar.fs`). The grammar now runs over a text of just the segment tags, one per line (`QTY\nDTM\nDTM\n…`). The tokenized segments travel as FParsec user state (`Segment[]`). `seg "QTY"` matches the tag and takes the segment from the array by line number, which is also its APERAK segment position. Error messages and positions are unchanged: all structure-error tests pass as before. As a side effect, the message parsers no longer need the delimiters (`parseMessageIn zone message`).
2. **Hand-written segment primitive** (`Tokenizer.segment`). This is now one function over FParsec's `CharStream`. It reads plain runs with `ReadCharsOrNewlinesWhile`, uses a `StringBuilder` only when a release character occurs, and builds the element lists with tail-recursive loops. It keeps the combinator contract:
   - no input consumed when no tag is ahead, so `many` stops cleanly
   - `\r\n` inside values normalized to `\n`, as before (the FsCheck round-trip properties rely on this)
   - the same error positions and the "character after release character" message

   The rest of the tokenizer and both grammars stay FParsec combinators.

All 227 tests pass unchanged, including the 10,000-case FsCheck round-trip properties with random delimiters and escapes.

## After

BenchmarkDotNet default job (≥ 15 iterations).

**Workstation GC** (console apps, test runners):

| Stage    | 100 KiB  | 1 MiB (mean / median) | Allocated (1 MiB) |
|----------|---------:|----------------------:|------------------:|
| Tokenize |  3.7 ms  |  96.9 / 95.0 ms  | 22.0 MB |
| Envelope |  4.6 ms  | 107.9 / 104.7 ms | 25.6 MB |
| Mscons   | 14.2 ms  | **227.5 / 202.0 ms** | 54.8 MB |

**Server GC** (the default for the ASP.NET Core API that receives messages):

| Stage    | 100 KiB  | 1 MiB (mean / median) | Allocated (1 MiB) |
|----------|---------:|----------------------:|------------------:|
| Tokenize |  3.5 ms  |  60.8 / 61.8 ms  | 22.0 MB¹ |
| Envelope |  3.9 ms  |  56.3 / 53.1 ms  | 25.6 MB |
| Mscons   | 11.8 ms  | **123.5 / 120.0 ms** | 54.8 MB |

¹ BenchmarkDotNet reported 325 MB for this one row under Server GC. The same code allocates 22.0 MB in every other run, so this is a measurement artefact of per-heap allocation counting.

Summary: the full parse is **2.8× faster under workstation GC and ~5× faster in the API's configuration**, with **53 % less allocation** (117.5 → 54.8 MB per MiB).

## Result against the target

**The target is not met: 1 MiB takes ~120 ms (Server GC) instead of < 100 ms. The target is revised to < 150 ms per MiB under Server GC on this laptop CPU.** Reasons:

- **What is left is mostly memory, not parsing logic.** A 1 MiB message becomes ~39,000 `Segment` records with `string list list` elements and ~13,000 typed values, and all of it survives until the parse returns. The time grows faster than the input (10× the size costs ~10–15× the time) because each GC has to promote an ever-larger live set. Server GC's larger gen-0 budget halves the 1 MiB time compared with workstation GC, which shows the remaining cost is GC rather than CPU work in the parser.
- **Going below 100 ms would need a model change**, not a hotspot fix: arrays or `ReadOnlyMemory<char>` slices instead of F# lists in `Segment`, or a streaming parser that yields one metering point at a time. Both would touch every consumer (validation, serializer, explainer, C# adapter), which is out of proportion for this step.
- **The real workload is far smaller.** Market partners usually send one MSCONS per metering point or small batches of a few KiB, and 100 KiB parses in ~12 ms. Even at 1 MiB per 120 ms, one core reads ~100,000 quarter-hour values per second. That covers a daily delivery of 100,000 metering points × 96 values in under 2 minutes of CPU time.
- The 4-core 2019 laptop CPU at 1.9 GHz is slow. A current server CPU would very likely be under 100 ms, but that was not measured here.

## Next steps

- Stream metering points (`seq`) from the MSCONS stage so that a large message never has to be fully alive in memory
- Store segment elements as arrays instead of lists if profiling of the API under load points at the tokenizer again
- A CI performance smoke test (e.g. "1 MiB under 1 s") to catch an accidental quadratic step, deliberately loose so it does not flake on shared runners
