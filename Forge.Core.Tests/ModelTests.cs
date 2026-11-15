using System.Text.Json.Nodes;
using Forge.Core.Api;
using Forge.Core.Model;
using Forge.Core.Setup;

namespace Forge.Core.Tests;

internal static class Fixture
{
    public static JsonNode Load(string name) =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name)))!;
}

public class ParseTests
{
    [Fact]
    public void Servers_collapse_readiness_into_one_state()
    {
        var servers = Parse.Servers(Fixture.Load("servers.json"));
        Assert.Equal(3, servers.Count);
        Assert.Equal(ServerState.Ready, servers[0].State);
        Assert.Equal("101", servers[0].Id);
        Assert.Equal(ServerState.Unreachable, servers[1].State);
        Assert.Equal(2222, servers[1].SshPort);
        Assert.Equal(ServerState.Provisioning, servers[2].State);
    }

    [Fact]
    public void Unknown_connection_status_reads_as_healthy()
    {
        var attrs = JsonNode.Parse("""{"is_ready":true,"connection_status":"something-new"}""");
        Assert.Equal(ServerState.Ready, Parse.ServerStateOf(attrs));
    }

    [Fact]
    public void Sites_join_their_latest_deployment_and_drop_rows_without_a_server()
    {
        var sites = Parse.Sites(Fixture.Load("sites.json"));
        Assert.Equal(2, sites.Count);

        var zeta = sites.Single(s => s.Id == "1");
        Assert.Equal("101", zeta.ServerId);
        Assert.Equal("101:1", zeta.Key);
        Assert.Equal("finished", zeta.DeploymentStatus);
        Assert.Equal("9001", zeta.DeploymentId);
        Assert.Equal("abcdef1", zeta.CommitHash);
        Assert.Equal("Fix the thing", zeta.CommitMessage);
        Assert.Equal("2026-11-15T10:01:30Z", zeta.DeployedAt);
        Assert.True(zeta.Maintenance);

        // The live status beats the record: a running deploy has none yet.
        Assert.Equal("deploying", sites.Single(s => s.Id == "2").DeploymentStatus);
        Assert.Empty(sites.Single(s => s.Id == "2").Aliases);
    }

    [Fact]
    public void A_page_where_no_row_has_its_server_is_an_error_not_an_empty_org()
    {
        var body = JsonNode.Parse("""{"data":[{"id":1,"attributes":{"name":"x"}}]}""");
        var sites = Parse.Sites(body);
        Assert.True(Parse.SitesLinkageMissing(body, sites));
        Assert.False(Parse.SitesLinkageMissing(JsonNode.Parse("""{"data":[]}"""), []));
    }

    [Fact]
    public void Next_cursor_reads_meta_only()
    {
        Assert.Equal("abc", Parse.NextCursor(JsonNode.Parse("""{"meta":{"next_cursor":"abc"},"links":[]}""")));
        Assert.Equal("", Parse.NextCursor(JsonNode.Parse("""{"meta":{"next_cursor":null}}""")));
    }
}

public class StatusTests
{
    private static Site S(string status = "", bool parked = false, string flip = "") =>
        new() { Id = "1", ServerId = "1", DeploymentStatus = status, Maintenance = parked, MaintenanceStatus = flip };

    [Fact]
    public void Running_or_failed_deploys_outrank_maintenance()
    {
        Assert.Equal(Tone.Busy, Status.SiteTone(S("deploying", parked: true)));
        Assert.Equal(Tone.Bad, Status.SiteTone(S("failed", parked: true)));
        Assert.Equal(Tone.Warn, Status.SiteTone(S("finished", parked: true)));
        Assert.Equal(Tone.Busy, Status.SiteTone(S("finished", flip: "enabling")));
        Assert.Equal(Tone.Ok, Status.SiteTone(S("finished")));
    }

