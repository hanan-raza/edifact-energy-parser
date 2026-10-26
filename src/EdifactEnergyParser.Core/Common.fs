namespace EdifactEnergyParser.Core

open System
open System.Globalization

/// Result helpers shared by the message parsers.
module internal Results =

    type ResultBuilder() =
        member _.Bind(r, f) = Result.bind f r
        member _.Return v = Ok v
        member _.ReturnFrom r = r

    let result = ResultBuilder()

    /// Applies f to every item and stops at the first error.
    let traverse (f: 'a -> Result<'b, 'e>) (xs: 'a list) : Result<'b list, 'e> =
        List.foldBack
            (fun x acc ->
                result {
                    let! y = f x
                    let! ys = acc
                    return y :: ys
                })
            xs
            (Ok [])

/// Turns EDIFACT times without a UTC offset into instants.
module internal LocalTime =

    // An ambiguous time in the repeated October hour is read as the first pass (daylight time);
    // a time in the March gap cannot exist.
    let resolve (zone: TimeZoneInfo) (local: DateTime) : Result<DateTimeOffset, string> =
        if zone.IsInvalidTime local then
            let text = local.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)
            Error $"{text} does not exist in {zone.Id} (DST gap)"
        elif zone.IsAmbiguousTime local then
            Ok(DateTimeOffset(local, Array.max (zone.GetAmbiguousTimeOffsets local)))
        else
            Ok(DateTimeOffset(local, zone.GetUtcOffset local))
