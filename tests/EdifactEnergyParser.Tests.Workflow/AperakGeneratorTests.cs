using EdifactEnergyParser.Core;
using EdifactEnergyParser.Workflow;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace EdifactEnergyParser.Tests.Workflow;

public class AperakGeneratorTests
{
    private const string ResponseReference = "APK0001";

    private static readonly ReceivedInterchange Original = new(
        ControlReference: "MSC20251026",
        MessageReference: "MSG0042",
        Sender: new MarketPartner("9900000000003", "500"),
        Recipient: new MarketPartner("4012345000023", "14"));

    private static readonly MessageError QuantityError = new("QTY", "Quantity -1 is negative.");
    private static readonly MessageError CountError = new("UNT", "UNT declares 12 segments, message has 11.");

    // 10:15 in Berlin (CEST, +02).
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2025, 7, 1, 8, 15, 0, TimeSpan.Zero));
    private readonly AperakGenerator _generator;

    public AperakGeneratorTests() => _generator = new AperakGenerator(_clock);

    [Fact]
    public void RejectionNamesTheOriginalControlNumberAndTheError()
    {
        var text = _generator.Reject(Original, ResponseReference, [QuantityError]);

        text.ShouldContain("RFF+ACW:MSC20251026'");
        text.ShouldContain("FTX+AAO+++QTY?: Quantity -1 is negative.'");

        var body = Body(text);
        Segment(body, "RFF", 0).TryGet(0, 1)!.Value.ShouldBe("MSC20251026");
        Segment(body, "RFF", 1).TryGet(0, 1)!.Value.ShouldBe("MSG0042");
        Segment(body, "FTX", 0).TryGet(3, 0)!.Value.ShouldBe("QTY: Quantity -1 is negative.");
    }

    // Same text as the sample in the README, so the documentation cannot drift from the code.
    [Fact]
    public void RejectionMatchesTheDocumentedSample()
    {
        const string expected = """
            UNA:+.? '
            UNB+UNOC:3+4012345000023:14+9900000000003:500+250701:1015+APK0001'
            UNH+1+APERAK:D:07B:UN'
            BGM+313+APK0001'
            DTM+137:202507011015?+02:303'
            RFF+ACW:MSC20251026'
            RFF+ACE:MSG0042'
            NAD+MS+4012345000023::9'
            NAD+MR+9900000000003::293'
            ERC+E20'
            FTX+AAO+++QTY?: Quantity -1 is negative.'
            UNT+10+1'
            UNZ+1+APK0001'

            """;

        _generator.Reject(Original, ResponseReference, [QuantityError])
            .ShouldBe(expected.ReplaceLineEndings("\n"));
    }

    [Fact]
    public void RejectionIsValidEdifactThatTheTokenizerReadsBack()
    {
        var text = _generator.Reject(Original, ResponseReference, [QuantityError]);

        var tokens = Tokenizer.tokenize(text);

        tokens.IsOk.ShouldBeTrue(tokens.IsError ? tokens.ErrorValue.Message : "");
        tokens.ResultValue.Delimiters.ShouldBe(Delimiters.Default);
        tokens.ResultValue.Tokens.Count(t => t.IsSegmentTerminator).ShouldBe(text.Count(c => c == '\n') - 1);
    }

    [Fact]
    public void RejectionPassesTheEnvelopeChecksWithSenderAndRecipientSwapped()
    {
        var text = _generator.Reject(Original, ResponseReference, [QuantityError, CountError]);

        // The envelope parser also checks the UNT segment count and the UNZ control reference.
        var envelope = Parse(text);

        envelope.MessageType.ShouldBe("APERAK");
        envelope.ControlReference.ShouldBe(ResponseReference);
        envelope.Sender.Id.ShouldBe("4012345000023");
        envelope.Sender.CodeQualifier!.Value.ShouldBe("14");
        envelope.Recipient.Id.ShouldBe("9900000000003");
        envelope.Recipient.CodeQualifier!.Value.ShouldBe("500");
        envelope.Prepared.ShouldBe(_clock.GetUtcNow());

        var body = envelope.Messages.Single().Segments;
        Segment(body, "BGM", 0).TryGet(0, 0)!.Value.ShouldBe("313");
        Elements(Segment(body, "NAD", 0)).ShouldBe("MS | 4012345000023,,9");
        Elements(Segment(body, "NAD", 1)).ShouldBe("MR | 9900000000003,,293");
    }

    [Fact]
    public void EveryErrorGetsACodeAndATextInOrder()
    {
        var text = _generator.Reject(Original, ResponseReference, [QuantityError, CountError]);

        var body = Body(text);
        Tags(body).ShouldBe(["BGM", "DTM", "RFF", "RFF", "NAD", "NAD", "ERC", "FTX", "ERC", "FTX"]);
        Segment(body, "ERC", 0).TryGet(0, 0)!.Value.ShouldBe(AperakCodes.ContentError);
        Segment(body, "FTX", 0).TryGet(3, 0)!.Value.ShouldBe("QTY: Quantity -1 is negative.");
        Segment(body, "ERC", 1).TryGet(0, 0)!.Value.ShouldBe(AperakCodes.EnvelopeError);
        Segment(body, "FTX", 1).TryGet(3, 0)!.Value.ShouldBe("UNT: UNT declares 12 segments, message has 11.");
    }

    [Fact]
    public void AcceptanceCarriesTheAcknowledgementCodeAndNoErrorText()
    {
        var text = _generator.Accept(Original, ResponseReference);

        var body = Body(text);
        Tags(body).ShouldBe(["BGM", "DTM", "RFF", "RFF", "NAD", "NAD", "ERC"]);
        Segment(body, "BGM", 0).TryGet(0, 0)!.Value.ShouldBe("312");
        Segment(body, "BGM", 0).TryGet(1, 0)!.Value.ShouldBe(ResponseReference);
        Segment(body, "ERC", 0).TryGet(0, 0)!.Value.ShouldBe(AperakCodes.Accepted);
        Segment(body, "RFF", 0).TryGet(0, 1)!.Value.ShouldBe("MSC20251026");
    }

    [Fact]
    public void DelimitersInsideErrorTextAreReleasedAndReadBackUnchanged()
    {
        const string message = "Value '1+2:3' is not a number? Use '.' as decimal mark.";

        var text = _generator.Reject(Original, ResponseReference, [new MessageError("QTY", message)]);

        text.ShouldContain("?'1?+2?:3?' is not a number?? Use ?'.?' as decimal mark.");
        Segment(Body(text), "FTX", 0).TryGet(3, 0)!.Value.ShouldBe("QTY: " + message);
    }

    [Fact]
    public void LineBreaksAndCharactersOutsideLatin1AreReplaced()
    {
        var text = _generator.Reject(Original, ResponseReference, [new MessageError("FTX", "Price 5€\r\nper kWh, über")]);

        Segment(Body(text), "FTX", 0).TryGet(3, 0)!.Value.ShouldBe("FTX: Price 5?  per kWh, über");
    }

    [Fact]
    public void LongErrorTextIsSplitIntoFiveComponentsAndCut()
    {
        var text = _generator.Reject(Original, ResponseReference, [new MessageError("QTY", new string('x', 3000))]);

        var components = Segment(Body(text), "FTX", 0).Elements.ElementAt(3).ToArray();
        components.Length.ShouldBe(5);
        components.ShouldAllBe(c => c.Length == 512);
        string.Concat(components).ShouldEndWith("xxx...");
    }

    [Theory]
    // Winter: CET, +01.
    [InlineData("2025-01-15T09:00:00Z", "202501151000+01", "250115", "1000")]
    // Summer: CEST, +02.
    [InlineData("2025-07-01T08:15:00Z", "202507011015+02", "250701", "1015")]
    // Both passes of the repeated October hour: same local time, the offset tells them apart.
    [InlineData("2025-10-26T00:30:00Z", "202510260230+02", "251026", "0230")]
    [InlineData("2025-10-26T01:30:00Z", "202510260230+01", "251026", "0230")]
    // Just after the March switch: 01:59+01 is followed by 03:00+02.
    [InlineData("2025-03-30T01:00:00Z", "202503300300+02", "250330", "0300")]
    public void DocumentDateIsGermanLocalTimeWithItsOffset(string utc, string dtm, string unbDate, string unbTime)
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse(utc, System.Globalization.CultureInfo.InvariantCulture));

        var text = new AperakGenerator(clock).Accept(Original, ResponseReference);

        text.ShouldContain($"UNB+UNOC:3+4012345000023:14+9900000000003:500+{unbDate}:{unbTime}+{ResponseReference}'");
        Elements(Segment(Body(text), "DTM", 0)).ShouldBe($"137,{dtm},303");
    }

    [Fact]
    public void PartnersWithoutCodeListAreWrittenWithoutQualifier()
    {
        var original = Original with
        {
            Sender = new MarketPartner("9900000000003", null),
            Recipient = new MarketPartner("4012345000023", null),
        };

        var envelope = Parse(_generator.Accept(original, ResponseReference));

        envelope.Sender.CodeQualifier.ShouldBeNull();
        Elements(Segment(envelope.Messages.Single().Segments, "NAD", 0)).ShouldBe("MS | 4012345000023");
    }

    [Fact]
    public void RejectionWithoutErrorsIsRefused() =>
        Should.Throw<ArgumentException>(() => _generator.Reject(Original, ResponseReference, []));

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("APK000000000001")]
    public void InvalidResponseReferenceIsRefused(string reference) =>
        Should.Throw<ArgumentException>(() => _generator.Accept(Original, reference));

    [Fact]
    public void OverlongOriginalControlReferenceIsRefused() =>
        Should.Throw<ArgumentException>(() =>
            _generator.Accept(Original with { ControlReference = new string('9', 15) }, ResponseReference));

    [Fact]
    public void ResponseFollowsTheWorkflowState()
    {
        var workflow = new MessageWorkflow(new InMemoryMessageStore(), _clock, NullLogger<MessageWorkflow>.Instance);
        var received = workflow.Receive("UNB+UNOC:3+9900000000003:500+4012345000023:14+250701:1015+MSC20251026'");

        Should.Throw<InvalidOperationException>(() => _generator.For(received, Original, ResponseReference));

        var rejected = workflow.Reject(received.Id, [QuantityError]);
        _generator.For(rejected, Original, ResponseReference)
            .ShouldBe(_generator.Reject(Original, ResponseReference, [QuantityError]));

        var accepted = workflow.MarkProcessed(workflow.MarkValidated(workflow.Receive("UNB'").Id).Id);
        _generator.For(accepted, Original, ResponseReference)
            .ShouldBe(_generator.Accept(Original, ResponseReference));
    }

    private static InterchangeEnvelope Parse(string text)
    {
        var result = EnvelopeParser.parse(text);
        result.IsOk.ShouldBeTrue(result.IsError ? string.Join("; ", result.ErrorValue) : "");
        return result.ResultValue;
    }

    private static IReadOnlyList<Segment> Body(string text) => [.. Parse(text).Messages.Single().Segments];

    // Unescaped values, elements joined by " | " and components by ",", so a failure shows the whole segment.
    private static string Elements(Segment segment) =>
        string.Join(" | ", segment.Elements.Select(e => string.Join(",", e)));

    private static string[] Tags(IEnumerable<Segment> body) => [.. body.Select(s => s.Tag)];

    private static Segment Segment(IEnumerable<Segment> body, string tag, int occurrence) =>
        body.Where(s => s.Tag == tag).ElementAt(occurrence);
}