    [Fact]
    public void Only_the_deployments_label_is_timed()
    {
        Assert.False(Status.SiteStatus(S("finished", parked: true)).Timed);
        Assert.Equal("maintenance", Status.SiteStatus(S("finished", parked: true)).Label);
        Assert.True(Status.SiteStatus(S("failed-build")).Timed);
        Assert.Equal("build failed", Status.SiteStatus(S("failed-build")).Label);
        Assert.Equal("never deployed", Status.SiteStatus(S()).Label);
        Assert.False(Status.SiteStatus(S("deploying")).Timed);
        Assert.Equal("enabling…", Status.SiteStatus(S(flip: "enabling")).Label);
    }

    [Fact]
    public void Health_precedence_puts_broken_above_token_problems()
    {
        var down = new Server { Id = "1", State = ServerState.Unreachable };
        Assert.Equal(Health.Bad, Status.HealthOf([down], [], accountProblem: true, requestProblem: true));
        Assert.Equal(Health.Error, Status.HealthOf([], [new[] { S("deploying") }], false, true));
        Assert.Equal(Health.Busy, Status.HealthOf([], [new[] { S(parked: true), S("deploying") }], false, false));
        Assert.Equal(Health.Maintenance, Status.HealthOf([], [new[] { S(parked: true) }], false, false));
        Assert.Equal(Health.Ok, Status.HealthOf([], [], false, false));
        Assert.Equal(Badge.Warn, Status.BadgeFor(Health.Setup));
    }
}

public class TextTests
{
    [Theory]
    [InlineData("javascript:alert(1)", "")]
    [InlineData("https://user@evil.example", "")]
    [InlineData("ftp://example.com", "")]
    [InlineData("  https://Example.com/a b  ", "https://Example.com/a%20b")]
    [InlineData("HTTPS://example.com/it's", "https://example.com/it%27s")]
    [InlineData("https://example.com/?q=1&x=%zz", "https://example.com/?q=1&x=%25zz")]
    public void External_url_only_vouches_for_plain_http_addresses(string input, string expected) =>
        Assert.Equal(expected, ForgeText.ExternalUrl(input));

    [Fact]
    public void Ssh_command_adds_the_port_and_refuses_odd_addresses()
    {
        Assert.Equal("ssh forge@203.0.113.11 -p 2222", ForgeText.SshCommand(new Server { Id = "1", Ip = "203.0.113.11", SshPort = 2222 }));
        Assert.Equal("ssh forge@10.0.0.1", ForgeText.SshCommand(new Server { Id = "1", Ip = "10.0.0.1" }));
        Assert.Equal("", ForgeText.SshCommand(new Server { Id = "1", Ip = "1.2.3.4; rm -rf /" }));
    }

    [Fact]
    public void Log_text_strips_ansi_and_resolves_carriage_returns()
    {
        var lines = ForgeText.LogLines("\u001b[32mok\u001b[0m\r\n10%\r20%\r30%\nbell\u0007done\n");
        Assert.Equal(["ok", "30%", "belldone"], lines);
    }

    [Fact]
    public void Safe_file_name_keeps_only_name_characters()
    {
        Assert.Equal("etc-passwd", ForgeText.SafeFileName("../../etc/passwd"));
        Assert.Equal("site", ForgeText.SafeFileName("..."));
    }

    [Fact]
    public void Notify_text_strips_leading_hyphens_and_controls()
    {
        Assert.Equal("hint=x y", ForgeText.NotifyText(" --hint=x\ny"));
    }

    [Fact]
    public void Dashboard_url_drops_the_site_segment_for_a_server()
    {
        var server = new Server { Id = "101", Slug = "pale-wave" };
        Assert.Equal("https://forge.laravel.com/acme/pale-wave", ForgeText.DashboardUrl(ForgeText.DefaultDashboardTemplate, "acme", server, null));
        Assert.Equal("https://forge.laravel.com/acme/pale-wave/7", ForgeText.DashboardUrl(ForgeText.DefaultDashboardTemplate, "acme", server, "7"));
        Assert.Equal("", ForgeText.DashboardUrl(ForgeText.DefaultDashboardTemplate, "acme", new Server { Id = "1" }, null));
    }

    [Fact]
    public void Empty_site_log_sentinel_becomes_empty() =>
        Assert.Equal("", ForgeText.SiteLogContent("=== Empty log file ===\n"));
}

