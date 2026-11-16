using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using EdifactEnergyParser.Api.Contracts;
using EdifactEnergyParser.Workflow;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
using Shouldly;
using WireMock.Logging;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace EdifactEnergyParser.Tests.Integration;

/// <summary>
/// MSCONS in, meter-data-service imports out: the API runs in memory and meter-data-service is a
/// WireMock stub, so the test sees exactly the multipart uploads that would go over the wire.
/// </summary>
public sealed class MeterDataForwardingTests : IDisposable
{
    private const string MeteringPoint1 = "DE0001234567800000000000000000001";
    private const string MeteringPoint2 = "DE0001234567800000000000000000002";
    private const string MeteringPoint3 = "DE0001234567800000000000000000003";
    private const string MarketLocation1 = "51238696781";
    private const string MarketLocation2 = "10000000009";
    private const string MarketLocation3 = "41000000004";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private static readonly Dictionary<string, string> AllMapped = new()
    {
        [MeteringPoint1] = MarketLocation1,
        [MeteringPoint2] = MarketLocation2,
        [MeteringPoint3] = MarketLocation3,
    };

    private readonly WireMockServer _meterDataService = WireMockServer.Start();
    private ApiFactory? _factory;

    public void Dispose()
    {
        _factory?.Dispose();
        _meterDataService.Dispose();
    }

    private void StubImportReturns(HttpStatusCode status) =>
        _meterDataService
            .Given(Request.Create().WithPath("/api/import").UsingPost())
            .RespondWith(Response.Create()
                .WithStatusCode(status)
                .WithHeader("Location", $"{_meterDataService.Url}/api/import/{Guid.NewGuid()}"));

    private ApiFactory NewFactory(IReadOnlyDictionary<string, string> marketLocations) =>
        new() { MeterDataService = new Uri(_meterDataService.Url!), MarketLocations = marketLocations };

