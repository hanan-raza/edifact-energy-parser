namespace EdifactEnergyParser.Api.Contracts;

/// <summary>One segment of an explained message, returned by <c>POST /api/messages/explain</c>.</summary>
/// <param name="Segment">The segment tag, e.g. <c>UNB</c>.</param>
/// <param name="Description">What the segment is for, including its qualifier's meaning where it has one.</param>
/// <param name="Fields">Every non-empty value of the segment in input order.</param>
public sealed record SegmentExplanationResponse(
    string Segment,
    string Description,
    IReadOnlyList<FieldExplanationResponse> Fields);

/// <summary>One value of an explained segment.</summary>
/// <param name="Name">Name of the data element or component, e.g. <c>Sender identification</c>.</param>
/// <param name="Value">The value as sent, without release characters.</param>
/// <param name="Meaning">What the value means, e.g. the text behind a code or a date with its UTC offset.</param>
public sealed record FieldExplanationResponse(string Name, string Value, string Meaning);
