using System.Text.Json.Serialization;
using EdifactEnergyParser.Api.Adapters;
using EdifactEnergyParser.Api.Formatters;
using EdifactEnergyParser.Workflow;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

builder.Services
    .AddControllers(options => options.InputFormatters.Insert(0, new PlainTextInputFormatter()))
    // States as "Rejected" rather than 3: clients should not depend on enum order.
    .AddJsonOptions(options => options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();

builder.Services.AddSingleton(TimeProvider.System);
// Singleton because the in-memory store is the only copy of the messages.
builder.Services.AddSingleton<IMessageStore, InMemoryMessageStore>();
builder.Services.AddSingleton<MessageWorkflow>();
builder.Services.AddSingleton<MsconsAdapter>();
builder.Services.AddSingleton<ExplainerAdapter>();

builder.Services.Configure<MeterDataServiceOptions>(builder.Configuration.GetSection(MeterDataServiceOptions.SectionName));
builder.Services.AddHttpClient<MeterDataServiceClient>((services, http) =>
{
    if (services.GetRequiredService<IOptions<MeterDataServiceOptions>>().Value.BaseUrl is { } baseUrl)
    {
        // Without the trailing slash a base path such as "/mds" would be dropped when "api/import" is appended.
        http.BaseAddress = baseUrl.AbsoluteUri.EndsWith('/') ? baseUrl : new Uri(baseUrl.AbsoluteUri + "/");
    }

    // The import endpoint only queues the file, so anything slower than this means the service is in trouble.
    http.Timeout = TimeSpan.FromSeconds(30);
});
// Scoped, not singleton: it holds a typed HttpClient, whose handler the factory must be free to recycle.
builder.Services.AddScoped<MeterDataForwarder>();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();
app.MapControllers();

app.Run();

/// <summary>Entry point; public so that integration tests can host the API with WebApplicationFactory.</summary>
public partial class Program;
