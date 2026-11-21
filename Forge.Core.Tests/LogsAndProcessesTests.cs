using Forge.Core.Api;
using Forge.Core.Model;
using Forge.Core.Secrets;
using Forge.Core.Service;
using Forge.Core.Setup;

namespace Forge.Core.Tests;

public class LogsAndProcessesParseTests
{
    [Fact]
    public void Deployments_keep_forge_order_and_shorten_the_commit()
    {
        var list = Parse.Deployments(Fixture.Load("deployments.json"));
        Assert.Equal(["9003", "9002", "9001"], list.Select(d => d.Id));
        var failed = list[0];
        Assert.Equal("failed", failed.Status);
        Assert.Equal("push", failed.Type);
        Assert.Equal("0123456", failed.CommitHash);
        Assert.Equal("Break the build", failed.CommitMessage);
        Assert.Equal("Dana", failed.CommitAuthor);
        Assert.Equal("0123456 · Dana · push", Status.DeploymentDetail(failed));
        Assert.Equal("2026-11-20T09:00:40Z", Status.DeploymentTime(failed));
        Assert.Equal("2026-11-19T09:00:00Z", Status.DeploymentTime(list[1]));
        Assert.Equal("2026-11-18T08:00:00Z", Status.DeploymentTime(list[2]));
        Assert.Equal("", list[2].CommitHash);
    }

    [Fact]
    public void Background_processes_put_the_broken_ones_first()
    {
        var list = Parse.BackgroundProcesses(Fixture.Load("background-processes.json"));
        Assert.Equal(["12", "13", "14", "11"], list.Select(p => p.Id));
        Assert.Equal(("fatal", Tone.Bad), Status.ProcessStatus(list[0]));
        Assert.Equal(("stopped", Tone.Warn), Status.ProcessStatus(list[1]));
        Assert.Equal(("restarting", Tone.Busy), Status.ProcessStatus(list[2]));
        Assert.Equal(("running", Tone.Ok), Status.ProcessStatus(list[3]));
        Assert.Equal("as forge · 2 processes · /home/forge/zeta", Status.ProcessDetail(list[3]));
        Assert.Equal("as forge", Status.ProcessDetail(list[0]));
    }

    [Fact]
    public void Scheduled_jobs_are_named_by_name_or_command()
    {
        var list = Parse.ScheduledJobs(Fixture.Load("scheduled-jobs.json"));
        Assert.Equal(["22", "21"], list.Select(j => j.Id));
        Assert.Equal("Backups", Status.ScheduledJobTitle(list[0]));
        Assert.Equal("15 3 * * * · as root · /usr/local/bin/backup", Status.ScheduledJobDetail(list[0]));
        Assert.Equal(("failed", Tone.Bad), Status.ScheduledJobStatus(list[0]));
        Assert.Equal("php /home/forge/zeta/artisan schedule:run", Status.ScheduledJobTitle(list[1]));
        Assert.Equal("minutely · as forge", Status.ScheduledJobDetail(list[1]));
        Assert.Equal(("", Tone.Idle), Status.ScheduledJobStatus(list[1]));
    }

    [Fact]
    public void A_jobs_next_run_reads_forward()
    {
        var now = Json.ParseIsoMs("2026-11-21T10:00:00Z")!.Value;
        ScheduledJob At(string iso) => new() { Id = "1", NextRunTime = iso };
        Assert.Equal("next in 17h", Status.ScheduledJobNext(At("2026-11-22T03:15:00Z"), now));
        Assert.Equal("next in 5m", Status.ScheduledJobNext(At("2026-11-21T10:05:00Z"), now));
        Assert.Equal("next in 2d", Status.ScheduledJobNext(At("2026-11-23T10:00:00Z"), now));
        Assert.Equal("due now", Status.ScheduledJobNext(At("2026-11-21T09:00:00Z"), now));
        Assert.Equal("", Status.ScheduledJobNext(At(""), now));
    }

