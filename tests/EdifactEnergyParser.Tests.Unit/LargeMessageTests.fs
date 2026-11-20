module LargeMessageTests

open Xunit
open EdifactEnergyParser.Benchmarks
open EdifactEnergyParser.Core

// The benchmark input must itself be a valid message, or the numbers would measure an error path.
[<Fact>]
let ``One MiB MSCONS parses with every metering point and quarter-hour value`` () =
    let points = LargeMscons.pointsForSize (1024 * 1024)
    let input = LargeMscons.build points

    match MsconsParser.parse input with
    | Error errors -> failwithf "Expected Ok but got %A" errors
    | Ok [ message ] ->
        Assert.Equal(points, message.MeteringPoints.Length)

        let values =
            message.MeteringPoints
            |> List.collect _.TimeSeries
            |> List.sumBy _.Values.Length

        Assert.Equal(points * LargeMscons.ValuesPerPoint, values)
    | Ok messages -> failwithf "Expected one message but got %d" messages.Length

[<Fact>]
let ``Generated size is close to the requested size`` () =
    let target = 100 * 1024
    let actual = (LargeMscons.build (LargeMscons.pointsForSize target)).Length

    // Within one metering point (one day of quarter hours) of the target.
    Assert.InRange(actual, target - 10_000, target + 10_000)