    private async Task<MessageStatusResponse> PostMessageAsync(string edifact, IReadOnlyDictionary<string, string> marketLocations)
    {
        _factory = NewFactory(marketLocations);
        using var client = _factory.CreateClient();
        using var response = await client.PostAsync(
            new Uri("/api/messages", UriKind.Relative), new StringContent(edifact, Encoding.UTF8, "text/plain"));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<MessageStatusResponse>(Json)).ShouldNotBeNull();
    }

    private static string Sample(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, name));

    private IReadOnlyList<ImportUpload> Uploads() =>
        [.. _meterDataService.LogEntries.Select(ImportUpload.Read).OrderBy(u => u.MarketLocationId, StringComparer.Ordinal)];

    [Fact]
    public async Task SampleMsconsSendsOneCsvImportPerMeteringPoint()
    {
        StubImportReturns(HttpStatusCode.Accepted);

        var status = await PostMessageAsync(Sample("mscons_example.edi"), AllMapped);

        status.State.ShouldBe(MessageState.Processed);
        var uploads = Uploads();
        uploads.Count.ShouldBe(2);

        // Point 1 is a household series (1.29.0) with one substitute value (QTY+67).
        uploads[1].MarketLocationId.ShouldBe(MarketLocation1);
        uploads[1].FileName.ShouldBe($"MSI0000001_{MeteringPoint1}.csv");
        uploads[1].ContentType.ShouldBe("text/csv; charset=utf-8");
        uploads[1].Csv.ShouldBe(
            """
            timestamp,value,status
            2026-01-15T00:00:00+01:00,0.250,Measured
            2026-01-15T00:15:00+01:00,0.198,Measured
            2026-01-15T00:30:00+01:00,0.205,Replaced
            2026-01-15T00:45:00+01:00,0.231,Measured

            """.ReplaceLineEndings("\n"));

        // Point 2 is a feed-in series (2.29.0) with one proposed value (QTY+201).
        uploads[0].MarketLocationId.ShouldBe(MarketLocation2);
        uploads[0].Csv.ShouldBe(
            """
            timestamp,value,status
            2026-01-15T12:00:00+01:00,1.420,Measured
            2026-01-15T12:15:00+01:00,1.385,Estimated

            """.ReplaceLineEndings("\n"));

        // Point 3 has two energy registers; both would land in one market location series, so it is skipped.
        uploads.ShouldNotContain(u => u.MarketLocationId == MarketLocation3);
    }

    [Fact]
    public async Task FallBackDayKeepsBothTwoOClockHoursApart()
    {
        StubImportReturns(HttpStatusCode.Accepted);
        // 25-hour day: 02:00-03:00 happens twice, first in summer time (+02), then in winter time (+01).
        var mscons = new SyntheticMscons
        {
            Start = new DateTimeOffset(2026, 10, 25, 2, 30, 0, TimeSpan.FromHours(2)),
            Quantities = [0.1m, 0.2m, 0.3m, 0.4m],
        };

        await PostMessageAsync(mscons.ToString(), AllMapped);

        Uploads().ShouldHaveSingleItem().Csv.ShouldBe(
            """
            timestamp,value,status
            2026-10-25T02:30:00+02:00,0.1,Measured
            2026-10-25T02:45:00+02:00,0.2,Measured
            2026-10-25T02:00:00+01:00,0.3,Measured
            2026-10-25T02:15:00+01:00,0.4,Measured

            """.ReplaceLineEndings("\n"));
    }

    [Fact]
    public async Task MegawattHoursAreConvertedToKilowattHours()
    {
        StubImportReturns(HttpStatusCode.Accepted);
        var mscons = new SyntheticMscons { Unit = "MWH", Quantities = [0.5m, 0.00125m] };

        await PostMessageAsync(mscons.ToString(), AllMapped);

        var rows = Uploads().ShouldHaveSingleItem().Csv.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        rows.Skip(1).Select(r => r.Split(',')[1]).ShouldBe(["500.0", "1.25000"]);
    }

    [Fact]
    public async Task ElevenDigitIdInLocIsUsedAsMarketLocationWithoutMapping()
    {
        StubImportReturns(HttpStatusCode.Accepted);

        await PostMessageAsync(new SyntheticMscons { MeteringPointId = MarketLocation1 }.ToString(), new Dictionary<string, string>());

        Uploads().ShouldHaveSingleItem().MarketLocationId.ShouldBe(MarketLocation1);
    }

    [Fact]
    public async Task UnmappedMeteringPointIsNotSent()
    {
        StubImportReturns(HttpStatusCode.Accepted);

        var status = await PostMessageAsync(new SyntheticMscons().ToString(), new Dictionary<string, string>());

        status.State.ShouldBe(MessageState.Processed);
        _meterDataService.LogEntries.ShouldBeEmpty();
    }

    [Fact]
    public async Task MeterDataServiceErrorDoesNotRejectTheMessage()
    {
        StubImportReturns(HttpStatusCode.ServiceUnavailable);

        var status = await PostMessageAsync(Sample("mscons_example.edi"), AllMapped);

        // The sender's message was fine; a downstream outage must not produce a negative APERAK.
        status.State.ShouldBe(MessageState.Processed);
        status.Errors.ShouldBeEmpty();
        // One failed upload does not stop the next one.
        _meterDataService.LogEntries.Count.ShouldBe(2);
    }

    [Fact]
    public async Task RejectedMessageIsNotSent()
    {
        StubImportReturns(HttpStatusCode.Accepted);
        _factory = NewFactory(AllMapped);
        using var client = _factory.CreateClient();

        using var response = await client.PostAsync(
            new Uri("/api/messages", UriKind.Relative),
            new StringContent(new SyntheticMscons { Quantities = [-1m] }.ToString(), Encoding.UTF8, "text/plain"));

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        _meterDataService.LogEntries.ShouldBeEmpty();
    }

    [Fact]
    public void AppSettingsBindBaseUrlAndDevelopmentMappings()
    {
        // The other tests replace the options, so this is the only check that the JSON files bind.
        using var factory = new WebApplicationFactory<Program>();

        var options = factory.Services.GetRequiredService<IOptions<MeterDataServiceOptions>>().Value;

        options.BaseUrl.ShouldBe(new Uri("http://localhost:5227"));
        options.MarketLocations[MeteringPoint1].ShouldBe(MarketLocation1);
        options.MarketLocations[MeteringPoint2].ShouldBe(MarketLocation2);
    }

    /// <summary>One <c>POST /api/import</c> as meter-data-service would receive it.</summary>
    private sealed record ImportUpload(string MarketLocationId, string FileName, string ContentType, string Csv)
    {
        public static ImportUpload Read(ILogEntry entry)
        {
            var request = entry.RequestMessage.ShouldNotBeNull();
            request.Path.ShouldBe("/api/import");
            var boundary = HeaderUtilities.RemoveQuotes(
                MediaTypeHeaderValue.Parse(request.Headers!["Content-Type"].ToString()).Boundary).Value!;

            var reader = new MultipartReader(boundary, new MemoryStream(request.BodyAsBytes!));
            string? marketLocationId = null, fileName = null, contentType = null, csv = null;
            while (reader.ReadNextSectionAsync().GetAwaiter().GetResult() is { } section)
            {
                var disposition = ContentDispositionHeaderValue.Parse(section.ContentDisposition);
                var text = new StreamReader(section.Body, Encoding.UTF8).ReadToEnd();
                switch (disposition.Name.Value)
                {
                    case "marketLocationId":
                        marketLocationId = text;
                        break;
                    case "file":
                        fileName = disposition.FileName.Value;
                        contentType = section.ContentType;
                        csv = text;
                        break;
                }
            }

            return new ImportUpload(
                marketLocationId.ShouldNotBeNull(), fileName.ShouldNotBeNull(), contentType.ShouldNotBeNull(), csv.ShouldNotBeNull());
        }
    }
}
