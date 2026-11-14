using EdifactEnergyParser.Api.Adapters;
using Shouldly;

namespace EdifactEnergyParser.Tests.Integration;

public class MsconsAdapterTests
{
    private readonly MsconsAdapter _adapter = new();

    [Fact]
    public void ValidInterchangeIsAcceptedWithItsReferences()
    {
        var result = _adapter.Check(new SyntheticMscons().ToString());

        result.IsAccepted.ShouldBeTrue();
        result.Messages.Count.ShouldBe(1);
        result.Messages[0].MeteringPoints.Head.TimeSeries.Head.Values.Length.ShouldBe(4);

        var interchange = result.Interchange.ShouldNotBeNull();
        interchange.ControlReference.ShouldBe("TST0000001");
        interchange.MessageReference.ShouldBe("1");
        interchange.Sender.Id.ShouldBe("9900000000003");
        interchange.Sender.CodeQualifier.ShouldBe("500");
        interchange.Recipient.Id.ShouldBe("9900000000010");
    }

    [Fact]
    public void SampleFileFromTheDocsIsAccepted()
    {
        var sample = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "mscons_example.edi"));

        _adapter.Check(sample).Errors.ShouldBeEmpty();
    }

    [Fact]
    public void ValidationErrorsAreAllReportedWithMessageReference()
    {
        var result = _adapter.Check(new SyntheticMscons { SenderId = "12345", Quantities = [1m, -2m] }.ToString());

        result.IsAccepted.ShouldBeFalse();
        // The message parsed, so the interchange is known and an APERAK can be addressed.
        result.Interchange.ShouldNotBeNull();
        result.Messages.Count.ShouldBe(1);
        result.Errors.Select(e => e.Segment).ShouldBe(["NAD", "QTY"], ignoreOrder: true);
        result.Errors.ShouldAllBe(e => e.Message.StartsWith("Message 1: "));
    }

    [Fact]
    public void EnvelopeErrorNamesTheTrailerAndSkipsMessageParsing()
    {
        var result = _adapter.Check(new SyntheticMscons { SegmentCountOffset = 1 }.ToString());

        result.Interchange.ShouldBeNull();
        result.Messages.ShouldBeEmpty();
        var error = result.Errors.ShouldHaveSingleItem();
        error.Segment.ShouldBe("UNT");
        error.Message.ShouldContain("declares 24 segments");
    }

    [Fact]
    public void SyntaxErrorReportsPositionWithoutFParsecCaretDrawing()
    {
        var error = _adapter.Check("UNB+UNOC:3+broken").Errors.ShouldHaveSingleItem();

        error.Segment.ShouldBe("");
        error.Message.ShouldStartWith("Syntax error at line 1, column ");
        error.Message.ShouldNotContain("^");
        error.Message.ShouldNotContain("\n");
    }

    [Fact]
    public void UtilmdMessageIsRefusedAsWrongType()
    {
        var utilmd = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "utilmd_example.edi"));

        var error = _adapter.Check(utilmd).Errors.ShouldHaveSingleItem();

        error.Segment.ShouldBe("UNH");
        error.Message.ShouldContain("UTILMD, expected MSCONS");
    }

    [Fact]
    public void StructureErrorNamesTheSegmentWhereParsingStopped()
    {
        // Drops the LIN group's values: the parser reaches UNT while it still expects a QTY.
        var text = new SyntheticMscons { Quantities = [] }.ToString();

        var error = _adapter.Check(text).Errors.ShouldHaveSingleItem();

        error.Segment.ShouldBe("UNT");
        error.Message.ShouldStartWith("Message 1, segment ");
    }
}
