using System.Text.Json.Nodes;
using Forge.Core.Api;
using Forge.Core.Model;
using Forge.Core.Secrets;
using Forge.Core.Service;
using Forge.Core.Setup;

namespace Forge.Core.Tests;

public class DeployModelTests
{
    private static readonly Site Site = new()
    {
        Id = "7", ServerId = "101", Name = "app.example.com", RepoUrl = "https://github.com/acme/app", Branch = "main",
    };

    [Fact]
    public void Paths_sit_under_the_site_and_encode_their_segments()
    {
        Assert.Equal("/orgs/acme/servers/101/sites/7/deployments/push-to-deploy", Paths.PushToDeploy("acme", "101", "7"));
        Assert.Equal("/orgs/acme/servers/101/sites/7/deployments/deploy-hook", Paths.DeployHook("acme", "101", "7"));
        Assert.Equal("/orgs/acme/servers/101/sites/7/webhooks?page%5Bsize%5D=30", Paths.Webhooks("acme", "101", "7"));
        Assert.Equal("/orgs/acme/servers/101/sites/7/webhooks", Paths.WebhookCreate("acme", "101", "7"));
        Assert.Equal("/orgs/acme/servers/101/sites/7/webhooks/3", Paths.Webhook("acme", "101", "7", "3"));
        Assert.Equal("/orgs/acme/servers/101/sites/7/deploy-key", Paths.DeployKey("acme", "101", "7"));
        Assert.Equal("/orgs/a%2Fb/servers/1%3F/sites/%23/webhooks/x%2Fy", Paths.Webhook("a/b", "1?", "#", "x/y"));
        Assert.Equal("/orgs/a%2Fb/servers/1%3F/sites/%23/deployments/deploy-hook", Paths.DeployHook("a/b", "1?", "#"));
    }

    [Fact]
    public void Quick_deploy_toggle_flips_method_and_intent()
    {
        var on = Actions.QuickDeploy("acme", Site with { QuickDeploy = true });
        Assert.Equal("DELETE", on.Method);
        Assert.Equal("off", on.ArmIntent);
        Assert.Null(on.Body);
        Assert.Equal("Disable quick deploy", on.Label);

        var off = Actions.QuickDeploy("acme", Site);
        Assert.Equal("POST", off.Method);
        Assert.Equal("on", off.ArmIntent);
        Assert.Equal("{}", off.Body!.ToJsonString());

        // Never deployments/status: its DELETE resets a deployment's state, not quick deploy.
        Assert.All(new[] { on, off }, s => Assert.EndsWith("/deployments/push-to-deploy", s.Path));
        Assert.All(new[] { on, off }, s => Assert.Equal(Refetch.Sites, s.Refetch));
        Assert.Contains("site:manage-deploys", on.ScopeMessage);
    }

    [Fact]
    public void Quick_deploy_needs_a_repository()
    {
        var spec = Actions.QuickDeploy("acme", Site with { RepoUrl = "" });
        Assert.False(spec.Available);
        Assert.Equal("no repository", spec.Reason);
        Assert.False(Actions.QuickDeploy("acme", null).Available);
        Assert.Equal("", Actions.QuickDeploy("acme", null).Path);
    }

    [Fact]
    public void The_site_list_offers_quick_deploy_the_hook_and_the_settings()
    {
        var ids = Actions.SiteActions("acme", Site).Select(a => a.Id).ToList();
        Assert.Contains("quick-deploy", ids);
        Assert.Contains("deploy-hook", ids);
        Assert.Contains("deploy-settings", ids);
        var hook = Actions.SiteActions("acme", Site).Single(a => a.Id == "deploy-hook");
        Assert.False(hook.Armable);
        Assert.Equal("u", hook.Hint);
    }

    [Fact]
    public void Regenerating_the_hook_is_a_put_with_no_body()
    {
        var spec = Actions.RegenerateDeployHook("acme", Site);
        Assert.Equal("PUT", spec.Method);
        Assert.Equal(Paths.DeployHook("acme", "101", "7"), spec.Path);
        Assert.Null(spec.Body);
        Assert.True(spec.Armable);
        Assert.False(Actions.RegenerateDeployHook("acme", null).Available);
    }

    [Theory]
    [InlineData("https://example.com/hook", "")]
    [InlineData("  http://example.com  ", "")]
    [InlineData("", "Type a URL first")]
    [InlineData("example.com/hook", "That isn't a URL")]
    [InlineData("ftp://example.com/hook", "Use an http or https URL")]
    [InlineData("file:///C:/x", "That isn't a URL")]
    [InlineData("javascript:alert(1)", "That isn't a URL")]
    public void A_webhook_url_must_be_http_or_https(string url, string problem) =>
        Assert.Equal(problem, Actions.WebhookUrlProblem(url));

