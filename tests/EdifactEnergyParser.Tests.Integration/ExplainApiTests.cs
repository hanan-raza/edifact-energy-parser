using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using EdifactEnergyParser.Api.Contracts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Shouldly;

namespace EdifactEnergyParser.Tests.Integration;

/// <summary>End to end for <c>POST /api/messages/explain</c>.</summary>
public sealed class ExplainApiTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    private static readonly Uri ExplainUri = new("/api/messages/explain", UriKind.Relative);

    // Wording the explainer uses when it has nothing specific to say about a value.
    private static readonly string[] VaguePhrases =
        ["not interpreted", "not in the explainer", "not described", "not recognised", "cannot be read"];

    private readonly HttpClient _client = factory.CreateClient();

    private Task<HttpResponseMessage> PostAsync(string body, string mediaType = "text/plain") =>
        _client.PostAsync(ExplainUri, new StringContent(body, Encoding.UTF8, mediaType));

    private static string Sample(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, name));

    private static async Task<IReadOnlyList<SegmentExplanationResponse>> ReadAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<List<SegmentExplanationResponse>>()).ShouldNotBeNull();

    private static bool IsVague(string text) =>
        string.IsNullOrWhiteSpace(text) || VaguePhrases.Any(p => text.Contains(p, StringComparison.OrdinalIgnoreCase));

    [Theory]
    [InlineData("mscons_example.edi", 48)]
    [InlineData("utilmd_example.edi", 28)]
    public async Task SampleIsFullyExplainedWithoutNullMeanings(string sample, int segmentCount)
    {
        using var response = await PostAsync(Sample(sample));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var segments = await ReadAsync(response);
        segments.Count.ShouldBe(segmentCount);
        segments.ShouldAllBe(s => !IsVague(s.Description) && s.Fields.Count > 0);
        segments.SelectMany(s => s.Fields).ShouldAllBe(f => f.Name != null && f.Value != null && !IsVague(f.Meaning));
    }

    [Fact]
    public async Task ResponseIsAJsonArrayWithSegmentDescriptionAndFields()
    {
        using var response = await PostAsync(Sample("mscons_example.edi"));

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        doc.RootElement.ValueKind.ShouldBe(JsonValueKind.Array);
        var unb = doc.RootElement.EnumerateArray().Single(e => e.GetProperty("segment").GetString() == "UNB");
        unb.GetProperty("description").GetString().ShouldStartWith("Interchange header");
        var first = unb.GetProperty("fields").EnumerateArray().First();
        first.GetProperty("name").GetString().ShouldBe("Syntax identifier");
        first.GetProperty("value").GetString().ShouldBe("UNOC");
        first.GetProperty("meaning").ValueKind.ShouldBe(JsonValueKind.String);
    }

    [Fact]
    public async Task UnbFieldsNameSenderAndRecipient()
    {
        using var response = await PostAsync(Sample("mscons_example.edi"));

        var unb = (await ReadAsync(response)).Single(s => s.Segment == "UNB");
        unb.Fields.Single(f => f.Name == "Sender identification").Value.ShouldBe("9900000000003");
        unb.Fields.Single(f => f.Name == "Recipient identification").Value.ShouldBe("9900000000010");
    }

    [Fact]
    public async Task RejectedMessageCanStillBeExplainedAndShowsTheMismatch()
    {
        // The same message is a 422 on POST /api/messages; the explainer shows why.
        using var response = await PostAsync(new SyntheticMscons { SegmentCountOffset = 1 }.ToString());

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var unt = (await ReadAsync(response)).Single(s => s.Segment == "UNT");
        unt.Fields[0].Meaning.ShouldBe("Segments in the message, UNH and UNT included: declared 24, counted 23");
    }

    [Fact]
    public async Task ExplainingStoresNothing()
    {
        using var response = await PostAsync(new SyntheticMscons().ToString());

        response.Headers.Location.ShouldBeNull();
        (await response.Content.ReadAsStringAsync()).ShouldNotContain("\"id\"");
    }

    [Fact]
    public async Task TextThatIsNotEdifactIs422WithPosition()
    {
        using var response = await PostAsync("hello world");

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        var problem = (await response.Content.ReadFromJsonAsync<ProblemDetails>()).ShouldNotBeNull();
        problem.Detail.ShouldNotBeNull().ShouldStartWith("Not readable as EDIFACT at line 1, column 1");
    }

    [Theory]
    [InlineData("")]
    [InlineData("  \n ")]
    public async Task EmptyBodyIs400(string body)
    {
        using var response = await PostAsync(body);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task JsonBodyIs415()
    {
        using var response = await PostAsync("{\"text\":\"UNB\"}", "application/json");

        response.StatusCode.ShouldBe(HttpStatusCode.UnsupportedMediaType);
    }
}
