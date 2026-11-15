using System.Text.Json.Nodes;
using Forge.Core.Model;

namespace Forge.Core.Api;

/// <summary>
/// Every request answers with one of these, whatever failed — transport,
/// HTTP or success arrive through the same door.
/// </summary>
public sealed record ForgeEnvelope
{
    public bool Ok { get; init; }
    public int Status { get; init; }
    public int? RateRemaining { get; init; }
    /// <summary>Seconds from now until the rate limit resets, folded from Retry-After and X-RateLimit-Reset.</summary>
    public int? RateResetSec { get; init; }
    public JsonNode? Body { get; init; }
    public string? Error { get; init; }
    /// <summary>The request never left the machine because there was no token: "not set up", not "failed".</summary>
    public bool MissingToken { get; init; }
    /// <summary>The request was not sent because the bucket is held or at its ceiling.</summary>
    public bool Throttled { get; init; }

    public static ForgeEnvelope Failure(string message) => new() { Ok = false, Status = 0, Error = message };

    public static ForgeEnvelope Throttle(string message) => new() { Ok = false, Status = 0, Error = message, Throttled = true };

    public static ForgeEnvelope NoToken(string account) => new()
    {
        Ok = false, Status = 0, MissingToken = true,
        Error = $"No API token for account '{account}'. Open Settings to add one.",
    };

    /// <summary>Status before the body's message: Forge words a 429 as "Too Many Attempts.", which says nothing useful.</summary>
    public string ErrorMessage
    {
        get
        {
            if (!string.IsNullOrEmpty(Error)) return Error;
            switch (Status)
            {
                case 401: return "Token rejected";
                case 403: return "Token is missing a scope for this";
                case 404: return "Not found";
                case 429: return "Rate limited — backing off";
            }
            var message = Json.Str(Json.Get(Body, "message"));
            return message != "" ? message : "HTTP " + Status;
        }
    }

    public const int BackoffFallbackSec = 60;
    public const int BackoffCeilingSec = 300;

    /// <summary>A 429 without a reset still has to wait; the ceiling keeps a nonsense header from parking the app.</summary>
    public long BackoffUntilMs(long nowMs)
    {
        var seconds = RateResetSec is > 0 ? RateResetSec.Value : BackoffFallbackSec;
        return nowMs + Math.Min(seconds, BackoffCeilingSec) * 1000L;
    }
}
