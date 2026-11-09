using EdifactEnergyParser.Workflow;
using Shouldly;

namespace EdifactEnergyParser.Tests.Workflow;

public class InMemoryMessageStoreTests
{
    private static readonly DateTimeOffset At = new(2025, 3, 30, 1, 0, 0, TimeSpan.Zero);

    private readonly InMemoryMessageStore _store = new();

    private static TrackedMessage NewMessage() =>
        new(Guid.CreateVersion7(At), "UNA:+.? '", MessageState.Received, 0, [],
            [new StateChange(null, MessageState.Received, At)]);

    private static TrackedMessage Next(TrackedMessage message, MessageState state) =>
        message with { State = state, Version = message.Version + 1 };

    [Fact]
    public void FindReturnsWhatWasAdded()
    {
        var message = NewMessage();

        _store.TryAdd(message).ShouldBeTrue();

        _store.Find(message.Id).ShouldBe(message);
    }

    [Fact]
    public void FindReturnsNullForAnUnknownId() =>
        _store.Find(Guid.CreateVersion7(At)).ShouldBeNull();

    [Fact]
    public void TryAddRefusesADuplicateIdAndKeepsTheFirst()
    {
        var message = NewMessage();
        _store.TryAdd(message);

        _store.TryAdd(Next(message, MessageState.Validated)).ShouldBeFalse();

        _store.Find(message.Id).ShouldBe(message);
    }

    [Fact]
    public void TryReplaceSucceedsAgainstTheCurrentSnapshot()
    {
        var message = NewMessage();
        _store.TryAdd(message);
        var validated = Next(message, MessageState.Validated);

        _store.TryReplace(message, validated).ShouldBeTrue();

        _store.Find(message.Id).ShouldBe(validated);
    }

    [Fact]
    public void TryReplaceRefusesAStaleSnapshot()
    {
        var stale = NewMessage();
        _store.TryAdd(stale);
        var validated = Next(stale, MessageState.Validated);
        _store.TryReplace(stale, validated);

        _store.TryReplace(stale, Next(stale, MessageState.Rejected)).ShouldBeFalse();

        _store.Find(stale.Id).ShouldBe(validated);
    }

    [Fact]
    public void TryReplaceReturnsFalseForAnUnknownId()
    {
        var message = NewMessage();

        _store.TryReplace(message, Next(message, MessageState.Validated)).ShouldBeFalse();
    }

    [Fact]
    public void TryReplaceRefusesToChangeTheId()
    {
        var message = NewMessage();
        _store.TryAdd(message);

        Should.Throw<ArgumentException>(() =>
            _store.TryReplace(message, message with { Id = Guid.CreateVersion7(At) }));
    }
}
