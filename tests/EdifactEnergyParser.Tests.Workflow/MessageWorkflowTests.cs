using EdifactEnergyParser.Workflow;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace EdifactEnergyParser.Tests.Workflow;

public class MessageWorkflowTests
{
    private const string Content = "UNB+UNOC:3+9900000000003:500+9900000000010:500+250101:0000+REF1'";

    private static readonly DateTimeOffset Start = new(2025, 10, 26, 0, 30, 0, TimeSpan.Zero);
    private static readonly MessageError QuantityError = new("QTY", "Quantity -1 is negative.");

    private readonly FakeTimeProvider _clock = new(Start);
    private readonly InMemoryMessageStore _store = new();
    private readonly MessageWorkflow _workflow;

    public MessageWorkflowTests() =>
        _workflow = new MessageWorkflow(_store, _clock, NullLogger<MessageWorkflow>.Instance);

    [Fact]
    public void ReceiveStoresTheMessageAsReceived()
    {
        var message = _workflow.Receive(Content);

        message.State.ShouldBe(MessageState.Received);
        message.Content.ShouldBe(Content);
        message.Version.ShouldBe(0);
        message.Errors.ShouldBeEmpty();
        message.History.ShouldBe([new StateChange(null, MessageState.Received, Start)]);
        _store.Find(message.Id).ShouldBe(message);
    }

