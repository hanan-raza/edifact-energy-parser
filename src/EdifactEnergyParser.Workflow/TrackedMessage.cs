namespace EdifactEnergyParser.Workflow;

/// <summary>
/// A reason a message was rejected (Ablehnungsgrund), in the shape of the F# <c>ValidationError</c>
/// so validation results can be passed on without translation.
/// </summary>
/// <param name="Segment">Tag of the segment the problem is about, e.g. "NAD" or "QTY".</param>
/// <param name="Message">What is wrong, in words the sender can act on.</param>
public sealed record MessageError(string Segment, string Message);

/// <summary>One step in a message's history (Statusverlauf).</summary>
/// <param name="From">State before the step; <c>null</c> for the initial receipt.</param>
/// <param name="To">State after the step.</param>
/// <param name="At">When the step happened, in UTC.</param>
public sealed record StateChange(MessageState? From, MessageState To, DateTimeOffset At);

/// <summary>An incoming market message and where it stands in the workflow (eingehende Nachricht).</summary>
/// <remarks>
/// Immutable: every transition stores a new instance. <see cref="Version"/> grows by one per
/// transition, so a store can refuse an update that was based on an outdated snapshot.
/// </remarks>
/// <param name="Id">ID assigned on receipt (time-ordered GUID v7).</param>
/// <param name="Content">The raw EDIFACT text exactly as received, kept for audit and re-processing.</param>
/// <param name="State">Current state.</param>
/// <param name="Version">Number of transitions so far; 0 right after receipt.</param>
/// <param name="Errors">Reasons for the rejection; empty unless <see cref="State"/> is <see cref="MessageState.Rejected"/>.</param>
/// <param name="History">Every state change, oldest first, starting with the receipt.</param>
public sealed record TrackedMessage(
    Guid Id,
    string Content,
    MessageState State,
    int Version,
    IReadOnlyList<MessageError> Errors,
    IReadOnlyList<StateChange> History);
