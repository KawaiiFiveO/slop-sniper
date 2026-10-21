using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;
using SlopSniper.Options;

namespace SlopSniper.Provenance;

/// <summary>Calls OpenAI's content provenance API. Every failure is thrown as a <see cref="ProvenanceException"/>.</summary>
public sealed class ProvenanceClient(IHttpClientFactory httpClientFactory, IOptionsMonitor<BotOptions> options)
{
    public const string HttpClientName = "openai";

    private static readonly Uri Endpoint = new("https://api.openai.com/v1/content_provenance_checks");

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
    };

    /// <param name="mediaType">image/png, image/jpeg or image/webp.</param>
    public async Task<ProvenanceCheck> CheckAsync(byte[] image, string mediaType, CancellationToken cancellationToken)
    {
        var file = new ByteArrayContent(image);
        file.Headers.ContentType = new MediaTypeHeaderValue(mediaType);

        // A generic file name, so the original one isn't sent to OpenAI.
        using var form = new MultipartFormDataContent();
        form.Add(file, "file", "image." + mediaType["image/".Length..]);

        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint) { Content = form };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.CurrentValue.OpenAiApiKey);

        HttpResponseMessage response;
        try
        {
            response = await httpClientFactory.CreateClient(HttpClientName).SendAsync(request, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            throw new ProvenanceException(ProvenanceFailure.Transient, $"Couldn't reach OpenAI: {ex.Message}", innerException: ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ProvenanceException(ProvenanceFailure.Transient, "OpenAI didn't respond in time.", innerException: ex);
        }

        using (response)
        {
            if (response.IsSuccessStatusCode)
            {
                try
                {
                    return await response.Content.ReadFromJsonAsync<ProvenanceCheck>(JsonOptions, cancellationToken)
                        ?? throw new JsonException("The response was empty.");
                }
                catch (JsonException ex)
                {
                    throw new ProvenanceException(ProvenanceFailure.Transient, "OpenAI returned an unexpected response.", innerException: ex);
                }
            }

            var (code, detail) = await ReadErrorAsync(response, cancellationToken);
            var message = $"OpenAI returned {(int)response.StatusCode}: {detail}";
            throw response.StatusCode switch
            {
                HttpStatusCode.TooManyRequests when code == "insufficient_quota"
                    => new ProvenanceException(ProvenanceFailure.QuotaExceeded, message),
                HttpStatusCode.TooManyRequests
                    => new ProvenanceException(ProvenanceFailure.RateLimited, message, GetRetryAfter(response)),
                HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound
                    => new ProvenanceException(ProvenanceFailure.AccessDenied, message),
                HttpStatusCode.RequestTimeout or >= HttpStatusCode.InternalServerError
                    => new ProvenanceException(ProvenanceFailure.Transient, message),
                _ => new ProvenanceException(ProvenanceFailure.BadFile, message),
            };
        }
    }

    /// <summary>Reads <c>{"error": {"message", "type", "code"}}</c>, tolerating any other body.</summary>
    private static async Task<(string? Code, string Detail)> ReadErrorAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        try
        {
            using var json = JsonDocument.Parse(body);
            if (json.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
            {
                var code = GetString(error, "code") ?? GetString(error, "type");
                return (code, GetString(error, "message") ?? code ?? response.ReasonPhrase ?? "unknown error");
            }
        }
        catch (JsonException)
        {
            // Not JSON (e.g. an error page from a proxy); fall back to the status text.
        }

        return (null, response.ReasonPhrase ?? "unknown error");
    }

    private static string? GetString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static TimeSpan? GetRetryAfter(HttpResponseMessage response)
    {
        // OpenAI can send a millisecond-precision header alongside the standard one.
        if (response.Headers.TryGetValues("retry-after-ms", out var values)
            && double.TryParse(values.FirstOrDefault(), NumberStyles.Float, CultureInfo.InvariantCulture, out var ms))
        {
            return TimeSpan.FromMilliseconds(ms);
        }

        var retryAfter = response.Headers.RetryAfter;
        if (retryAfter?.Delta is { } delta)
        {
            return delta;
        }
        if (retryAfter?.Date is { } date)
        {
            var wait = date - DateTimeOffset.UtcNow;
            return wait > TimeSpan.Zero ? wait : TimeSpan.Zero;
        }
        return null;
    }
}
