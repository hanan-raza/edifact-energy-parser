using Microsoft.Extensions.Logging;

namespace EdifactEnergyParser.Workflow;

/// <summary>
/// Moves incoming messages through <see cref="MessageState"/> and persists every step in an
/// <see cref="IMessageStore"/>. Which steps are allowed is defined in <see cref="MessageTransitions"/>.
/// </summary>
/// <remarks>
/// The workflow does not parse or validate anything itself: the caller does the work and reports
/// the outcome. That keeps this class free of F# and EDIFACT details and testable on its own.
/// </remarks>
public sealed partial class MessageWorkflow(IMessageStore store, TimeProvider timeProvider, ILogger<MessageWorkflow> logger)
{
    /// <summary>Stores a newly arrived message in <see cref="MessageState.Received"/>.</summary>
    /// <exception cref="ArgumentException">The content is empty.</exception>
    public TrackedMessage Receive(string content)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(content);

        var now = timeProvider.GetUtcNow();
        var message = new TrackedMessage(
            Guid.CreateVersion7(now),
            content,
            MessageState.Received,
            Version: 0,
            Errors: [],
            History: [new StateChange(null, MessageState.Received, now)]);

        if (!store.TryAdd(message))
        {
            // Only possible if two receipts in the same millisecond also drew the same 74 random bits.
            throw new InvalidOperationException($"Message ID {message.Id} is already in use.");
        }

        LogReceived(message.Id, content.Length);
        return message;
    }

    /// <summary>Records that the message parsed and passed validation.</summary>
    /// <exception cref="KeyNotFoundException">No message has this ID.</exception>
    /// <exception cref="InvalidOperationException">The message is not in <see cref="MessageState.Received"/>.</exception>
    public TrackedMessage MarkValidated(Guid id) => Transition(id, MessageState.Validated, []);

    /// <summary>Records that the business process has taken over the message.</summary>
    /// <exception cref="KeyNotFoundException">No message has this ID.</exception>
    /// <exception cref="InvalidOperationException">The message is not in <see cref="MessageState.Validated"/>.</exception>
    public TrackedMessage MarkProcessed(Guid id) => Transition(id, MessageState.Processed, []);

    /// <summary>Rejects the message with the reasons the sender will receive.</summary>
    /// <exception cref="ArgumentException">No reason was given.</exception>
    /// <exception cref="KeyNotFoundException">No message has this ID.</exception>
    /// <exception cref="InvalidOperationException">The message is already <see cref="MessageState.Processed"/> or <see cref="MessageState.Rejected"/>.</exception>
    public TrackedMessage Reject(Guid id, IReadOnlyList<MessageError> errors)
    {
        ArgumentNullException.ThrowIfNull(errors);
        if (errors.Count == 0)
        {
            // A negative APERAK without a reason gives the sender nothing to fix.
            throw new ArgumentException("A rejection needs at least one error.", nameof(errors));
        }

        return Transition(id, MessageState.Rejected, errors);
    }

    /// <summary>The current snapshot of a message.</summary>
    /// <exception cref="KeyNotFoundException">No message has this ID.</exception>
    public TrackedMessage Get(Guid id) =>
        store.Find(id) ?? throw new KeyNotFoundException($"No message with ID {id}.");

    private TrackedMessage Transition(Guid id, MessageState target, IReadOnlyList<MessageError> errors)
    {
        // Optimistic loop: if another thread moved the message between our read and our write,
        // read again and re-check the rule. The loser of a race then fails the guard instead of
        // overwriting the winner (e.g. two concurrent MarkProcessed calls: exactly one succeeds).
        while (true)
        {
            var current = Get(id);
            if (!MessageTransitions.IsAllowed(current.State, target))
            {
                LogRefused(id, current.State, target);
                throw new InvalidOperationException(
                    $"Message {id} cannot move from {current.State} to {target}.");
            }

            var updated = current with
            {
                State = target,
                Version = current.Version + 1,
                Errors = [.. errors],
                History = [.. current.History, new StateChange(current.State, target, timeProvider.GetUtcNow())],
            };

            if (store.TryReplace(current, updated))
            {
                LogTransition(id, current.State, target, errors.Count);
                return updated;
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Received message {MessageId} ({Length} characters)")]
    private partial void LogReceived(Guid messageId, int length);

    [LoggerMessage(Level = LogLevel.Information, Message = "Message {MessageId} moved from {From} to {To} with {ErrorCount} errors")]
    private partial void LogTransition(Guid messageId, MessageState from, MessageState to, int errorCount);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Refused to move message {MessageId} from {From} to {To}")]
    private partial void LogRefused(Guid messageId, MessageState from, MessageState to);
}
