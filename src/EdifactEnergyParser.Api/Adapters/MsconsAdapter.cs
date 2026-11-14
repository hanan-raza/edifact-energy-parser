using EdifactEnergyParser.Core;
using EdifactEnergyParser.Workflow;

namespace EdifactEnergyParser.Api.Adapters;

/// <summary>Outcome of parsing and validating an MSCONS interchange.</summary>
/// <param name="Interchange">What a response must refer back to; <c>null</c> if the envelope could not be read.</param>
/// <param name="Messages">The messages that parsed, whether or not they passed validation.</param>
/// <param name="Errors">Every envelope, parse and validation error found; empty means the interchange is accepted.</param>
public sealed record MsconsCheckResult(
    ReceivedInterchange? Interchange,
    IReadOnlyList<MsconsMessage> Messages,
    IReadOnlyList<MessageError> Errors)
{
    /// <summary>Whether the interchange parsed and passed every rule.</summary>
    public bool IsAccepted => Errors.Count == 0;
}

/// <summary>
/// The only place where C# calls the F# core: runs <see cref="EnvelopeParser"/>, <see cref="MsconsParser"/>
/// and <see cref="ValidationRules"/> and translates their F# unions into <see cref="MessageError"/>s.
/// </summary>
/// <remarks>
/// The F# modules stay plain functions; they are not registered in DI or wrapped in interfaces.
/// The envelope is parsed here rather than inside <c>MsconsParser.parse</c> so that the UNB data
/// (needed for an APERAK) and the segments (needed to name the tag of a structure error) are at hand.
/// </remarks>
public sealed class MsconsAdapter
{
    private const string ExpectedType = "MSCONS";

    /// <summary>Parses <paramref name="content"/> and runs the standard MSCONS rules on every message.</summary>
    public MsconsCheckResult Check(string content)
    {
        ArgumentNullException.ThrowIfNull(content);

        var envelopeResult = EnvelopeParser.parse(content);
        if (envelopeResult.IsError)
        {
            return new MsconsCheckResult(null, [], [.. envelopeResult.ErrorValue.Select(FromEnvelope)]);
        }

        var envelope = envelopeResult.ResultValue;
        var messages = new List<MsconsMessage>();
        var errors = new List<MessageError>();

        foreach (var raw in envelope.Messages)
        {
            var parsed = MsconsParser.parseMessageIn(EnvelopeParser.marketTimeZone, envelope.Delimiters, raw);
            if (parsed.IsError)
            {
                errors.Add(FromMscons(parsed.ErrorValue, raw));
                continue;
            }

            messages.Add(parsed.ResultValue);
            errors.AddRange(ValidationRules.validate(parsed.ResultValue)
                .Select(e => new MessageError(e.Segment, $"Message {raw.ReferenceNumber}: {e.Message}")));
        }

        return new MsconsCheckResult(ToInterchange(envelope), messages, errors);
    }

    private static ReceivedInterchange ToInterchange(InterchangeEnvelope envelope) =>
        new(
            envelope.ControlReference,
            envelope.Messages.Head.ReferenceNumber,
            ToPartner(envelope.Sender),
            ToPartner(envelope.Recipient));

    private static MarketPartner ToPartner(PartyIdentification party) =>
        // F# represents None as null, so the option collapses to a nullable string.
        new(party.Id, party.CodeQualifier?.Value);

    // An empty segment tag marks a problem that no single segment can be blamed for; the APERAK
    // generator reports it as an envelope error.
    private static MessageError FromEnvelope(EnvelopeError error) => error switch
    {
        EnvelopeError.SyntaxError e => new("", $"Syntax error at line {e.line}, column {e.column}: {Expectation(e.message)}"),
        EnvelopeError.InvalidElement e => new(e.segment, $"{e.reason} (line {e.line}, column {e.column})"),
        EnvelopeError.SegmentCountMismatch e => new("UNT",
            $"Message {e.messageReference}: UNT declares {e.declared} segments, but the message has {e.actual}"),
        EnvelopeError.MessageReferenceMismatch e => new("UNT",
            $"UNT repeats message reference '{e.trailer}', but UNH has '{e.header}'"),
        EnvelopeError.MessageCountMismatch e => new("UNZ",
            $"UNZ declares {e.declared} messages, but the interchange has {e.actual}"),
        EnvelopeError.ControlReferenceMismatch e => new("UNZ",
            $"UNZ repeats control reference '{e.trailer}', but UNB has '{e.header}'"),
        EnvelopeError.MixedMessageTypes e => new("UNH",
            $"The interchange mixes message types: {string.Join(", ", e.types)}"),
        _ => throw new ArgumentOutOfRangeException(nameof(error), error, "Unknown envelope error."),
    };

    private static MessageError FromMscons(MsconsError error, EdifactMessage raw) => error switch
    {
        MsconsError.EnvelopeInvalid e => FromEnvelope(e.Item),
        MsconsError.NotMscons e => new("UNH",
            $"Message {e.messageReference} is {e.messageType}, expected {ExpectedType}"),
        MsconsError.StructureError e => new(TagAt(raw, e.segmentPosition),
            $"Message {e.messageReference}, segment {e.segmentPosition}: {e.message}"),
        MsconsError.InvalidSegment e => new(e.segment,
            $"Message {e.messageReference}, segment {e.segmentPosition}: {e.reason}"),
        _ => throw new ArgumentOutOfRangeException(nameof(error), error, "Unknown MSCONS error."),
    };

    // Positions count UNH as 1, so the body starts at 2; one past the body is where UNT stands,
    // which is what a "message ended too early" error points at.
    private static string TagAt(EdifactMessage raw, int position)
    {
        var index = position - 2;
        return index >= 0 && index < raw.Segments.Length ? raw.Segments[index].Tag : "UNT";
    }

    // FParsec messages repeat the position and draw a caret under the input line; the API already
    // reports line and column, so only the "Expecting: …" part is kept.
    private static string Expectation(string fparsecMessage)
    {
        var lines = fparsecMessage
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(l => !l.StartsWith("Error in Ln", StringComparison.Ordinal))
            .ToList();
        var expecting = lines.FindIndex(l => l.StartsWith("Expecting", StringComparison.Ordinal));
        var relevant = expecting >= 0 ? lines.Skip(expecting) : lines;
        return string.Join(" ", relevant);
    }
}
