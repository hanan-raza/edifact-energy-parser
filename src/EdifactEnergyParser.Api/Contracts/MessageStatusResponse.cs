using EdifactEnergyParser.Workflow;

namespace EdifactEnergyParser.Api.Contracts;

/// <summary>Where a received message stands, returned by both POST and GET.</summary>
/// <remarks>The raw content is left out: the sender already has it, and it can be large.</remarks>
/// <param name="Id">ID assigned on receipt; use it with <c>GET /api/messages/{id}</c>.</param>
/// <param name="State">Current workflow state.</param>
/// <param name="Errors">Rejection reasons; empty unless <paramref name="State"/> is <see cref="MessageState.Rejected"/>.</param>
/// <param name="History">Every state change, oldest first.</param>
public sealed record MessageStatusResponse(
    Guid Id,
    MessageState State,
    IReadOnlyList<MessageError> Errors,
    IReadOnlyList<StateChange> History)
{
    /// <summary>Maps the workflow snapshot to the API shape.</summary>
    public static MessageStatusResponse From(TrackedMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return new(message.Id, message.State, message.Errors, message.History);
    }
}