    [Fact]
    public void Adding_a_webhook_sends_its_trimmed_url()
    {
        var spec = Actions.AddWebhook("acme", Site, "  https://example.com/h?t=1  ");
        Assert.Equal("POST", spec.Method);
        Assert.Equal("/orgs/acme/servers/101/sites/7/webhooks", spec.Path);
        Assert.Equal("https://example.com/h?t=1", (string)spec.Body!["url"]!);
        Assert.Equal("https://example.com/h?t=1", spec.ArmIntent);
        Assert.True(spec.Available);

        var bad = Actions.AddWebhook("acme", Site, "nope");
        Assert.False(bad.Available);
        Assert.Equal("That isn't a URL", bad.Reason);
    }

    [Fact]
    public void Deleting_a_webhook_names_it()
    {
        var spec = Actions.DeleteWebhook("acme", Site, new Webhook("3", "https://example.com", ""));
        Assert.Equal(("DELETE", "/orgs/acme/servers/101/sites/7/webhooks/3"), (spec.Method, spec.Path));
        Assert.Equal("3", spec.ArmIntent);
        var gone = Actions.DeleteWebhook("acme", Site, null);
        Assert.False(gone.Available);
        Assert.Equal("", gone.Path);
    }

    [Fact]
    public void The_deploy_key_is_made_with_a_post_and_removed_with_a_delete()
    {
        var create = Actions.CreateDeployKey("acme", Site);
        var delete = Actions.DeleteDeployKey("acme", Site);
        Assert.Equal(("POST", "/orgs/acme/servers/101/sites/7/deploy-key"), (create.Method, create.Path));
        Assert.Equal(("DELETE", "/orgs/acme/servers/101/sites/7/deploy-key"), (delete.Method, delete.Path));
        Assert.Null(create.Body);
        Assert.All(new[] { create, delete }, s => Assert.Contains("site:manage-deploys", s.ScopeMessage));
    }

    [Fact]
    public void Webhooks_parse_and_rows_without_an_id_or_url_are_dropped()
    {
        var body = JsonNode.Parse("""
            {"data":[
              {"id":"36141","type":"deploymentWebhooks","attributes":{"url":"https://example.com/a","created_at":"2026-11-24T15:51:56.000000Z"}},
              {"type":"deploymentWebhooks","attributes":{"url":"https://example.com/b"}},
              {"id":"2","attributes":{"url":null}}
            ],"meta":{"next_cursor":null}}
            """);
        var hook = Assert.Single(Parse.Webhooks(body));
        Assert.Equal(new Webhook("36141", "https://example.com/a", "2026-11-24T15:51:56.000000Z"), hook);
        Assert.Empty(Parse.Webhooks(JsonNode.Parse("""{"data":[]}""")));
    }

    [Fact]
    public void A_missing_deploy_key_reads_as_empty_and_a_malformed_answer_as_null()
    {
        Assert.Equal("ssh-rsa AAAA", Parse.DeployKey(JsonNode.Parse("""{"data":{"attributes":{"key":"ssh-rsa AAAA"}}}""")));
        Assert.Equal("", Parse.DeployKey(JsonNode.Parse("""{"data":{"attributes":{"key":null}}}""")));
        Assert.Null(Parse.DeployKey(JsonNode.Parse("""{"data":{"attributes":{}}}""")));
        Assert.Null(Parse.DeployKey(null));
    }

    [Theory]
    [InlineData("https://hooks.slack.com/services/T0/B0/secret", "https://hooks.slack.com/…")]
    [InlineData("https://example.com", "https://example.com")]
    [InlineData("https://example.com/", "https://example.com")]
    [InlineData("https://example.com/?token=x", "https://example.com/…")]
    [InlineData("https://user:pass@example.com:8443/x", "https://example.com:8443/…")]
    [InlineData("not a url", "•••")]
    [InlineData(null, "•••")]
    public void A_webhook_url_is_shown_as_its_host(string? url, string shown) =>
        Assert.Equal(shown, ForgeText.MaskedUrl(url));

    [Fact]
    public void Site_details_say_whether_quick_deploy_is_on_when_there_is_a_repository()
    {
        Assert.Contains(("Quick deploy", "on"), Status.SiteDetails(Site with { QuickDeploy = true }));
        Assert.Contains(("Quick deploy", "off"), Status.SiteDetails(Site));
        Assert.DoesNotContain(Status.SiteDetails(Site with { RepoUrl = "" }), d => d.Label == "Quick deploy");
    }
}

