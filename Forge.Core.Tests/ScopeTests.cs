using System.Reflection;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Forge.Core.Api;
using Forge.Core.Model;
using Forge.Core.Secrets;
using Forge.Core.Service;
using Forge.Core.Setup;

namespace Forge.Core.Tests;

public class ScopeTests
{
    [Fact]
    public void The_list_has_each_scope_once_and_three_needed()
    {
        var names = Scopes.All.Select(s => s.Name).ToList();
        Assert.Equal(names.Count, names.Distinct().Count());
        Assert.Equal(["user:view", "organization:view", "server:view"], Scopes.Required.Select(s => s.Name));
        Assert.All(Scopes.All, s => Assert.Matches("^[a-z]+:[a-z-]+$", s.Name));
        Assert.All(Scopes.All, s => Assert.NotEqual("", s.Use));
    }

    /// <summary>Every "needs the X scope" message the app can show must name a scope the Settings list tells people to tick.</summary>
    [Fact]
    public void Every_scope_a_message_names_is_in_the_list()
    {
        var messages = typeof(Actions).Assembly.GetTypes()
            .SelectMany(t => t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .Where(v => v.Contains(" scope", StringComparison.Ordinal))
            .ToList();
        Assert.NotEmpty(messages);
        foreach (var message in messages) AssertNamesKnownScopes(message);
    }

    /// <summary>
    /// The messages written inline in a spec, or built from a table, aren't constants: build every write
    /// the app can send and check the scope its 403 names.
    /// </summary>
    [Fact]
    public void Every_write_names_a_listed_scope_when_refused()
    {
        var server = new Server { Id = "101", Name = "web-1", Ip = "203.0.113.10", PhpVersion = "php84", DatabaseType = "mysql8", State = ServerState.Ready };
        var site = new Site { Id = "7", ServerId = "101", Name = "app.example.com", RepoUrl = "git@x", PhpVersion = "PHP 8.4" };
        var key = TestKeys.Public();
        var specs = new List<ActionSpec>();
        specs.AddRange(Actions.ServerActions("acme", server));
        specs.AddRange(Actions.SiteActions("acme", site));
        specs.AddRange(Actions.SiteActions("acme", site with { Maintenance = true }));
        specs.Add(Actions.ClearSiteLog("acme", site, Actions.SiteLogKinds[0].Kind));
        specs.Add(Actions.ClearServerLog("acme", server, Actions.ServerLogKinds(server)[0].Key));
        specs.Add(Actions.ProcessRestart("acme", server, new BackgroundProcess { Id = "3", Command = "php artisan queue:work" }));
        specs.Add(Actions.QuickDeploy("acme", site));
        specs.Add(Actions.QuickDeploy("acme", site with { QuickDeploy = true }));
        specs.Add(Actions.RegenerateDeployHook("acme", site));
        specs.Add(Actions.AddWebhook("acme", site, "https://example.com/hook"));
        specs.Add(Actions.DeleteWebhook("acme", site, new Webhook("4", "https://example.com/hook", "")));
        specs.Add(Actions.CreateDeployKey("acme", site));
        specs.Add(Actions.DeleteDeployKey("acme", site));
        specs.Add(Actions.SiteCommand("acme", site, "php artisan about"));
        specs.Add(Actions.RecipeRun("acme", server, new Recipe("5", "Update", "root", "apt update", "")));
        specs.AddRange(ConfigFiles.All.Select(f => Actions.SaveConfig("acme", site, f.File, "x")));
        specs.Add(Actions.AddSshKey("acme", server, "laptop", key, "forge"));
        specs.Add(Actions.DeleteSshKey("acme", server, new SshKey("5", "laptop", "forge", "installed", "", "")));
        specs.Add(Actions.AddFirewallRule("acme", server, "Web", "443", "", true));
        specs.Add(Actions.DeleteFirewallRule("acme", server, new FirewallRule("9", "Web", "443", "", "allow", "installed", "")));

        // The deploy has no scope message of its own: its refusal is reported on the organization.
        var writes = specs.Where(s => s.Armable && s.Id != "deploy").ToList();
        Assert.True(writes.Count > 20, $"only {writes.Count} writes were built");
        foreach (var spec in writes)
        {
            Assert.False(spec.ScopeMessage is null, $"{spec.Id} has no scope message");
            Assert.Matches(@"[a-z]+:[a-z-]+ scope", spec.ScopeMessage);
            AssertNamesKnownScopes(spec.ScopeMessage!);
        }

        foreach (var file in ConfigFiles.All)
        {
            Assert.Contains(file.Scope, Scopes.All.Select(s => s.Name));
            AssertNamesKnownScopes(ConfigFiles.ReadScopeMessage(file.File));
            AssertNamesKnownScopes(ConfigFiles.WriteScopeMessage(file.File));
        }
    }

    private static void AssertNamesKnownScopes(string message)
    {
        var known = Scopes.All.Select(s => s.Name).ToHashSet();
        foreach (Match m in Regex.Matches(message, @"([a-z]+:[a-z-]+) scope"))
            Assert.True(known.Contains(m.Groups[1].Value), $"\"{message}\" names {m.Groups[1].Value}, which isn't in Scopes.All");
    }
}

public class TokenCheckTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "forge-tests-" + Guid.NewGuid().ToString("N"));
    private readonly FakeClient _client = new();

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private ForgeService NewService() =>
        new(_client, new MemoryCredentialStore(), new StateStore(Path.Combine(_dir, "state.json")));

    [Fact]
    public async Task A_refused_user_read_names_user_view()
    {
        _client.Route = (_, _) => new ForgeEnvelope { Ok = false, Status = 403 };
        var (user, _, error) = await NewService().VerifyTokenAsync("t");
        Assert.Null(user);
        Assert.Contains("user:view", error);
    }

    [Fact]
    public async Task A_refused_organization_list_names_organization_view()
    {
        _client.Route = (_, path) => path == "/user"
            ? FakeClient.Ok(new JsonObject { ["data"] = new JsonObject { ["attributes"] = new JsonObject { ["email"] = "me@example.com" } } })
            : new ForgeEnvelope { Ok = false, Status = 403 };
        var (user, orgs, error) = await NewService().VerifyTokenAsync("t");
        Assert.NotNull(user);
        Assert.Empty(orgs);
        Assert.Contains("organization:view", error);
    }

    [Fact]
    public async Task A_rejected_token_is_still_a_401()
    {
        _client.Route = (_, _) => new ForgeEnvelope { Ok = false, Status = 401 };
        Assert.Equal("Forge rejected that token", (await NewService().VerifyTokenAsync("t")).Error);
    }
}
