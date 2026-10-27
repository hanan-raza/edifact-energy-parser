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

/// Reads DTM values (C507) in the formats used by the German market.
module internal DateTimeFormat =

    let private parseExact (text: string) (pattern: string) : DateTime option =
        match DateTime.TryParseExact(text, pattern, CultureInfo.InvariantCulture, DateTimeStyles.None) with
        | true, local -> Some local
        | _ -> None

    // Format 303 carries its own UTC offset ("+01"); 102 and 203 are local time in the market zone.
    let parse (zone: TimeZoneInfo) (format: string) (text: string) : Result<DateTimeOffset, string> =
        let local pattern =
            match parseExact text pattern with
            | Some value -> LocalTime.resolve zone value
            | None -> Error $"Date/time '{text}' does not match its format"

        let withOffset () =
            let offset =
                if text.Length = 15 && (text[12] = '+' || text[12] = '-') then
                    match Int32.TryParse(text.Substring 13, NumberStyles.None, CultureInfo.InvariantCulture) with
                    | true, hours when hours <= 14 ->
                        let sign = if text[12] = '-' then -1.0 else 1.0
                        Some(TimeSpan.FromHours(sign * float hours))
                    | _ -> None
                else
                    None

            match parseExact (text.Substring(0, min 12 text.Length)) "yyyyMMddHHmm", offset with
            | Some value, Some offset -> Ok(DateTimeOffset(value, offset))
            | _ -> Error $"Date/time '{text}' does not match format 303 (CCYYMMDDHHMMZZZ)"

        match format with
        | "102" -> local "yyyyMMdd"
        | "203" -> local "yyyyMMddHHmm"
        | "303" -> withOffset ()
        | other -> Error $"Date/time format '{other}' is not supported"
