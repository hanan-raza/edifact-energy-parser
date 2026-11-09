using System.Collections.Concurrent;

namespace EdifactEnergyParser.Workflow;

/// <summary>Thread-safe message store for a single process; contents are lost on restart.</summary>
public sealed class InMemoryMessageStore : IMessageStore
{
    private readonly ConcurrentDictionary<Guid, TrackedMessage> _messages = new();

    /// <inheritdoc />
    public bool TryAdd(TrackedMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return _messages.TryAdd(message.Id, message);
    }

    /// <inheritdoc />
    public TrackedMessage? Find(Guid id) => _messages.TryGetValue(id, out var message) ? message : null;

    /// <inheritdoc />
    public bool TryReplace(TrackedMessage expected, TrackedMessage replacement)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(replacement);
        if (expected.Id != replacement.Id)
        {
            throw new ArgumentException("A message cannot change its ID.", nameof(replacement));
        }

        // TryUpdate compares with record equality, which includes Version, so a snapshot that
        // is one transition behind never matches: this is a compare-and-swap, not a blind write.
        return _messages.TryUpdate(expected.Id, replacement, expected);
    }
}