public class ActionTests
{
    [Fact]
    public void Server_type_decides_which_services_are_listed()
    {
        var lb = new Server { Id = "1", Kind = "loadbalancer", State = ServerState.Ready };
        var ids = Actions.ServerActions("acme", lb).Select(a => a.Id).ToList();
        Assert.Contains("nginx-restart", ids);
        Assert.DoesNotContain("php-reload", ids);
        Assert.DoesNotContain("database-restart", ids);
        Assert.Contains("reboot", ids);
    }

    [Fact]
    public void Database_row_is_named_for_what_runs()
    {
        var app = new Server { Id = "9", Kind = "app", DatabaseType = "mariadb1011", PhpVersion = "php84", State = ServerState.Ready };
        var db = Actions.ServerActions("acme", app).Single(a => a.Id == "database-restart");
        Assert.Equal("Restart MariaDB", db.Label);
        Assert.Equal("/orgs/acme/servers/9/services/mysql/actions", db.Path);
        var php = Actions.ServerActions("acme", app).Single(a => a.Id == "php-reload");
        Assert.Equal("""{"action":"reload","version":"php84"}""", php.Body!.ToJsonString());
    }

    [Fact]
    public void Unreachable_server_can_still_be_rebooted_but_not_restarted()
    {
        var down = new Server { Id = "1", Kind = "app", State = ServerState.Unreachable };
        var actions = Actions.ServerActions("acme", down);
        Assert.True(actions.Single(a => a.Id == "reboot").Available);
        Assert.False(actions.Single(a => a.Id == "nginx-restart").Available);
        Assert.Equal("unreachable", actions.Single(a => a.Id == "nginx-restart").Reason);
    }

    [Theory]
    [InlineData("PHP 8.4", "php84")]
    [InlineData("php83", "php83")]
    [InlineData("8.4", "")]
    public void Site_php_pool_turns_the_display_string_into_the_enum_word(string version, string expected) =>
        Assert.Equal(expected, Actions.SitePhpPool(new Site { Id = "1", ServerId = "1", PhpVersion = version }));

    [Fact]
    public void Maintenance_toggle_flips_method_and_intent()
    {
        var parked = new Site { Id = "5", ServerId = "9", Maintenance = true };
        var off = Actions.SiteActions("acme", parked).Single(a => a.Id == "maintenance");
        Assert.Equal("DELETE", off.Method);
        Assert.Equal("off", off.ArmIntent);
        Assert.Null(off.Body);
        var on = Actions.SiteActions("acme", parked with { Maintenance = false }).Single(a => a.Id == "maintenance");
        Assert.Equal("POST", on.Method);
        Assert.Equal("""{"status":503}""", on.Body!.ToJsonString());
    }

    [Fact]
    public void Command_is_trimmed_once()
    {
        var spec = Actions.SiteCommand("acme", new Site { Id = "1", ServerId = "2" }, "  php artisan migrate  ");
        Assert.Equal("php artisan migrate", spec.ArmIntent);
        Assert.False(Actions.SiteCommand("acme", new Site { Id = "1", ServerId = "2" }, "  ").Available);
    }
}

public class GroupingTests
{
    private static readonly Server[] Servers = [new() { Id = "1" }, new() { Id = "2" }];
    private static Site S(string id, string server, string name) => new() { Id = id, ServerId = server, Name = name };

    [Fact]
    public void Merge_keeps_sites_the_window_did_not_reach()
    {
        var previous = SiteGrouping.Group([S("a", "1", "a"), S("b", "1", "b")], Servers);
        var merged = SiteGrouping.Merge(previous, [S("c", "1", "c")], Servers);
        Assert.Equal(["a", "b", "c"], merged["1"].Select(s => s.Id));
        Assert.Empty(merged["2"]);
    }

    [Fact]
    public void Group_is_the_only_publish_that_deletes_and_sorts_by_name()
    {
        var grouped = SiteGrouping.Group([S("z", "1", "Zeta"), S("a", "1", "admin"), S("x", "9", "lost")], Servers);
        Assert.Equal(["admin", "Zeta"], grouped["1"].Select(s => s.Name));
        Assert.False(grouped.ContainsKey("9"));
    }

