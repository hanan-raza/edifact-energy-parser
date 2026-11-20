module EdifactEnergyParser.Benchmarks.Program

open BenchmarkDotNet.Running

// Run in Release: dotnet run -c Release --project benchmarks/EdifactEnergyParser.Benchmarks
[<EntryPoint>]
let main args =
    BenchmarkSwitcher.FromAssembly(typeof<ParserBenchmarks>.Assembly).Run(args) |> ignore
    0
