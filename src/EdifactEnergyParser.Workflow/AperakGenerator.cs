using System.Globalization;
using System.Text;

namespace EdifactEnergyParser.Workflow;

/// <summary>
/// Codes written in the ERC segment of a response (Fehlercode). Project-specific and deliberately
/// coarse: they tell the sender which side of the fence the problem is on, the FTX text says what it is.
/// </summary>
public static class AperakCodes
{
    /// <summary>The message was accepted (positive acknowledgement, Annahme).</summary>
    public const string Accepted = "A00";

    /// <summary>The interchange or message envelope (UNA/UNB/UNH/UNT/UNZ) is broken.</summary>
    public const string EnvelopeError = "E10";

    /// <summary>The message parsed but breaks a business rule (Prüffehler).</summary>
    public const string ContentError = "E20";
}

/// <summary>
/// Builds a minimal APERAK-style response (Anwendungs-Fehler- und Bestätigungsnachricht) for a
/// received message: an acknowledgement when it was accepted, or a rejection with one ERC/FTX
/// pair per error. The output is a complete interchange that the tokenizer and envelope parser read back.
/// </summary>
/// <remarks>
/// Simplified on purpose: segment choice and qualifiers follow UN/EDIFACT APERAK, but this is not an
/// implementation of any market-specific application handbook.
/// </remarks>
public sealed class AperakGenerator(TimeProvider timeProvider)
{
    /// <summary>Document name code (UNCL 1001) for an acknowledgement message.</summary>
    internal const string AcknowledgementDocument = "312";

    /// <summary>Document name code (UNCL 1001) for an application error message.</summary>
    internal const string ErrorDocument = "313";

    // ISO 9735 defaults, written out in UNA so the receiver need not assume them.
    private const char ComponentSeparator = ':';
    private const char DataElementSeparator = '+';
    private const char ReleaseCharacter = '?';
    private const char SegmentTerminator = '\'';
    private const string ServiceStringAdvice = "UNA:+.? '";

    // UNB 0020 is an..14; UNH 0062 and RFF 1154 allow more, so the tightest limit wins.
    private const int MaxReferenceLength = 14;

    // FTX C108 holds up to five text components of 512 characters each.
    private const int TextComponentLength = 512;
    private const int MaxTextComponents = 5;

    private const string MessageReference = "1";

    private static readonly TimeZoneInfo MarketTimeZone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");

    private static readonly HashSet<string> ServiceSegments = ["", "UNA", "UNB", "UNH", "UNT", "UNZ"];

    /// <summary>Positive response: the message was accepted.</summary>
    /// <param name="original">The interchange being answered.</param>
    /// <param name="responseReference">Control reference for the response interchange (max. 14 characters), assigned by the caller so it can be stored and matched later.</param>
    /// <exception cref="ArgumentException">A reference is empty or longer than 14 characters.</exception>
    public string Accept(ReceivedInterchange original, string responseReference)
    {
        ArgumentNullException.ThrowIfNull(original);
        return Build(original, responseReference, AcknowledgementDocument, [[AperakCodes.Accepted]], []);
    }

    /// <summary>Negative response: the message was rejected for the given reasons.</summary>
    /// <param name="original">The interchange being answered.</param>
    /// <param name="responseReference">Control reference for the response interchange (max. 14 characters).</param>
    /// <param name="errors">At least one reason; each becomes an ERC code and an FTX text naming the segment.</param>
    /// <exception cref="ArgumentException">No error was given, or a reference is empty or too long.</exception>
    public string Reject(ReceivedInterchange original, string responseReference, IReadOnlyList<MessageError> errors)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(errors);
        if (errors.Count == 0)
        {
            throw new ArgumentException("A rejection needs at least one error.", nameof(errors));
        }