    [Fact]
    public void Replace_touches_only_its_server()
    {
        var previous = SiteGrouping.Group([S("a", "1", "a"), S("b", "2", "b")], Servers);
        var replaced = SiteGrouping.Replace(previous, "1", [], Servers);
        Assert.Empty(replaced["1"]);
        Assert.Single(replaced["2"]);
    }
}

public class RateBudgetTests
{
    [Fact]
    public void Ceiling_stops_discretionary_requests_but_not_forced_ones()
    {
        var budget = new RateBudget { Ceiling = 2 };
        Assert.True(budget.TryCharge("b", 0));
        Assert.True(budget.TryCharge("b", 1));
        Assert.False(budget.TryCharge("b", 2));
        Assert.True(budget.TryCharge("b", 3, force: true));
        Assert.Equal(0, budget.Headroom("b", 4));
        Assert.Equal(2, budget.Headroom("b", 60_004));
    }

    [Fact]
    public void A_hold_refuses_even_forced_requests_until_it_ends()
    {
        var budget = new RateBudget();
        budget.Hold("b", 10_000);
        Assert.False(budget.TryCharge("b", 5_000, force: true));
        Assert.True(budget.TryCharge("b", 10_000));
    }

    [Fact]
    public void Backoff_falls_back_and_is_capped()
    {
        Assert.Equal(60_000, new ForgeEnvelope { Status = 429 }.BackoffUntilMs(0));
        Assert.Equal(300_000, new ForgeEnvelope { Status = 429, RateResetSec = 9999 }.BackoffUntilMs(0));
        Assert.Equal(5_000, new ForgeEnvelope { Status = 429, RateResetSec = 5 }.BackoffUntilMs(0));
    }
}

public class RunTests
{
    private static JsonNode List(params (string Id, string Command, string Created)[] rows) =>
        new JsonObject
        {
            ["data"] = new JsonArray(rows.Select(r => (JsonNode)new JsonObject
            {
                ["id"] = r.Id,
                ["attributes"] = new JsonObject { ["command"] = r.Command, ["created_at"] = r.Created },
            }).ToArray()),
        };

    [Fact]
    public void Command_is_recognised_by_exact_text_and_time()
    {
        var sent = DateTimeOffset.Parse("2026-11-15T12:00:00Z").ToUnixTimeMilliseconds();
        var body = List(("3", "ls", "2026-11-15T12:00:05Z"), ("2", "ls ", "2026-11-15T12:00:04Z"), ("1", "ls", "2026-11-14T12:00:00Z"));
        var found = Runs.CommandFrom(body, "ls", sent, 0);
        Assert.Equal("3", found!.Id);
        // A retry must not adopt the run it is retrying.
        Assert.Null(Runs.CommandFrom(body, "ls", sent, found.MadeMs));
    }

    [Fact]
    public void Recipe_run_takes_the_highest_matching_id()
    {
        var body = JsonNode.Parse("""
            {"data":[
              {"id":"7","attributes":{"server_id":101,"started_at":null}},
              {"id":"9","attributes":{"server_id":101,"started_at":null}},
              {"id":"12","attributes":{"server_id":555,"started_at":null}}
            ]}
            """);
        Assert.Equal("9", Runs.RecipeRunFrom(body, "101", 0, 0)!.Id);
        Assert.Null(Runs.RecipeRunFrom(body, "101", 0, 9));
    }

    [Fact]
    public void Command_state_leads_with_the_status_word()
    {
        var state = Runs.CommandState(JsonNode.Parse("""{"status":"failed","duration":"2s","exit_code":1}"""));
        Assert.False(state.Running);
        Assert.True(state.Bad);
        Assert.Equal("failed · 2s · exit 1", state.Header);
    }
}

