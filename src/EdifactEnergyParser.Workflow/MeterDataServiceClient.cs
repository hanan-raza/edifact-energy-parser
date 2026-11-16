using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Logging;

namespace EdifactEnergyParser.Workflow;

/// <summary>
/// Typed <see cref="HttpClient"/> for meter-data-service: uploads values to <c>POST /api/import</c>
/// as the CSV file (<c>timestamp,value,status</c>) that endpoint accepts.
/// </summary>
/// <remarks>
/// The CSV upload is reused instead of adding a JSON endpoint to meter-data-service, so values from
/// MSCONS go through exactly the same parsing, validation and gap detection as a manual import.
/// </remarks>
public sealed partial class MeterDataServiceClient(HttpClient http, ILogger<MeterDataServiceClient> logger)
{
    /// <summary>Path of the import endpoint, relative to the base URL.</summary>
    public const string ImportPath = "api/import";

    /// <summary>Header row meter-data-service requires.</summary>
    public const string CsvHeader = "timestamp,value,status";

    /// <summary>Uploads one batch and returns the URL of the import job that meter-data-service queued.</summary>
    /// <exception cref="HttpRequestException">meter-data-service is unreachable or refused the upload.</exception>
    public async Task<Uri?> ImportAsync(MeterReadingBatch batch, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(batch);

        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(batch.MarketLocationId), "marketLocationId");
        var file = new StringContent(ToCsv(batch.Readings), Encoding.UTF8);
        file.Headers.ContentType = new MediaTypeHeaderValue("text/csv") { CharSet = "utf-8" };
        form.Add(file, "file", batch.FileName);

        using var response = await http.PostAsync(new Uri(ImportPath, UriKind.Relative), form, cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            LogRefused(batch.MarketLocationId, batch.FileName, (int)response.StatusCode, body);
            throw new HttpRequestException(
                $"meter-data-service refused {batch.FileName} with {(int)response.StatusCode}.", null, response.StatusCode);
        }

        LogImported(batch.MarketLocationId, batch.FileName, batch.Readings.Count, response.Headers.Location);
        return response.Headers.Location;
    }

    /// <summary>Renders the readings as the CSV that meter-data-service imports.</summary>
    public static string ToCsv(IEnumerable<MeterReading> readings)
    {
        ArgumentNullException.ThrowIfNull(readings);

        var csv = new StringBuilder(CsvHeader).Append('\n');
        foreach (var reading in readings)
        {
            // The offset is always written: without it meter-data-service would read German local
            // time and could only guess which 02:00 is meant on the 25-hour day.
            csv.Append(CultureInfo.InvariantCulture,
                $"{reading.IntervalStart:yyyy-MM-dd'T'HH:mm:sszzz},{reading.KilowattHours},{reading.Status}\n");
        }

        return csv.ToString();
    }

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Forwarded {ReadingCount} values for {MarketLocationId} as {FileName}; import job {JobLocation}")]
    private partial void LogImported(string marketLocationId, string fileName, int readingCount, Uri? jobLocation);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "meter-data-service refused {FileName} for {MarketLocationId} with {StatusCode}: {ResponseBody}")]
    private partial void LogRefused(string marketLocationId, string fileName, int statusCode, string responseBody);
}
