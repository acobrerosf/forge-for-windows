using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Forge.Core.Model;

namespace Forge.Core.Api;

public interface IForgeClient
{
    Task<ForgeEnvelope> SendAsync(string method, string path, string? token, JsonNode? body, CancellationToken ct);
}

/// <summary>
/// The only thing that talks HTTP. The token is passed per call and never
/// stored here; the caller reads it from Credential Manager.
/// </summary>
public sealed class ForgeClient : IForgeClient, IDisposable
{
    public const string DefaultBaseUrl = "https://forge.laravel.com/api";

    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly TimeProvider _time;

    public ForgeClient(string? baseUrl = null, HttpMessageHandler? handler = null, TimeProvider? time = null)
    {
        _baseUrl = (baseUrl ?? Environment.GetEnvironmentVariable("FORGE_API") ?? DefaultBaseUrl).TrimEnd('/');
        _time = time ?? TimeProvider.System;
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.Timeout = TimeSpan.FromSeconds(15);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("ForgeForWindows/0.1");
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.api+json");
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
    }

    public async Task<ForgeEnvelope> SendAsync(string method, string path, string? token, JsonNode? body, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(token)) return ForgeEnvelope.Failure("No API token");

        using var request = new HttpRequestMessage(new HttpMethod(method), _baseUrl + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null)
            request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return ForgeEnvelope.Failure("Forge did not answer in time");
        }
        catch (HttpRequestException e)
        {
            return ForgeEnvelope.Failure(e.HttpRequestError switch
            {
                HttpRequestError.NameResolutionError => "Could not resolve forge.laravel.com — are you offline?",
                HttpRequestError.ConnectionError => "Could not connect to Forge",
                _ => e.Message,
            });
        }

        using (response)
        {
            var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            JsonNode? parsed = null;
            if (!string.IsNullOrWhiteSpace(text))
            {
                try { parsed = JsonNode.Parse(text); }
                catch (System.Text.Json.JsonException)
                {
                    if (response.IsSuccessStatusCode)
                        return ForgeEnvelope.Failure("Forge returned something that wasn't JSON") with { Status = (int)response.StatusCode };
                }
            }

            return new ForgeEnvelope
            {
                Ok = response.IsSuccessStatusCode,
                Status = (int)response.StatusCode,
                RateRemaining = IntHeader(response, "X-RateLimit-Remaining"),
                RateResetSec = ResetSeconds(response),
                Body = parsed,
            };
        }
    }

    private static int? IntHeader(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) &&
        int.TryParse(values.LastOrDefault(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null;

    /// <summary>
    /// Laravel answers a refusal with Retry-After (seconds) and
    /// X-RateLimit-Reset (a unix timestamp). Both fold into seconds-from-now
    /// so nothing downstream compares clocks.
    /// </summary>
    private int? ResetSeconds(HttpResponseMessage response)
    {
        if (response.Headers.RetryAfter is { } retry)
        {
            if (retry.Delta is { } delta) return (int)Math.Ceiling(delta.TotalSeconds);
            if (retry.Date is { } date) return Math.Max(0, (int)Math.Ceiling((date - _time.GetUtcNow()).TotalSeconds));
        }
        var reset = IntHeader(response, "X-RateLimit-Reset");
        if (reset is null) return null;
        return Math.Max(0, (int)(reset.Value - _time.GetUtcNow().ToUnixTimeSeconds()));
    }

    public void Dispose() => _http.Dispose();
}
