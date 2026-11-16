using EdifactEnergyParser.Core;
using EdifactEnergyParser.Workflow;
using Microsoft.Extensions.Options;

namespace EdifactEnergyParser.Api.Adapters;

/// <summary>Outcome of handing a processed interchange to meter-data-service.</summary>
/// <param name="Forwarded">Batches meter-data-service accepted.</param>
/// <param name="Failed">Batches that could not be delivered.</param>
/// <param name="Skipped">Metering points that were not sent at all.</param>
public sealed record MeterDataForwardingResult(int Forwarded, int Failed, int Skipped);

/// <summary>Sends the values of a processed MSCONS to meter-data-service, one import per metering point.</summary>
/// <remarks>
/// A failed upload is logged and does not stop the other batches: the message is already accepted,
/// so a downstream outage is not the sender's fault and must not turn into a negative APERAK.
/// A production system would write the batches to an outbox and retry; this keeps the hand-over
/// in-process and visible in the log.
/// </remarks>
public sealed partial class MeterDataForwarder(
    MeterDataServiceClient client,
    IOptions<MeterDataServiceOptions> options,
    ILogger<MeterDataForwarder> logger)
{
    /// <summary>Plans and uploads the batches; never throws for a delivery problem.</summary>
    public async Task<MeterDataForwardingResult> ForwardAsync(IReadOnlyList<MsconsMessage> messages, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(messages);

        var settings = options.Value;
        if (settings.BaseUrl is null)
        {
            LogDisabled();
            return new MeterDataForwardingResult(0, 0, 0);
        }

        var plan = MeterReadingMapper.Plan(messages, settings.MarketLocations);
        foreach (var skipped in plan.Skipped)
        {
            LogSkipped(skipped.MessageReference, skipped.MeteringPointId, skipped.Reason);
        }

        int forwarded = 0, failed = 0;
        foreach (var batch in plan.Batches)
        {
            try
            {
                await client.ImportAsync(batch, cancellationToken).ConfigureAwait(false);
                forwarded++;
            }
            catch (HttpRequestException ex)
            {
                LogDeliveryFailed(ex, batch.FileName, batch.MarketLocationId);
                failed++;
            }
            catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                // HttpClient reports its own timeout as a cancellation; only the caller's token is a real abort.
                LogDeliveryFailed(ex, batch.FileName, batch.MarketLocationId);
                failed++;
            }
        }

        return new MeterDataForwardingResult(forwarded, failed, plan.Skipped.Count);
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Forwarding to meter-data-service is off (no MeterDataService:BaseUrl)")]
    private partial void LogDisabled();

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Not forwarding metering point {MeteringPointId} of message {MessageReference}: {Reason}")]
    private partial void LogSkipped(string messageReference, string meteringPointId, string reason);

    [LoggerMessage(Level = LogLevel.Error, Message = "Could not deliver {FileName} for {MarketLocationId} to meter-data-service")]
    private partial void LogDeliveryFailed(Exception exception, string fileName, string marketLocationId);
}