    [Fact]
    public void ReceiveAssignsDistinctTimeOrderedIds()
    {
        var first = _workflow.Receive(Content);
        _clock.Advance(TimeSpan.FromMilliseconds(1));
        var second = _workflow.Receive(Content);

        second.Id.ShouldNotBe(first.Id);
        second.Id.CompareTo(first.Id).ShouldBeGreaterThan(0);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ReceiveRefusesEmptyContent(string content) =>
        Should.Throw<ArgumentException>(() => _workflow.Receive(content));

    [Fact]
    public void AcceptedMessageGoesReceivedValidatedProcessed()
    {
        var id = _workflow.Receive(Content).Id;
        _clock.Advance(TimeSpan.FromSeconds(1));
        _workflow.MarkValidated(id).State.ShouldBe(MessageState.Validated);
        _clock.Advance(TimeSpan.FromSeconds(2));
        var processed = _workflow.MarkProcessed(id);

        processed.State.ShouldBe(MessageState.Processed);
        processed.Version.ShouldBe(2);
        processed.Errors.ShouldBeEmpty();
        processed.History.ShouldBe(
        [
            new StateChange(null, MessageState.Received, Start),
            new StateChange(MessageState.Received, MessageState.Validated, Start.AddSeconds(1)),
            new StateChange(MessageState.Validated, MessageState.Processed, Start.AddSeconds(3)),
        ]);
        _workflow.Get(id).ShouldBe(processed);
    }

    [Fact]
    public void MessageFailingValidationGoesReceivedRejectedWithItsErrors()
    {
        var id = _workflow.Receive(Content).Id;
        MessageError[] errors = [QuantityError, new("NAD", "Sender ID must have 13 digits.")];

        var rejected = _workflow.Reject(id, errors);

        rejected.State.ShouldBe(MessageState.Rejected);
        rejected.Errors.ShouldBe(errors);
        rejected.History.Select(change => change.To)
            .ShouldBe([MessageState.Received, MessageState.Rejected]);
        _workflow.Get(id).ShouldBe(rejected);
    }

    [Fact]
    public void MessageFailingProcessingGoesValidatedRejected()
    {
        var id = _workflow.Receive(Content).Id;
        _workflow.MarkValidated(id);

        var rejected = _workflow.Reject(id, [new MessageError("LOC", "Unknown metering point.")]);

        rejected.State.ShouldBe(MessageState.Rejected);
        rejected.History.Select(change => change.To)
            .ShouldBe([MessageState.Received, MessageState.Validated, MessageState.Rejected]);
    }

    // Every valid transition, driven through the public methods.
    [Theory]
    [InlineData(MessageState.Received, MessageState.Validated)]
    [InlineData(MessageState.Received, MessageState.Rejected)]
    [InlineData(MessageState.Validated, MessageState.Processed)]
    [InlineData(MessageState.Validated, MessageState.Rejected)]
    public void ValidTransitionSucceeds(MessageState from, MessageState to)
    {
        var id = MessageIn(from);

        var updated = Apply(id, to);

        updated.State.ShouldBe(to);
        updated.History[^1].ShouldBe(new StateChange(from, to, Start));
        _workflow.Get(id).ShouldBe(updated);
    }

    // Every transition a method exists for but the state diagram forbids. Moving back to
    // Received has no method at all (a new receipt always creates a new message).
    [Theory]
    [InlineData(MessageState.Received, MessageState.Processed)]
    [InlineData(MessageState.Validated, MessageState.Validated)]
    [InlineData(MessageState.Processed, MessageState.Validated)]
    [InlineData(MessageState.Processed, MessageState.Processed)]
    [InlineData(MessageState.Processed, MessageState.Rejected)]
    [InlineData(MessageState.Rejected, MessageState.Validated)]
    [InlineData(MessageState.Rejected, MessageState.Processed)]
    [InlineData(MessageState.Rejected, MessageState.Rejected)]
    public void InvalidTransitionThrowsAndLeavesTheMessageUnchanged(MessageState from, MessageState to)
    {
        var id = MessageIn(from);
        var before = _workflow.Get(id);

        var exception = Should.Throw<InvalidOperationException>(() => Apply(id, to));

        exception.Message.ShouldContain($"from {from} to {to}");
        _workflow.Get(id).ShouldBe(before);
    }

    [Fact]
    public void RejectWithoutErrorsThrowsAndLeavesTheMessageUnchanged()
    {
        var id = _workflow.Receive(Content).Id;

        Should.Throw<ArgumentException>(() => _workflow.Reject(id, []));

        _workflow.Get(id).State.ShouldBe(MessageState.Received);
    }

    [Fact]
    public void RejectKeepsItsOwnCopyOfTheErrors()
    {
        var id = _workflow.Receive(Content).Id;
        var errors = new List<MessageError> { QuantityError };

        _workflow.Reject(id, errors);
        errors.Add(new MessageError("DTM", "Added later."));

        _workflow.Get(id).Errors.ShouldBe([QuantityError]);
    }

    [Fact]
    public void UnknownIdThrowsKeyNotFound()
    {
        var unknown = Guid.CreateVersion7();

        Should.Throw<KeyNotFoundException>(() => _workflow.Get(unknown));
        Should.Throw<KeyNotFoundException>(() => _workflow.MarkValidated(unknown));
        Should.Throw<KeyNotFoundException>(() => _workflow.MarkProcessed(unknown));
        Should.Throw<KeyNotFoundException>(() => _workflow.Reject(unknown, [QuantityError]));
    }

    [Fact]
    public async Task ConcurrentTransitionsOfOneMessageLetExactlyOneWin()
    {
        var id = MessageIn(MessageState.Validated);
        const int contenders = 16;
        using var gate = new Barrier(contenders);

        // Half try to process, half to reject; only one may move the message out of Validated.
        // LongRunning gives each contender its own thread: blocking pool threads on the barrier
        // would starve the pool and take seconds to release.
        var attempts = Enumerable.Range(0, contenders).Select(i => Task.Factory.StartNew(() =>
        {
            gate.SignalAndWait();
            try
            {
                _ = i % 2 == 0 ? _workflow.MarkProcessed(id) : _workflow.Reject(id, [QuantityError]);
                return true;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default));
        var outcomes = await Task.WhenAll(attempts);

        outcomes.Count(won => won).ShouldBe(1);
        var final = _workflow.Get(id);
        final.Version.ShouldBe(2);
        final.History.Count.ShouldBe(3);
        MessageTransitions.IsFinal(final.State).ShouldBeTrue();
    }

    private Guid MessageIn(MessageState state)
    {
        var id = _workflow.Receive(Content).Id;
        switch (state)
        {
            case MessageState.Received:
                break;
            case MessageState.Validated:
                _workflow.MarkValidated(id);
                break;
            case MessageState.Processed:
                _workflow.MarkValidated(id);
                _workflow.MarkProcessed(id);
                break;
            case MessageState.Rejected:
                _workflow.Reject(id, [QuantityError]);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(state), state, null);
        }

        _workflow.Get(id).State.ShouldBe(state);
        return id;
    }

    private TrackedMessage Apply(Guid id, MessageState target) => target switch
    {
        MessageState.Validated => _workflow.MarkValidated(id),
        MessageState.Processed => _workflow.MarkProcessed(id),
        MessageState.Rejected => _workflow.Reject(id, [QuantityError]),
        _ => throw new ArgumentOutOfRangeException(nameof(target), target, "No method moves a message there."),
    };
}
