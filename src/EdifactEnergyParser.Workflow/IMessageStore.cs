namespace EdifactEnergyParser.Workflow;

/// <summary>Keeps tracked messages; the workflow only talks to this interface.</summary>
public interface IMessageStore
{
    /// <summary>Stores a new message; <c>false</c> if its ID is already taken.</summary>
    bool TryAdd(TrackedMessage message);

    /// <summary>The current snapshot of a message, or <c>null</c> if the ID is unknown.</summary>
    TrackedMessage? Find(Guid id);

    /// <summary>
    /// Replaces the stored message only if it still equals <paramref name="expected"/> (optimistic concurrency);
    /// <c>false</c> if someone else changed it first or the ID is unknown.
    /// </summary>
    bool TryReplace(TrackedMessage expected, TrackedMessage replacement);
}
