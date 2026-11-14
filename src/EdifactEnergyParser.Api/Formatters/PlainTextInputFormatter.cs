using System.Text;
using Microsoft.AspNetCore.Mvc.Formatters;
using Microsoft.Net.Http.Headers;

namespace EdifactEnergyParser.Api.Formatters;

/// <summary>Binds a <c>text/plain</c> request body to a <see cref="string"/> parameter.</summary>
/// <remarks>
/// EDIFACT files are sent as they are, not wrapped in JSON. UTF-8 is the default; ISO 8859-1 is
/// accepted too because syntax identifier UNOC (the usual one in the German market) is Latin-1,
/// and a client that sends the file's bytes unchanged declares it with <c>charset=iso-8859-1</c>.
/// </remarks>
public sealed class PlainTextInputFormatter : TextInputFormatter
{
    /// <summary>Creates the formatter for <c>text/plain</c> in UTF-8 and Latin-1.</summary>
    public PlainTextInputFormatter()
    {
        SupportedMediaTypes.Add(MediaTypeHeaderValue.Parse("text/plain"));
        SupportedEncodings.Add(new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true));
        SupportedEncodings.Add(Encoding.Latin1);
    }

    /// <inheritdoc />
    protected override bool CanReadType(Type type) => type == typeof(string);

    /// <inheritdoc />
    public override async Task<InputFormatterResult> ReadRequestBodyAsync(InputFormatterContext context, Encoding encoding)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(encoding);

        using var reader = new StreamReader(context.HttpContext.Request.Body, encoding);
        try
        {
            var content = await reader.ReadToEndAsync(context.HttpContext.RequestAborted);
            return await InputFormatterResult.SuccessAsync(content);
        }
        catch (DecoderFallbackException)
        {
            // Bytes that are not valid UTF-8: most likely a Latin-1 file sent without a charset.
            context.ModelState.TryAddModelError(context.ModelName,
                "The body is not valid UTF-8. Send ISO 8859-1 content with 'Content-Type: text/plain; charset=iso-8859-1'.");
            return await InputFormatterResult.FailureAsync();
        }
    }
}