public class DeployServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "forge-tests-" + Guid.NewGuid().ToString("N"));
    private readonly FakeClient _client = new();
    private readonly MemoryCredentialStore _creds = new();
    private readonly StateStore _store;
    private static readonly Site Site = new() { Id = "7", ServerId = "101", Name = "app.example.com", RepoUrl = "https://github.com/acme/app" };
    private const string HookPath = "/orgs/acme/servers/101/sites/7/deployments/deploy-hook";

    public DeployServiceTests()
    {
        _store = new StateStore(Path.Combine(_dir, "state.json"));
        _store.Save(SetupState.Empty.WithAccount("work", new ForgeUser("me@example.com", "Me"), [new OrgEntry("acme", "", "Acme")], out _));
        _creds.Write("work", "secret-token");
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

    private static ForgeEnvelope Attrs(JsonObject attrs) =>
        FakeClient.Ok(new JsonObject { ["data"] = new JsonObject { ["id"] = "7", ["attributes"] = attrs } });

    [Fact]
    public async Task The_hook_url_is_read_with_one_get()
    {
        _client.Route = (_, _) => Attrs(new JsonObject { ["url"] = "https://forge.laravel.com/servers/1/sites/7/deploy/http?token=abc" });
        var result = await NewService().DeployHookUrlAsync("acme", Site);
        Assert.Equal("https://forge.laravel.com/servers/1/sites/7/deploy/http?token=abc", result.Value);
        Assert.Equal(("GET", HookPath, (string?)null), Assert.Single(_client.Calls));
    }

    [Fact]
    public async Task A_hook_answer_without_a_url_is_an_error()
    {
        _client.Route = (_, _) => Attrs(new JsonObject { ["url"] = null });
        var result = await NewService().DeployHookUrlAsync("acme", Site);
        Assert.False(result.Ok);
        Assert.DoesNotContain("token", result.Error);
    }

    [Fact]
    public async Task A_refused_read_names_the_deploy_scope()
    {
        _client.Route = (_, _) => new ForgeEnvelope { Ok = false, Status = 403 };
        var service = NewService();
        Assert.Contains("site:manage-deploys", (await service.DeployHookUrlAsync("acme", Site)).Error);
        Assert.Contains("site:manage-deploys", (await service.WebhooksAsync("acme", Site)).Error);
        Assert.Contains("site:manage-deploys", (await service.DeployKeyAsync("acme", Site)).Error);
    }

    [Fact]
    public async Task Webhooks_and_the_key_are_read_from_their_own_paths()
    {
        _client.Route = (_, path) => path.Contains("/webhooks")
            ? FakeClient.Ok(JsonNode.Parse("""{"data":[{"id":"1","attributes":{"url":"https://example.com"}}],"meta":{"next_cursor":"c2"}}""")!)
            : Attrs(new JsonObject { ["key"] = null });
        var service = NewService();
        var hooks = await service.WebhooksAsync("acme", Site);
        var key = await service.DeployKeyAsync("acme", Site);
        Assert.Single(hooks.Value!);
        Assert.Equal("c2", hooks.NextCursor);
        Assert.Equal("", key.Value);
        Assert.Equal(["/orgs/acme/servers/101/sites/7/webhooks?page%5Bsize%5D=30", "/orgs/acme/servers/101/sites/7/deploy-key"],
            _client.Calls.Select(c => c.Path));
    }

    [Fact]
    public async Task Turning_quick_deploy_on_posts_an_empty_body_to_push_to_deploy()
    {
        _client.Route = (_, _) => new ForgeEnvelope { Ok = true, Status = 202 };
        var outcome = await NewService().SendActionAsync("acme", "101", Actions.QuickDeploy("acme", Site));
        Assert.True(outcome.Ok);
        Assert.Equal("Quick deploy on — requested", outcome.Message);
        Assert.Equal(("POST", "/orgs/acme/servers/101/sites/7/deployments/push-to-deploy", "{}"), _client.Calls[0]);
    }

    [Fact]
    public async Task A_refused_deploy_write_stays_on_the_row()
    {
        _client.Route = (_, _) => new ForgeEnvelope { Ok = false, Status = 403 };
        var service = NewService();
        var outcome = await service.SendActionAsync("acme", "101", Actions.RegenerateDeployHook("acme", Site));
        Assert.Equal(Actions.DeployScopeMessage, outcome.Message);
        Assert.Equal("", service.Snapshot.Org("acme")!.LastError);
    }

    [Fact]
    public async Task A_rejected_webhook_carries_forges_message()
    {
        _client.Route = (_, _) => new ForgeEnvelope
        {
            Ok = false, Status = 422, Body = new JsonObject { ["message"] = "The url field must be a valid URL." },
        };
        var outcome = await NewService().SendActionAsync("acme", "101", Actions.AddWebhook("acme", Site, "https://example.com/x"));
        Assert.Equal("The url field must be a valid URL.", outcome.Message);
        Assert.Equal("""{"url":"https://example.com/x"}""", _client.Calls[0].Body);
    }
}
