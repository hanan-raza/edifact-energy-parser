using EdifactEnergyParser.Workflow;
using Shouldly;

namespace EdifactEnergyParser.Tests.Workflow;

public class MessageTransitionsTests
{
    // Written out in full (all 16 pairs) rather than derived from the production table,
    // so a change to the table has to be made twice on purpose.
    [Theory]
    [InlineData(MessageState.Received, MessageState.Received, false)]
    [InlineData(MessageState.Received, MessageState.Validated, true)]
    [InlineData(MessageState.Received, MessageState.Processed, false)]
    [InlineData(MessageState.Received, MessageState.Rejected, true)]
    [InlineData(MessageState.Validated, MessageState.Received, false)]
    [InlineData(MessageState.Validated, MessageState.Validated, false)]
    [InlineData(MessageState.Validated, MessageState.Processed, true)]
    [InlineData(MessageState.Validated, MessageState.Rejected, true)]
    [InlineData(MessageState.Processed, MessageState.Received, false)]
    [InlineData(MessageState.Processed, MessageState.Validated, false)]
    [InlineData(MessageState.Processed, MessageState.Processed, false)]
    [InlineData(MessageState.Processed, MessageState.Rejected, false)]
    [InlineData(MessageState.Rejected, MessageState.Received, false)]
    [InlineData(MessageState.Rejected, MessageState.Validated, false)]
    [InlineData(MessageState.Rejected, MessageState.Processed, false)]
    [InlineData(MessageState.Rejected, MessageState.Rejected, false)]
    public void IsAllowedMatchesTheStateDiagram(MessageState from, MessageState to, bool expected) =>
        MessageTransitions.IsAllowed(from, to).ShouldBe(expected);

    [Fact]
    public void TheTableCoversEveryState() =>
        Enum.GetValues<MessageState>().ShouldAllBe(state => MessageTransitions.From(state) != null);

    [Theory]
    [InlineData(MessageState.Received, false)]
    [InlineData(MessageState.Validated, false)]
    [InlineData(MessageState.Processed, true)]
    [InlineData(MessageState.Rejected, true)]
    public void OnlyProcessedAndRejectedAreFinal(MessageState state, bool expected) =>
        MessageTransitions.IsFinal(state).ShouldBe(expected);

    [Fact]
    public void AnUndefinedStateIsRefused() =>
        Should.Throw<ArgumentOutOfRangeException>(() => MessageTransitions.From((MessageState)42));
}