        return Build(original, responseReference, ErrorDocument, [], errors);
    }

    /// <summary>Response matching the message's workflow state: accepted once validated, rejected with its stored errors.</summary>
    /// <exception cref="InvalidOperationException">The message is still <see cref="MessageState.Received"/>, so there is no outcome to report yet.</exception>
    public string For(TrackedMessage message, ReceivedInterchange original, string responseReference)
    {
        ArgumentNullException.ThrowIfNull(message);

        return message.State switch
        {
            MessageState.Validated or MessageState.Processed => Accept(original, responseReference),
            MessageState.Rejected => Reject(original, responseReference, message.Errors),
            _ => throw new InvalidOperationException(
                $"Message {message.Id} is {message.State}; a response needs a validation outcome."),
        };
    }

    private string Build(
        ReceivedInterchange original,
        string responseReference,
        string documentCode,
        IReadOnlyList<string[]> acknowledgement,
        IReadOnlyList<MessageError> errors)
    {
        RequireReference(responseReference, nameof(responseReference));
        RequireReference(original.ControlReference, nameof(original));
        RequireReference(original.MessageReference, nameof(original));
        ArgumentException.ThrowIfNullOrWhiteSpace(original.Sender.Id, nameof(original));
        ArgumentException.ThrowIfNullOrWhiteSpace(original.Recipient.Id, nameof(original));

        var local = TimeZoneInfo.ConvertTime(timeProvider.GetUtcNow(), MarketTimeZone);

        // Sender and recipient swap: we answer whoever sent the original.
        var us = original.Recipient;
        var them = original.Sender;

        List<(string Tag, string[][] Elements)> segments =
        [
            ("BGM", [[documentCode], [responseReference]]),
            ("DTM", [["137", Format303(local), "303"]]),
            // ACW: reference to the previous message — here the original interchange, which is how
            // the sender matches our answer to what they sent.
            ("RFF", [["ACW", original.ControlReference]]),
            ("RFF", [["ACE", original.MessageReference]]),
            ("NAD", [["MS"], NadParty(us)]),
            ("NAD", [["MR"], NadParty(them)]),
        ];

        foreach (var code in acknowledgement)
        {
            segments.Add(("ERC", [code]));
        }

        foreach (var error in errors)
        {
            segments.Add(("ERC", [[CodeFor(error)]]));
            segments.Add(("FTX", [["AAO"], [""], [""], TextComponents($"{error.Segment}: {error.Message}")]));
        }

        var sb = new StringBuilder(ServiceStringAdvice).Append('\n');
        Append(sb, "UNB",
            [["UNOC", "3"], Party(us), Party(them),
             [local.ToString("yyMMdd", CultureInfo.InvariantCulture), local.ToString("HHmm", CultureInfo.InvariantCulture)],
             [responseReference]]);
        Append(sb, "UNH", [[MessageReference], ["APERAK", "D", "07B", "UN"]]);
        foreach (var (tag, elements) in segments)
        {
            Append(sb, tag, elements);
        }

        // UNH and UNT count themselves.
        Append(sb, "UNT", [[(segments.Count + 2).ToString(CultureInfo.InvariantCulture)], [MessageReference]]);
        Append(sb, "UNZ", [["1"], [responseReference]]);
        return sb.ToString();
    }

    private static void RequireReference(string value, string paramName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, paramName);
        if (value.Length > MaxReferenceLength)
        {
            throw new ArgumentException($"Reference '{value}' is longer than {MaxReferenceLength} characters.", paramName);
        }
    }

    private static string CodeFor(MessageError error) =>
        ServiceSegments.Contains(error.Segment) ? AperakCodes.EnvelopeError : AperakCodes.ContentError;

    // Format 303 is CCYYMMDDHHMMZZZ: local time plus its whole-hour UTC offset, so the receiver
    // gets the instant without guessing which pass of the repeated October hour was meant.
    private static string Format303(DateTimeOffset local) =>
        local.ToString("yyyyMMddHHmm", CultureInfo.InvariantCulture)
        + (local.Offset < TimeSpan.Zero ? "-" : "+")
        + Math.Abs(local.Offset.Hours).ToString("00", CultureInfo.InvariantCulture);

    private static string[] Party(MarketPartner partner) =>
        partner.CodeQualifier is null ? [partner.Id] : [partner.Id, partner.CodeQualifier];

    // NAD uses responsible-agency codes (UNCL 3055), UNB uses its own qualifiers; same lists, different numbers.
    private static string[] NadParty(MarketPartner partner) => partner.CodeQualifier switch
    {
        "500" => [partner.Id, "", "293"],
        "14" => [partner.Id, "", "9"],
        "502" => [partner.Id, "", "332"],
        _ => [partner.Id],
    };

    private static string[] TextComponents(string text)
    {
        var clean = Sanitize(text);
        var limit = TextComponentLength * MaxTextComponents;
        if (clean.Length > limit)
        {
            clean = string.Concat(clean.AsSpan(0, limit - 3), "...");
        }

        return clean.Chunk(TextComponentLength).Select(c => new string(c)).ToArray();
    }

    // Error texts come from our own validators and may quote sender data. Line breaks would be
    // normalized by the reader (\r\n -> \n), and UNOC is Latin-1, so both are replaced rather
    // than producing a response that does not say what we meant.
    private static string Sanitize(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            sb.Append(char.IsControl(c) ? ' ' : c > 'ÿ' ? '?' : c);
        }

        return sb.ToString();
    }

    private static void Append(StringBuilder sb, string tag, string[][] elements)
    {
        sb.Append(tag);
        foreach (var element in elements)
        {
            sb.Append(DataElementSeparator);
            for (var i = 0; i < element.Length; i++)
            {
                if (i > 0)
                {
                    sb.Append(ComponentSeparator);
                }

                Escape(sb, element[i]);
            }
        }

        // One segment per line: valid (line breaks between segments are tolerated) and readable in a log.
        sb.Append(SegmentTerminator).Append('\n');
    }

    private static void Escape(StringBuilder sb, string value)
    {
        foreach (var c in value)
        {
            if (c is ComponentSeparator or DataElementSeparator or ReleaseCharacter or SegmentTerminator)
            {
                sb.Append(ReleaseCharacter);
            }

            sb.Append(c);
        }
    }
}
