namespace EdifactEnergyParser.Workflow;

/// <summary>Processing state of an incoming market message (Bearbeitungsstatus einer Nachricht).</summary>
public enum MessageState
{
    /// <summary>Accepted from the sender and stored, not checked yet (empfangen).</summary>
    Received,

    /// <summary>Parsed and passed every validation rule (geprüft).</summary>
    Validated,

    /// <summary>Handed to the business process, e.g. meter values imported (verarbeitet). Final.</summary>
    Processed,

    /// <summary>Refused with at least one reason, answered with a negative APERAK (abgelehnt). Final.</summary>
    Rejected,
}
