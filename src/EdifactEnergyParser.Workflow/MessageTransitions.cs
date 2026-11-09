using System.Collections.Frozen;

namespace EdifactEnergyParser.Workflow;

/// <summary>
/// The allowed state changes of a message, as one table instead of checks spread over methods.
/// </summary>
/// <remarks>
/// <code>
/// Received ──► Validated ──► Processed
///    │             │
///    └─────────────┴──────► Rejected
/// </code>
/// A message must pass validation before it is processed, and both a failed validation and a failed
/// processing step (e.g. unknown metering point) end in <see cref="MessageState.Rejected"/>.
/// <see cref="MessageState.Processed"/> and <see cref="MessageState.Rejected"/> are final: the sender
/// has been (or will be) answered, so a correction must arrive as a new message.
/// </remarks>
public static class MessageTransitions
{
    private static readonly FrozenDictionary<MessageState, FrozenSet<MessageState>> Allowed =
        new Dictionary<MessageState, FrozenSet<MessageState>>
        {
            [MessageState.Received] = FrozenSet.Create(MessageState.Validated, MessageState.Rejected),
            [MessageState.Validated] = FrozenSet.Create(MessageState.Processed, MessageState.Rejected),
            [MessageState.Processed] = FrozenSet<MessageState>.Empty,
            [MessageState.Rejected] = FrozenSet<MessageState>.Empty,
        }.ToFrozenDictionary();

    /// <summary>States a message in <paramref name="state"/> may move to next.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not a defined <see cref="MessageState"/>.</exception>
    public static IReadOnlySet<MessageState> From(MessageState state) =>
        Allowed.TryGetValue(state, out var targets)
            ? targets
            : throw new ArgumentOutOfRangeException(nameof(state), state, "Unknown message state.");

    /// <summary>Whether a message may move directly from <paramref name="from"/> to <paramref name="to"/>.</summary>
    public static bool IsAllowed(MessageState from, MessageState to) => From(from).Contains(to);

    /// <summary>Whether no further transition is possible.</summary>
    public static bool IsFinal(MessageState state) => From(state).Count == 0;
}
