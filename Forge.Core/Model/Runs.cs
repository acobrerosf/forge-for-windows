using System.Text.Json.Nodes;

namespace Forge.Core.Model;

public sealed record RunState(bool Running, string Status, bool Bad, string Header);

public sealed record FoundRun(string Id, JsonNode? Attributes, long MadeMs);

/// <summary>
/// Forge answers a command or recipe POST with 202 and no body, so the run is
/// recognised afterwards rather than received. Port of Model.js's commandFrom
/// and recipeRunFrom.
/// </summary>
public static class Runs
{
    /// <summary>The margin for comparing Forge's clock with this machine's.</summary>
    public const long ClockSkewMs = 120_000;

    /// <summary>Seconds between looks at a run: quick commands land in about ten, slow ones earn longer gaps.</summary>
    public static readonly IReadOnlyList<int> PollLadder = [3, 3, 5, 5, 8, 8, 13, 13, 21, 21];

    public const int RecipeFindPages = 3;

    public static bool CommandIsTerminal(string status) => status is "finished" or "timeout" or "failed";

    public static bool RecipeIsTerminal(string status) => status is "finished" or "failed";

    public static RunState CommandState(JsonNode? attributes)
    {
        var status = Json.Str(Json.Get(attributes, "status"));
        var parts = new List<string> { status == "" ? "unknown" : status };
        var duration = Json.Str(Json.Get(attributes, "duration"));
        if (duration != "") parts.Add(duration);
        var exit = Json.Get(attributes, "exit_code");
        if (Json.IsNumber(exit)) parts.Add("exit " + Json.Str(exit));
        var failure = ForgeText.PlainText(Json.Str(Json.Get(attributes, "error_output"))).Trim();
        if (failure != "") parts.Add(failure);
        return new RunState(!CommandIsTerminal(status), status, status is "failed" or "timeout", string.Join(" · ", parts));
    }

    public static RunState RecipeState(JsonNode? attributes)
    {
        var status = Json.Str(Json.Get(attributes, "status"));
        var parts = new List<string> { status == "" ? "unknown" : status };
        var started = Json.ParseIsoMs(Json.Str(Json.Get(attributes, "started_at")));
        var finished = Json.ParseIsoMs(Json.Str(Json.Get(attributes, "finished_at")));
        if (started is not null && finished is not null && finished >= started)
            parts.Add(Math.Max(1, (long)Math.Round((finished.Value - started.Value) / 1000.0)) + "s");
        return new RunState(!RecipeIsTerminal(status), status, status == "failed", string.Join(" · ", parts));
    }

    /// <summary>
    /// The newest row whose command matches exactly, created no earlier than
    /// the send (less the skew margin) and strictly after the last run this
    /// session adopted for the same command — so a retry never adopts the failure.
    /// </summary>
    public static FoundRun? CommandFrom(JsonNode? body, string sent, long sinceMs, long afterMs)
    {
        var floor = Math.Max(sinceMs - ClockSkewMs, afterMs + 1);
        foreach (var row in Json.Items(body, "data"))
        {
            var attrs = Json.Get(row, "attributes");
            if (Json.Str(Json.Get(attrs, "command")) != sent) continue;
            var made = Json.ParseIsoMs(Json.Str(Json.Get(attrs, "created_at")));
            if (made is null || made < floor) continue;
            return new FoundRun(Json.Str(Json.Get(row, "id")), attrs, made.Value);
        }
        return null;
    }

    /// <summary>
    /// The right server, an id above the last adopted run, and a start time
    /// not older than the send (a null start passes: the run is still queued).
    /// The highest id wins because the list order is not documented.
    /// </summary>
    public static FoundRun? RecipeRunFrom(JsonNode? body, string serverId, long sinceMs, long afterId)
    {
        FoundRun? best = null;
        long bestId = 0;
        var skewFloor = sinceMs - ClockSkewMs;
        foreach (var row in Json.Items(body, "data"))
        {
            var attrs = Json.Get(row, "attributes");
            if (Json.Str(Json.Get(attrs, "server_id")) != serverId) continue;
            if (!long.TryParse(Json.Str(Json.Get(row, "id")), out var id) || id <= afterId) continue;
            var startedAt = Json.Str(Json.Get(attrs, "started_at"));
            if (startedAt != "" && Json.ParseIsoMs(startedAt) is { } started && started < skewFloor) continue;
            if (best is null || id > bestId) { best = new FoundRun(id.ToString(), attrs, 0); bestId = id; }
        }
        return best;
    }
}
