using EdifactEnergyParser.Api.Adapters;
using EdifactEnergyParser.Api.Contracts;
using EdifactEnergyParser.Workflow;
using Microsoft.AspNetCore.Mvc;

namespace EdifactEnergyParser.Api.Controllers;

/// <summary>Receives market messages (Marktnachrichten) and reports where they stand in the workflow.</summary>
[ApiController]
[Route("api/messages")]
public sealed partial class MessageController(
    MessageWorkflow workflow,
    MsconsAdapter adapter,
    ILogger<MessageController> logger) : ControllerBase
{
    /// <summary>Receives an MSCONS interchange, parses and validates it, and moves it through the workflow.</summary>
    /// <param name="content">The raw EDIFACT text, sent as <c>text/plain</c>.</param>
    /// <response code="200">The message passed every check and is now <c>Validated</c>.</response>
    /// <response code="400">The body is empty or not readable text.</response>
    /// <response code="415">The body is not <c>text/plain</c>.</response>
    /// <response code="422">The message was stored but rejected; the body lists every reason.</response>
    [HttpPost]
    [Consumes("text/plain")]
    [ProducesResponseType<MessageStatusResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status415UnsupportedMediaType)]
    [ProducesResponseType<MessageStatusResponse>(StatusCodes.Status422UnprocessableEntity)]
    public IActionResult Receive([FromBody] string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            // Nothing to store: an empty body is a client mistake, not a message to answer with APERAK.
            ModelState.AddModelError(nameof(content), "The body must contain an EDIFACT interchange.");
            return ValidationProblem(ModelState);
        }

        // Stored before checking, so even a rejected message has an ID and an audit trail.
        var received = workflow.Receive(content);
        var check = adapter.Check(content);
        LogChecked(received.Id, check.Interchange?.ControlReference, check.Messages.Count, check.Errors.Count);

        if (!check.IsAccepted)
        {
            var rejected = workflow.Reject(received.Id, check.Errors);
            return UnprocessableEntity(MessageStatusResponse.From(rejected));
        }

        // Processing (handing the values to meter-data-service) is a separate step; until it runs
        // the message stays Validated.
        var validated = workflow.MarkValidated(received.Id);
        return Ok(MessageStatusResponse.From(validated));
    }

    /// <summary>Returns the current state of a message and any rejection reasons.</summary>
    /// <param name="id">The ID returned when the message was received.</param>
    /// <response code="200">The message is known.</response>
    /// <response code="404">No message has this ID.</response>
    [HttpGet("{id:guid}")]
    [ProducesResponseType<MessageStatusResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public IActionResult Get(Guid id)
    {
        try
        {
            return Ok(MessageStatusResponse.From(workflow.Get(id)));
        }
        catch (KeyNotFoundException)
        {
            return Problem(statusCode: StatusCodes.Status404NotFound, title: "Message not found", detail: $"No message with ID {id}.");
        }
    }

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Checked message {MessageId} (interchange {ControlReference}): {MessageCount} messages parsed, {ErrorCount} errors")]
    private partial void LogChecked(Guid messageId, string? controlReference, int messageCount, int errorCount);
}
