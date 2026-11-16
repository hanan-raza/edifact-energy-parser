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
    MeterDataForwarder forwarder,
    ILogger<MessageController> logger) : ControllerBase
{
    /// <summary>
    /// Receives an MSCONS interchange, parses and validates it, moves it through the workflow and
    /// forwards the values of an accepted message to meter-data-service.
    /// </summary>
    /// <param name="content">The raw EDIFACT text, sent as <c>text/plain</c>.</param>
    /// <response code="200">The message passed every check and is now <c>Processed</c>.</response>
    /// <response code="400">The body is empty or not readable text.</response>
    /// <response code="415">The body is not <c>text/plain</c>.</response>
    /// <response code="422">The message was stored but rejected; the body lists every reason.</response>
    [HttpPost]
    [Consumes("text/plain")]
    [ProducesResponseType<MessageStatusResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status415UnsupportedMediaType)]
    [ProducesResponseType<MessageStatusResponse>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Receive([FromBody] string content)
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

        workflow.MarkValidated(received.Id);
        var processed = workflow.MarkProcessed(received.Id);

        // Deliberately not tied to the request's abort token: the message is already Processed, and a
        // client hanging up halfway must not leave only some metering points forwarded. The typed
        // client's timeout bounds the wait instead.
        var forwarding = await forwarder.ForwardAsync(check.Messages, CancellationToken.None);
        LogForwarded(received.Id, forwarding.Forwarded, forwarding.Failed, forwarding.Skipped);

        return Ok(MessageStatusResponse.From(processed));
    }

    /// <summary>
    /// Explains raw EDIFACT segment by segment: what each segment is for and what each value means.
    /// Nothing is stored, and the text does not have to be a valid message, so a rejected message
    /// can be explained too.
    /// </summary>
    /// <param name="content">The raw EDIFACT text, sent as <c>text/plain</c>.</param>
    /// <param name="explainer">Adapter to the F# explainer.</param>
    /// <response code="200">One entry per segment, UNA included when present.</response>
    /// <response code="400">The body is empty or not readable text.</response>
    /// <response code="415">The body is not <c>text/plain</c>.</response>
    /// <response code="422">The text cannot be split into EDIFACT segments; the detail names line and column.</response>
    [HttpPost("explain")]
    [Consumes("text/plain")]
    [ProducesResponseType<IReadOnlyList<SegmentExplanationResponse>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status415UnsupportedMediaType)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    public IActionResult Explain([FromBody] string content, [FromServices] ExplainerAdapter explainer)
    {
        ArgumentNullException.ThrowIfNull(explainer);

        if (string.IsNullOrWhiteSpace(content))
        {
            ModelState.AddModelError(nameof(content), "The body must contain EDIFACT text.");
            return ValidationProblem(ModelState);
        }

        if (!explainer.TryExplain(content, out var segments, out var error))
        {
            return Problem(statusCode: StatusCodes.Status422UnprocessableEntity, title: "Not EDIFACT", detail: error);
        }

        LogExplained(segments.Count);
        return Ok(segments);
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

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Message {MessageId} handed to meter-data-service: {Forwarded} forwarded, {Failed} failed, {Skipped} metering points skipped")]
    private partial void LogForwarded(Guid messageId, int forwarded, int failed, int skipped);

    [LoggerMessage(Level = LogLevel.Information, Message = "Explained {SegmentCount} segments")]
    private partial void LogExplained(int segmentCount);
}
