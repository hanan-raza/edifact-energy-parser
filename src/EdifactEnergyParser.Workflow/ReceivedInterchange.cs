namespace EdifactEnergyParser.Workflow;

/// <summary>A market partner as named in UNB (Marktpartner): ID plus the code list it comes from.</summary>
/// <param name="Id">Market-partner ID (Marktpartner-ID), e.g. a 13-digit BDEW code number.</param>
/// <param name="CodeQualifier">UNB code list qualifier, e.g. "500" (BDEW), "14" (GS1), "502" (DVGW); <c>null</c> if absent.</param>
public sealed record MarketPartner(string Id, string? CodeQualifier);

/// <summary>
/// What an acknowledgement has to refer back to from the received interchange (Bezug zur Originalnachricht).
/// </summary>
/// <remarks>
/// Plain strings rather than the F# envelope, so the generator stays free of F# types; the API
/// adapter copies these four values out of the parsed <c>InterchangeEnvelope</c>.
/// </remarks>
/// <param name="ControlReference">Interchange control reference (Datenaustauschreferenz, UNB 0020) of the original.</param>
/// <param name="MessageReference">Message reference number (Nachrichten-Referenznummer, UNH 0062) of the original message.</param>
/// <param name="Sender">Who sent the original; becomes the recipient of the response.</param>
/// <param name="Recipient">Who received the original (us); becomes the sender of the response.</param>
public sealed record ReceivedInterchange(
    string ControlReference,
    string MessageReference,
    MarketPartner Sender,
    MarketPartner Recipient);
