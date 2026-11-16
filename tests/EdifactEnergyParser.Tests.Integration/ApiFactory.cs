using EdifactEnergyParser.Workflow;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace EdifactEnergyParser.Tests.Integration;

/// <summary>
/// Hosts the API in memory. By default processed values go nowhere, so tests that are not about
/// forwarding never reach a real meter-data-service that happens to run on the developer machine.
/// </summary>
public sealed class ApiFactory : WebApplicationFactory<Program>
{
    /// <summary>Base URL of the (stubbed) meter-data-service; <c>null</c> turns forwarding off.</summary>
    public Uri? MeterDataService { get; init; }

    /// <summary>Metering point ID → MaLo-ID; replaces whatever the appsettings files configure.</summary>
    public IReadOnlyDictionary<string, string> MarketLocations { get; init; } = new Dictionary<string, string>();

    protected override void ConfigureWebHost(IWebHostBuilder builder) =>
        builder.ConfigureTestServices(services => services.PostConfigure<MeterDataServiceOptions>(options =>
        {
            options.BaseUrl = MeterDataService;

            // Replaced rather than merged, so the Development mappings do not leak into a test.
            options.MarketLocations.Clear();
            foreach (var (meteringPoint, marketLocation) in MarketLocations)
            {
                options.MarketLocations[meteringPoint] = marketLocation;
            }
        }));
}
