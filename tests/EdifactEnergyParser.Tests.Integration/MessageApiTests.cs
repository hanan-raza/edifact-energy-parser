using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using EdifactEnergyParser.Api.Contracts;
using EdifactEnergyParser.Workflow;
using Microsoft.AspNetCore.Mvc.Testing;
using Shouldly;

namespace EdifactEnergyParser.Tests.Integration;

/// <summary>End to end through HTTP: formatter, controller, F# adapter, workflow and store.</summary>
public sealed class MessageApiTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly HttpClient _client = factory.CreateClient();

    private Task<HttpResponseMessage> PostAsync(string body, string mediaType = "text/plain", Encoding? encoding = null) =>
        _client.PostAsync(new Uri("/api/messages", UriKind.Relative), new StringContent(body, encoding ?? Encoding.UTF8, mediaType));

    private static async Task<MessageStatusResponse> ReadStatusAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<MessageStatusResponse>(Json)).ShouldNotBeNull();

    [Fact]
    public async Task ValidMsconsIsValidatedAndCanBeFetchedAgain()
    {
        using var response = await PostAsync(new SyntheticMscons().ToString());

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var posted = await ReadStatusAsync(response);
        posted.State.ShouldBe(MessageState.Validated);
        posted.Errors.ShouldBeEmpty();
        posted.History.Select(h => h.To).ShouldBe([MessageState.Received, MessageState.Validated]);

        using var get = await _client.GetAsync(new Uri($"/api/messages/{posted.Id}", UriKind.Relative));

        get.StatusCode.ShouldBe(HttpStatusCode.OK);
        var fetched = await ReadStatusAsync(get);
        fetched.Id.ShouldBe(posted.Id);
        fetched.State.ShouldBe(MessageState.Validated);
    }

    [Fact]
    public async Task StatesAreSerializedAsNames()
    {
        using var response = await PostAsync(new SyntheticMscons().ToString());

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("state").GetString().ShouldBe("Validated");
    }

    [Fact]
    public async Task InvalidMsconsIsRejectedWith422AndEveryError()
    {
        using var response = await PostAsync(new SyntheticMscons { RecipientId = "ABC", Quantities = [-1m] }.ToString());

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        var rejected = await ReadStatusAsync(response);
        rejected.State.ShouldBe(MessageState.Rejected);
        rejected.Errors.Select(e => e.Segment).ShouldBe(["NAD", "QTY"], ignoreOrder: true);

        // The rejection is stored, not just returned.
        using var get = await _client.GetAsync(new Uri($"/api/messages/{rejected.Id}", UriKind.Relative));
        var fetched = await ReadStatusAsync(get);
        fetched.State.ShouldBe(MessageState.Rejected);
        fetched.Errors.Count.ShouldBe(2);
    }

    [Fact]
    public async Task BrokenEnvelopeIsRejectedWith422()
    {
        using var response = await PostAsync(new SyntheticMscons { SegmentCountOffset = -1 }.ToString());

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        var error = (await ReadStatusAsync(response)).Errors.ShouldHaveSingleItem();
        error.Segment.ShouldBe("UNT");
    }

    [Fact]
    public async Task TextThatIsNotEdifactIsRejectedWith422()
    {
        using var response = await PostAsync("hello world");

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await ReadStatusAsync(response)).Errors.ShouldHaveSingleItem().Message.ShouldStartWith("Syntax error");
    }

    [Fact]
    public async Task Latin1BodyIsReadWithItsDeclaredCharset()
    {
        // "ü" is one byte (0xFC) in Latin-1 and invalid on its own in UTF-8. RFF+MG is a free-text
        // reference, so the character survives parsing and the message must still be accepted.
        var text = new SyntheticMscons().ToString().Replace("LIN+1'", "RFF+MG:Zähler-Müller'\nLIN+1'", StringComparison.Ordinal)
            .Replace("UNT+23+1'", "UNT+24+1'", StringComparison.Ordinal);

        using var response = await PostAsync(text, encoding: Encoding.Latin1);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Latin1BodyWithoutCharsetIsA400WithAHint()
    {
        using var content = new ByteArrayContent(Encoding.Latin1.GetBytes("UNB+Müller"));
        content.Headers.ContentType = new("text/plain");

        using var response = await _client.PostAsync(new Uri("/api/messages", UriKind.Relative), content);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).ShouldContain("charset=iso-8859-1");
    }

    [Theory]
    [InlineData("")]
    [InlineData("  \n ")]
    public async Task EmptyBodyIsA400(string body)
    {
        using var response = await PostAsync(body);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task JsonBodyIsUnsupportedMediaType()
    {
        using var response = await PostAsync("{\"content\":\"UNB\"}", "application/json");

        response.StatusCode.ShouldBe(HttpStatusCode.UnsupportedMediaType);
    }

    [Fact]
    public async Task UnknownIdIsA404ProblemDetails()
    {
        using var response = await _client.GetAsync(new Uri($"/api/messages/{Guid.NewGuid()}", UriKind.Relative));

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");
    }

    [Fact]
    public async Task IdThatIsNotAGuidIsA404()
    {
        using var response = await _client.GetAsync(new Uri("/api/messages/not-a-guid", UriKind.Relative));

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }
}