    [Fact]
    public void Paths_encode_every_segment()
    {
        Assert.Equal("/orgs/acme/servers/1/sites/2/deployments?sort=-created_at&page%5Bsize%5D=30&page%5Bcursor%5D=a%2Fb",
            Paths.Deployments("acme", "1", "2", "a/b"));
        Assert.Equal("/orgs/acme/servers/1/logs/php-8.4", Paths.ServerLog("acme", "1", "php-8.4"));
        Assert.Equal("/orgs/acme/servers/1/background-processes/x%2Fy/log", Paths.BackgroundProcessLog("acme", "1", "x/y"));
        Assert.Equal("/orgs/acme/servers/1/sites/2/scheduled-jobs/3/output", Paths.SiteScheduledJobOutput("acme", "1", "2", "3"));
        Assert.Equal("/orgs/acme/servers/1/scheduled-jobs?page%5Bsize%5D=30", Paths.ScheduledJobs("acme", "1"));
    }
}

public class LogsAndProcessesActionTests
{
    [Fact]
    public void Server_logs_are_only_the_ones_the_server_can_have()
    {
        var app = new Server { Id = "1", Kind = "app", PhpVersion = "php84", DatabaseType = "mariadb1011", State = ServerState.Ready };
        Assert.Equal(["nginx-error", "nginx-access", "php-8.4", "database-mariadb", "redis-server", "ssh", "unattended-upgrades"],
            Actions.ServerLogKinds(app).Select(k => k.Key));

        var lb = new Server { Id = "2", Kind = "loadbalancer", PhpVersion = "php84", DatabaseType = "mysql8" };
        Assert.Equal(["nginx-error", "nginx-access", "ssh", "unattended-upgrades"], Actions.ServerLogKinds(lb).Select(k => k.Key));

        var db = new Server { Id = "3", Kind = "database", DatabaseType = "postgres17" };
        Assert.Contains("database-postgresql", Actions.ServerLogKinds(db).Select(k => k.Key));

        var search = new Server { Id = "4", Kind = "meilisearch" };
        Assert.Contains("meilisearch", Actions.ServerLogKinds(search).Select(k => k.Key));

        Assert.Empty(Actions.ServerLogKinds(null));
    }

    [Theory]
    [InlineData("php84", "php-8.4")]
    [InlineData("php56", "php-5.6")]
    [InlineData("php80-old", "php-8.0")]
    [InlineData("php99", "")]
    [InlineData("", "")]
    public void Php_log_key_comes_from_the_servers_version(string version, string key) =>
        Assert.Equal(key, Actions.ServerPhpLogKey(new Server { Id = "1", PhpVersion = version }));

    [Fact]
    public void Each_server_section_is_one_row_in_the_same_order()
    {
        var web = new Server { Id = "1", Kind = "web", PhpVersion = "php83", State = ServerState.Ready };
        var ids = Actions.ServerActions("acme", web).Select(a => a.Id).ToList();
        var sections = ids.Where(id => id is "events" or "server-logs" or "processes" or "schedule").ToList();
        Assert.Equal(["events", "server-logs", "processes", "schedule"], sections);
        Assert.DoesNotContain(ids, id => id.StartsWith("server-log:", StringComparison.Ordinal));
        Assert.All(Actions.ServerActions("acme", web).Where(a => sections.Contains(a.Id)), a => Assert.False(a.Armable));
        Assert.Equal("Logs", Actions.ServerActions("acme", web).Single(a => a.Id == "server-logs").Label);
    }

    [Fact]
    public void Each_site_section_is_one_row_in_the_same_order()
    {
        var site = new Site { Id = "2", ServerId = "1" };
        var ids = Actions.SiteActions("acme", site).Select(a => a.Id).ToList();
        Assert.Equal(["deployments", "site-logs", "site-schedule"],
            ids.Where(id => id is "deployments" or "site-logs" or "site-schedule").ToList());
        Assert.DoesNotContain("log", ids);
        Assert.DoesNotContain(ids, id => id.StartsWith("site-log:", StringComparison.Ordinal));
        // A site that was never deployed still opens its (empty) history rather than a dead row.
        Assert.True(Actions.SiteActions("acme", site).Single(a => a.Id == "deployments").Available);
    }

