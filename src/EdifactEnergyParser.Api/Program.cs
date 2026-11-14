using System.Text.Json.Serialization;
using EdifactEnergyParser.Api.Adapters;
using EdifactEnergyParser.Api.Formatters;
using EdifactEnergyParser.Workflow;

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