public class SetupTests
{
    [Fact]
    public void Round_trips_and_tolerates_junk()
    {
        var state = SetupState.Empty.WithAccount("Work", new ForgeUser("me@example.com", "Me"),
            [new OrgEntry("acme", "", "Acme Inc")], out var name);
        Assert.Equal("work", name);
        var back = SetupState.FromJson(state.ToJson());
        Assert.Equal("acme", back.DefaultOrganization);
        Assert.Equal("work", back.AccountForOrg("acme"));
        Assert.Equal("user:me@example.com", back.BudgetBucket("work"));
        Assert.Equal(60, back.Settings.RefreshIntervalSec);

        Assert.Same(SetupState.Empty, SetupState.FromJson("not json"));
        Assert.Equal(30, SetupState.FromJson("""{"settings":{"refreshIntervalSec":1}}""").Settings.RefreshIntervalSec);
    }

    [Fact]
    public void A_second_token_for_the_same_user_updates_that_account()
    {
        var user = new ForgeUser("me@example.com", "Me");
        var first = SetupState.Empty.WithAccount("work", user, [new OrgEntry("acme", "", "")], out _);
        var second = first.WithAccount(null, user, [new OrgEntry("acme", "", ""), new OrgEntry("beta", "", "")], out var name);
        Assert.Equal("work", name);
        Assert.Single(second.Accounts);
        Assert.Equal(["acme", "beta"], second.Organizations.Select(o => o.Slug));
    }

    [Fact]
    public void Removing_an_account_drops_its_organizations()
    {
        var a = SetupState.Empty
            .WithAccount("one", new ForgeUser("a@x", "A"), [new OrgEntry("acme", "", "")], out _)
            .WithAccount("two", new ForgeUser("b@x", "B"), [new OrgEntry("beta", "", "")], out _);
        var b = a.WithoutAccount("one");
        Assert.Equal(["beta"], b.Organizations.Select(o => o.Slug));
        Assert.Equal("beta", b.DefaultOrganization);
    }
}

public class CredentialTests
{
    [Fact]
    public void Blob_decodes_both_utf8_and_the_utf16_cmdkey_writes()
    {
        Assert.Equal("abc123", Forge.Core.Secrets.WindowsCredentialStore.DecodeBlob(System.Text.Encoding.UTF8.GetBytes("abc123")));
        Assert.Equal("abc123", Forge.Core.Secrets.WindowsCredentialStore.DecodeBlob(System.Text.Encoding.Unicode.GetBytes("abc123")));
    }
}

public class WindowsCredentialStoreTests
{
    [Fact]
    public void Long_tokens_round_trip_across_parts_and_leave_nothing_behind()
    {
        if (!OperatingSystem.IsWindows()) return;
        var store = new Forge.Core.Secrets.WindowsCredentialStore();
        var account = "test-" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            // Over the 2,560-byte limit of one credential, the way a many-scoped Forge JWT is.
            var longToken = string.Concat(Enumerable.Range(0, 700).Select(i => (char)('a' + i % 26))) + new string('x', 5000);
            store.Write(account, longToken);
            Assert.Equal(longToken, store.Read(account));

            // A shorter token over it must not pick up the old parts.
            store.Write(account, "short-token");
            Assert.Equal("short-token", store.Read(account));

            store.Write(account, longToken);
            store.Delete(account);
            Assert.Null(store.Read(account));
            Assert.Null(new Forge.Core.Secrets.WindowsCredentialStore().Read(account + "#1"));
        }
        finally
        {
            store.Delete(account);
        }
    }

    [Theory]
    [InlineData("Laravel Forge API token (Forge for Windows), part 1 of 3", 3)]
    [InlineData("Laravel Forge API token (Forge for Windows)", 1)]
    [InlineData(null, 1)]
    [InlineData("part 1 of 999", 1)]
    public void Part_count_is_read_from_the_first_comment(string? comment, int expected) =>
        Assert.Equal(expected, Forge.Core.Secrets.WindowsCredentialStore.PartsFromComment(comment));
}

public class ProfileTests
{
    [Theory]
    [InlineData("dev", "dev")]
    [InlineData(" Dev ", "dev")]
    [InlineData("default", "")]
    [InlineData("DEFAULT", "")]
    [InlineData("", "")]
    [InlineData(null, "")]
    [InlineData("my test/../x", "mytestx")]
    [InlineData("qa-2", "qa-2")]
    public void Profile_names_are_safe_for_folders_and_credentials(string? requested, string expected) =>
        Assert.Equal(expected, AppProfile.Clean(requested));
}