    [Fact]
    public void Clearing_a_log_is_a_delete_with_its_own_scope()
    {
        var site = new Site { Id = "2", ServerId = "1" };
        var clear = Actions.ClearSiteLog("acme", site, "nginx-error");
        Assert.True(clear.Armable);
        Assert.Equal("DELETE", clear.Method);
        Assert.Null(clear.Body);
        Assert.Equal("/orgs/acme/servers/1/sites/2/logs/nginx-error", clear.Path);
        Assert.Equal("Clear nginx error log", clear.Label);
        Assert.Equal("press Y to empty the nginx error log", clear.ArmedText);
        Assert.Contains("server:manage-logs", clear.ScopeMessage);
        Assert.Equal(Refetch.None, clear.Refetch);

        var server = new Server { Id = "1" };
        var php = Actions.ClearServerLog("acme", server, "php-8.4");
        Assert.Equal("/orgs/acme/servers/1/logs/php-8.4", php.Path);
        Assert.Equal("Clear PHP 8.4 log", php.Label);
        Assert.Equal("Clear MySQL log", Actions.ClearServerLog("acme", server, "database-mysql").Label);

        var unknown = Actions.ClearServerLog("acme", server, "../etc");
        Assert.False(unknown.Available);
        Assert.Equal("", unknown.Path);
        Assert.False(Actions.ClearSiteLog("acme", site, "bogus").Available);
    }

    [Fact]
    public void Restarting_a_process_posts_the_restart_action()
    {
        var server = new Server { Id = "1", State = ServerState.Ready };
        var process = new BackgroundProcess { Id = "11", Command = "php artisan queue:work" };
        var restart = Actions.ProcessRestart("acme", server, process);
        Assert.True(restart.Available);
        Assert.Equal("POST", restart.Method);
        Assert.Equal("/orgs/acme/servers/1/background-processes/11/actions", restart.Path);
        Assert.Equal("""{"action":"restart"}""", restart.Body!.ToJsonString());
        Assert.Equal("press Y to restart php artisan queue:work", restart.ArmedText);
        Assert.Equal("11", restart.ArmIntent);
        Assert.Contains("server:create-daemons", restart.ScopeMessage);

        var down = Actions.ProcessRestart("acme", server with { State = ServerState.Unreachable }, process);
        Assert.False(down.Available);
        Assert.Equal("unreachable", down.Reason);
    }
}

