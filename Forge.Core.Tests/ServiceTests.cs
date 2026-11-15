using System.Text.Json.Nodes;
using Forge.Core.Api;
using Forge.Core.Model;
using Forge.Core.Secrets;
using Forge.Core.Service;
using Forge.Core.Setup;

namespace Forge.Core.Tests;

/// <summary>Answers requests from a routing function and records what was asked.</summary>
internal sealed class FakeClient : IForgeClient
{
    public Func<string, string, ForgeEnvelope> Route { get; set; } = (_, _) => new ForgeEnvelope { Ok = true, Status = 200, Body = new JsonObject() };
    public List<(string Method, string Path, string? Body)> Calls { get; } = [];

    public Task<ForgeEnvelope> SendAsync(string method, string path, string? token, JsonNode? body, CancellationToken ct)
    {
        lock (Calls) Calls.Add((method, path, body?.ToJsonString()));
        return Task.FromResult(Route(method, path));
    }

    public static ForgeEnvelope Ok(JsonNode body) => new() { Ok = true, Status = 200, Body = body };
}

public class ServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "forge-tests-" + Guid.NewGuid().ToString("N"));
    private readonly FakeClient _client = new();
    private readonly MemoryCredentialStore _creds = new();
    private readonly StateStore _store;
    private string _status = "finished";

    public ServiceTests()
    {
        _store = new StateStore(Path.Combine(_dir, "state.json"));
        var setup = SetupState.Empty.WithAccount("work", new ForgeUser("me@example.com", "Me"),
            [new OrgEntry("acme", "", "Acme")], out _);
        _store.Save(setup);
        _creds.Write("work", "secret-token");

        _client.Route = (method, path) =>
        {
            if (path.StartsWith("/orgs/acme/servers?", StringComparison.Ordinal))
                return FakeClient.Ok(Fixture.Load("servers.json"));
            if (path.StartsWith("/orgs/acme/sites?", StringComparison.Ordinal) ||
                path.StartsWith("/orgs/acme/servers/101/sites?", StringComparison.Ordinal))
            {
                var body = Fixture.Load("sites.json");
                body["data"]![1]!["attributes"]!["deployment_status"] = _status;
                return FakeClient.Ok(body);
            }
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
    public async Task A_refresh_costs_two_requests_and_publishes_servers_and_sites()
    {
        var service = NewService();
        await service.RefreshOrgAsync("acme", CancellationToken.None);

        Assert.Equal(2, _client.Calls.Count);
        var org = service.Snapshot.Org("acme")!;
        Assert.Equal(3, org.Servers.Count);
        Assert.Equal(["admin.example.com", "zeta.example.com"], org.SitesOf("101").Select(s => s.Name));
        Assert.Empty(org.SitesOf("102"));
        // lb-1 is unreachable, which outranks everything.
        Assert.Equal(Health.Bad, service.Snapshot.Health);
        Assert.Equal("", org.LastError);
    }

    [Fact]
    public async Task First_observation_is_silent_and_a_change_announces_once()
    {
        var service = NewService();
        var notices = new List<DeploymentNotice>();
        service.DeploymentChanged += notices.Add;

        _status = "deploying";
        await service.RefreshOrgAsync("acme", CancellationToken.None);
        Assert.Empty(notices);

        _status = "finished";
        await service.RefreshOrgAsync("acme", CancellationToken.None);
        var notice = Assert.Single(notices);
        Assert.False(notice.Failed);
        Assert.Equal("admin.example.com", notice.Headline);
        Assert.Equal("https://admin.example.com", notice.Url);

        await service.RefreshOrgAsync("acme", CancellationToken.None);
        Assert.Single(notices);

        _status = "failed";
        await service.FetchServerSitesAsync("acme", "101", force: true);
        Assert.Equal(2, notices.Count);
        Assert.True(notices[1].Failed);
    }

    [Fact]
    public async Task Missing_token_is_reported_once_on_the_account()
    {
        _creds.Delete("work");
        var service = NewService();
        await service.RefreshOrgAsync("acme", CancellationToken.None);
        Assert.Empty(_client.Calls);
        var org = service.Snapshot.Org("acme")!;
        Assert.StartsWith("No token for", org.AccountError);
        Assert.Equal("", org.LastError);
        Assert.Equal(Health.Setup, org.Health);
    }

    [Fact]
    public async Task A_429_holds_the_bucket_and_refuses_writes()
    {
        _client.Route = (_, _) => new ForgeEnvelope { Ok = false, Status = 429, RateResetSec = 30 };
        var service = NewService();
        await service.RefreshOrgAsync("acme", CancellationToken.None);
        Assert.Single(_client.Calls);
        Assert.Contains("Rate limited", service.Snapshot.Org("acme")!.LastError);

        var site = new Site { Id = "2", ServerId = "101", RepoUrl = "git@x" };
        var outcome = await service.DeployAsync("acme", site);
        Assert.False(outcome.Ok);
        Assert.StartsWith("Rate limited", outcome.Message);
        Assert.Single(_client.Calls);
    }

    [Fact]
    public async Task Writes_send_the_declared_method_path_and_body()
    {
        var service = NewService();
        var server = new Server { Id = "101", Kind = "app", PhpVersion = "php84", State = ServerState.Ready };
        var reload = Actions.ServerActions("acme", server).Single(a => a.Id == "php-reload");
        var outcome = await service.SendActionAsync("acme", server.Id, reload);
        Assert.True(outcome.Ok);
        Assert.Equal("PHP-FPM reload requested", outcome.Message);
        var call = Assert.Single(_client.Calls);
        Assert.Equal(("POST", "/orgs/acme/servers/101/services/php/actions", """{"action":"reload","version":"php84"}"""), call);
    }

    [Fact]
    public async Task A_403_on_a_scoped_write_stays_on_the_row()
    {
        _client.Route = (_, _) => new ForgeEnvelope { Ok = false, Status = 403 };
        var service = NewService();
        var server = new Server { Id = "101", Kind = "app", State = ServerState.Ready };
        var outcome = await service.SendActionAsync("acme", server.Id, Actions.ServerActions("acme", server).Single(a => a.Id == "reboot"));
        Assert.Equal(Actions.ServerScopeMessage, outcome.Message);
        Assert.Equal("", service.Snapshot.Org("acme")!.LastError);
    }

    [Fact]
    public async Task Deployment_log_reports_a_missing_scope_on_the_pane()
    {
        _client.Route = (_, _) => new ForgeEnvelope { Ok = false, Status = 403 };
        var service = NewService();
        var result = await service.DeploymentLogAsync("acme", new Site { Id = "1", ServerId = "101", DeploymentId = "9" });
        Assert.Contains("site:manage-deploys", result.Error);
        Assert.Equal("/orgs/acme/servers/101/sites/1/deployments/9/log", Assert.Single(_client.Calls).Path);
    }
}
