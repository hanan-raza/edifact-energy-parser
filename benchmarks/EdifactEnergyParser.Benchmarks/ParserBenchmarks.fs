namespace EdifactEnergyParser.Benchmarks

open BenchmarkDotNet.Attributes
open EdifactEnergyParser.Core

/// <summary>
/// Measures the three stages of reading an MSCONS interchange, so a slowdown can be pinned to
/// the tokenizer, the envelope grammar or the MSCONS grammar and typed conversion.
/// </summary>
[<MemoryDiagnoser>]
type ParserBenchmarks() =
    let mutable input = ""

    /// <summary>Target input size in KiB; the real size is within one metering point (~9 KiB) of it.</summary>
    [<Params(100, 1024)>]
    member val SizeKiB = 0 with get, set

    [<GlobalSetup>]
    member this.Setup() =
        input <- LargeMscons.build (LargeMscons.pointsForSize (this.SizeKiB * 1024))

    /// <summary>Segments only: UNA handling, escapes and element splitting.</summary>
    [<Benchmark>]
    member _.Tokenize() = Tokenizer.segments input

    /// <summary>Tokenizing plus the UNB/UNH/UNT/UNZ envelope and its consistency checks.</summary>
    [<Benchmark>]
    member _.Envelope() = EnvelopeParser.parse input

    /// <summary>The full path the API takes: envelope, MSCONS segment order and typed values.</summary>
    [<Benchmark(Baseline = true)>]
    member _.Mscons() = MsconsParser.parse input