public class LogsAndProcessesServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "forge-tests-" + Guid.NewGuid().ToString("N"));
    private readonly FakeClient _client = new();
    private readonly MemoryCredentialStore _creds = new();
    private readonly StateStore _store;
    private static readonly Site Zeta = new() { Id = "1", ServerId = "101", Name = "zeta" };

    public LogsAndProcessesServiceTests()
    {
        _store = new StateStore(Path.Combine(_dir, "state.json"));
        var setup = SetupState.Empty.WithAccount("work", new ForgeUser("me@example.com", "Me"),
            [new OrgEntry("acme", "", "Acme")], out _);
        _store.Save(setup);
        _creds.Write("work", "secret-token");
        _client.Route = (_, path) =>
        {
            if (path.Contains("/deployments?", StringComparison.Ordinal)) return FakeClient.Ok(Fixture.Load("deployments.json"));
            if (path.Contains("/background-processes?", StringComparison.Ordinal)) return FakeClient.Ok(Fixture.Load("background-processes.json"));
            if (path.Contains("/scheduled-jobs?", StringComparison.Ordinal)) return FakeClient.Ok(Fixture.Load("scheduled-jobs.json"));
            if (path.EndsWith("/output", StringComparison.Ordinal) || path.EndsWith("/deployments/9002/log", StringComparison.Ordinal))
                return FakeClient.Ok(System.Text.Json.Nodes.JsonNode.Parse("""{"data":{"attributes":{"output":"one\ntwo\n"}}}""")!);
            if (path.Contains("/logs/", StringComparison.Ordinal) || path.EndsWith("/log", StringComparison.Ordinal))
                return FakeClient.Ok(System.Text.Json.Nodes.JsonNode.Parse("""{"data":{"attributes":{"content":"=== Empty log file ==="}}}""")!);
            return new ForgeEnvelope { Ok = true, Status = 202 };
        };
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private ForgeService NewService()
    {
        var service = new ForgeService(_client, _creds, _store);
        service.Reconcile(_store.Load());
        return service;
    }

    [Fact]
    public void Nothing_is_read_until_a_view_asks()
    {
        _ = NewService();
        Assert.Empty(_client.Calls);
    }

    [Fact]
    public async Task Each_read_asks_for_its_own_path()
    {
        var service = NewService();
        var deployments = await service.DeploymentsAsync("acme", Zeta);
        Assert.Equal(3, deployments.Value!.Count);
        Assert.Equal("abc", deployments.NextCursor);
        var log = await service.DeploymentLogAsync("acme", Zeta, "9002");
        Assert.Equal(["one", "two"], log.Value!);
        Assert.Equal(4, (await service.BackgroundProcessesAsync("acme", "101")).Value!.Count);
        Assert.Empty((await service.BackgroundProcessLogAsync("acme", "101", "11")).Value!);
        Assert.Empty((await service.ServerLogAsync("acme", "101", "nginx-error")).Value!);
        Assert.Equal(2, (await service.ScheduledJobsAsync("acme", "101", null)).Value!.Count);
        Assert.Equal(2, (await service.ScheduledJobsAsync("acme", "101", Zeta)).Value!.Count);
        Assert.Equal(["one", "two"], (await service.ScheduledJobOutputAsync("acme", "101", null, "21")).Value!);
        Assert.Equal(["one", "two"], (await service.ScheduledJobOutputAsync("acme", "101", Zeta, "21")).Value!);

        Assert.Equal(
        [
            "/orgs/acme/servers/101/sites/1/deployments?sort=-created_at&page%5Bsize%5D=30",
            "/orgs/acme/servers/101/sites/1/deployments/9002/log",
            "/orgs/acme/servers/101/background-processes?page%5Bsize%5D=30",
            "/orgs/acme/servers/101/background-processes/11/log",
            "/orgs/acme/servers/101/logs/nginx-error",
            "/orgs/acme/servers/101/scheduled-jobs?page%5Bsize%5D=30",
            "/orgs/acme/servers/101/sites/1/scheduled-jobs?page%5Bsize%5D=30",
            "/orgs/acme/servers/101/scheduled-jobs/21/output",
            "/orgs/acme/servers/101/sites/1/scheduled-jobs/21/output",
        ], _client.Calls.Select(c => c.Path));
    }

    [Fact]
    public async Task An_unknown_server_log_is_refused_without_a_request()
    {
        var service = NewService();
        var result = await service.ServerLogAsync("acme", "101", "../../user");
        Assert.False(result.Ok);
        Assert.Empty(_client.Calls);
    }

    [Fact]
    public async Task Every_new_read_names_the_scope_it_needs()
    {
        _client.Route = (_, _) => new ForgeEnvelope { Ok = false, Status = 403 };
        var service = NewService();
        Assert.Contains("server:view", (await service.DeploymentsAsync("acme", Zeta)).Error);
        Assert.Contains("site:manage-deploys", (await service.DeploymentLogAsync("acme", Zeta, "9002")).Error);
        Assert.Contains("server:manage-logs", (await service.ServerLogAsync("acme", "101", "ssh")).Error);
        Assert.Contains("server:view", (await service.BackgroundProcessesAsync("acme", "101")).Error);
        Assert.Contains("server:create-daemons", (await service.BackgroundProcessLogAsync("acme", "101", "11")).Error);
        Assert.Contains("server:view", (await service.ScheduledJobsAsync("acme", "101", Zeta)).Error);
        Assert.Contains("server:view", (await service.ScheduledJobOutputAsync("acme", "101", null, "21")).Error);
    }

    [Fact]
    public async Task Clear_and_restart_send_the_declared_request_and_report_their_scope()
    {
        var service = NewService();
        var server = new Server { Id = "101", State = ServerState.Ready };
        var clear = await service.SendActionAsync("acme", "101", Actions.ClearSiteLog("acme", Zeta, "application"));
        Assert.True(clear.Ok);
        Assert.Equal("Application log cleared", clear.Message);
        var restart = await service.SendActionAsync("acme", "101",
            Actions.ProcessRestart("acme", server, new BackgroundProcess { Id = "11", Command = "x" }));
        Assert.True(restart.Ok);
        Assert.Equal(
        [
            ("DELETE", "/orgs/acme/servers/101/sites/1/logs/application", (string?)null),
            ("POST", "/orgs/acme/servers/101/background-processes/11/actions", """{"action":"restart"}"""),
        ], _client.Calls);

        _client.Route = (_, _) => new ForgeEnvelope { Ok = false, Status = 403 };
        var refused = await service.SendActionAsync("acme", "101", Actions.ClearServerLog("acme", server, "ssh"));
        Assert.Equal(Actions.LogScopeMessage, refused.Message);
        Assert.Equal("", service.Snapshot.Org("acme")!.LastError);
    }
}
