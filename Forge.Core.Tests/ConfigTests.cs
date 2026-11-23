using System.Text.Json.Nodes;
using Forge.Core.Api;
using Forge.Core.Model;
using Forge.Core.Secrets;
using Forge.Core.Service;
using Forge.Core.Setup;

namespace Forge.Core.Tests;

public class ConfigModelTests
{
    private static readonly Site Site = new() { Id = "7", ServerId = "101", Name = "app.example.com" };

    [Fact]
    public void Paths_sit_under_the_site_and_encode_their_segments()
    {
        Assert.Equal("/orgs/acme/servers/101/sites/7/environment", Paths.SiteEnvironment("acme", "101", "7"));
        Assert.Equal("/orgs/acme/servers/101/sites/7/deployments/script", Paths.DeploymentScript("acme", "101", "7"));
        Assert.Equal("/orgs/acme/servers/101/sites/7/nginx", Paths.SiteNginx("acme", "101", "7"));
        Assert.Equal("/orgs/a%2Fb/servers/1%3F/sites/%23/nginx", Paths.SiteNginx("a/b", "1?", "#"));
    }

    [Theory]
    [InlineData(ConfigFile.Environment, "/orgs/acme/servers/101/sites/7/environment", """{"environment":"A=1\n"}""")]
    [InlineData(ConfigFile.DeployScript, "/orgs/acme/servers/101/sites/7/deployments/script", """{"content":"A=1\n"}""")]
    [InlineData(ConfigFile.Nginx, "/orgs/acme/servers/101/sites/7/nginx", """{"config":"A=1\n"}""")]
    public void Each_save_puts_the_whole_file_under_its_own_key(ConfigFile file, string path, string body)
    {
        var spec = Actions.SaveConfig("acme", Site, file, "A=1\n");
        Assert.Equal("PUT", spec.Method);
        Assert.Equal(path, spec.Path);
        Assert.Equal(body, spec.Body!.ToJsonString(new System.Text.Json.JsonSerializerOptions
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }));
        Assert.True(spec.Available);
        Assert.Contains(ConfigFiles.Of(file).Scope, spec.ScopeMessage);
    }

    [Fact]
    public void The_deploy_scripts_auto_source_goes_back_only_when_forge_reported_it()
    {
        Assert.False(Actions.SaveConfig("acme", Site, ConfigFile.DeployScript, "x").Body!.ContainsKey("auto_source"));
        Assert.True((bool)Actions.SaveConfig("acme", Site, ConfigFile.DeployScript, "x", true).Body!["auto_source"]!);
        Assert.False(Actions.SaveConfig("acme", Site, ConfigFile.Nginx, "x", true).Body!.ContainsKey("auto_source"));
    }

    [Fact]
    public void A_site_that_is_gone_cannot_be_saved()
    {
        var spec = Actions.SaveConfig("acme", null, ConfigFile.Nginx, "x");
        Assert.False(spec.Available);
        Assert.Equal("", spec.Path);
    }

    [Fact]
    public void Scope_messages_name_the_manage_scope()
    {
        Assert.Contains("site:manage-environment", ConfigFiles.ReadScopeMessage(ConfigFile.Environment));
        Assert.Contains("site:manage-deploys", ConfigFiles.WriteScopeMessage(ConfigFile.DeployScript));
        Assert.Contains("site:manage-nginx", ConfigFiles.WriteScopeMessage(ConfigFile.Nginx));
    }

    [Fact]
    public void A_config_document_is_read_exactly_as_sent()
    {
        var body = JsonNode.Parse("""{"data":{"attributes":{"content":"  A=1\r\nB=2\r\n\r\n","auto_source":true}}}""");
        var doc = Parse.ConfigDocument(body)!;
        Assert.Equal("  A=1\r\nB=2\r\n\r\n", doc.Content);
        Assert.True(doc.AutoSource);

        Assert.Equal("", Parse.ConfigDocument(JsonNode.Parse("""{"data":{"attributes":{"content":null}}}"""))!.Content);
        Assert.Null(Parse.ConfigDocument(JsonNode.Parse("""{"data":{"attributes":{}}}""")));
        Assert.Null(Parse.ConfigDocument(JsonNode.Parse("""{"data":{"attributes":{"content":"x"}}}"""))!.AutoSource);
    }
}

public class ConfigTextTests
{
    [Theory]
    [InlineData("A=1\rB=2\r", "A=1\nB=2\n", "A=1\nB=2\n")]
    [InlineData("A=1\rB=2\r", "A=1\r\nB=2\r\n", "A=1\r\nB=2\r\n")]
    [InlineData("A=1\r\nB=3\r\n", "A=1\nB=2\n", "A=1\nB=3\n")]
    [InlineData("A=1\nB=3", "A=1\r\nB=2", "A=1\r\nB=3")]
    [InlineData("", "A=1\n", "")]
    public void The_editor_gives_back_the_line_endings_forge_sent(string editor, string original, string expected) =>
        Assert.Equal(expected, ConfigText.FromEditor(editor, original));

    [Fact]
    public void An_unchanged_edit_round_trips_to_the_same_text()
    {
        const string lf = "APP_KEY=base64:x\n\n# comment\n";
        Assert.Equal(lf, ConfigText.FromEditor(lf.Replace('\n', '\r'), lf));
        const string crlf = "APP_KEY=base64:x\r\n\r\n# comment\r\n";
        Assert.Equal(crlf, ConfigText.FromEditor(crlf.Replace("\r\n", "\r"), crlf));
    }

    [Theory]
    [InlineData("DB_PASSWORD=hunter2", "DB_PASSWORD=••••••••")]
    [InlineData("export AWS_SECRET = \"abc def\"", "export AWS_SECRET =••••••••")]
    [InlineData("EMPTY=", "EMPTY=")]
    [InlineData("SPACES=   ", "SPACES=   ")]
    [InlineData("# PASSWORD=hunter2", "# PASSWORD=hunter2")]
    [InlineData("", "")]
    [InlineData("not an assignment", "not an assignment")]
    [InlineData("KEY=x\r", "KEY=••••••••\r")]
    public void Masking_hides_values_but_not_their_keys(string line, string expected) =>
        Assert.Equal(expected, ConfigText.MaskLine(line));

    [Fact]
    public void Masking_a_whole_file_keeps_its_lines_and_endings()
    {
        Assert.Equal("A=••••••••\r\n# c\r\nB=\r\n", ConfigText.MaskEnv("A=1\r\n# c\r\nB=\r\n"));
        Assert.Equal("A=••••••••\nB=••••••••", ConfigText.MaskEnv("A=a-very-long-secret-value\nB=x"));
    }

    private static string Show(IReadOnlyList<DiffLine> diff) => string.Join("\n", diff.Select(d => d.Kind switch
    {
        DiffKind.Same => "  " + d.Text,
        DiffKind.Added => "+ " + d.Text,
        DiffKind.Removed => "- " + d.Text,
        DiffKind.Gap => "… " + d.Text,
        _ => "! " + d.Text,
    }));

    [Fact]
    public void A_changed_line_shows_with_its_context_and_the_rest_folds()
    {
        var before = string.Join("\n", Enumerable.Range(1, 20).Select(i => $"L{i}")) + "\n";
        var after = before.Replace("L10\n", "L10 changed\n");
        Assert.Equal("""
            … 6 unchanged lines
              L7
              L8
              L9
            - L10
            + L10 changed
              L11
              L12
              L13
            … 7 unchanged lines
            """.ReplaceLineEndings("\n"), Show(ConfigText.Diff(before, after)));
    }

    [Fact]
    public void Insertions_and_deletions_line_up()
    {
        Assert.Equal("  a\n+ b\n  c", Show(ConfigText.Diff("a\nc\n", "a\nb\nc\n")));
        Assert.Equal("  a\n- b\n  c", Show(ConfigText.Diff("a\nb\nc\n", "a\nc\n")));
        Assert.Equal("+ a", Show(ConfigText.Diff("", "a\n")));
        Assert.Equal("- a\n- b", Show(ConfigText.Diff("a\nb", "")));
    }

    [Fact]
    public void No_change_is_an_empty_diff_and_endings_alone_say_so()
    {
        Assert.Empty(ConfigText.Diff("a\nb\n", "a\nb\n"));
        Assert.Equal("! Only the line endings change", Show(ConfigText.Diff("a\r\nb\n", "a\nb\n")));
        Assert.Equal("! Adds a newline at the end of the file", Show(ConfigText.Diff("a", "a\n")));
        Assert.Equal("  a\n- b\n+ c\n! Removes the newline at the end of the file", Show(ConfigText.Diff("a\nb\n", "a\nc")));
    }

    [Fact]
    public void An_environment_diff_masks_the_context_but_shows_the_change()
    {
        var diff = Show(ConfigText.Diff("A=1\nB=2\nC=3\n", "A=1\nB=two\nC=3\n", maskContext: true));
        Assert.Equal("  A=••••••••\n- B=2\n+ B=two\n  C=••••••••", diff);
    }

    [Fact]
    public void A_diff_too_large_to_match_shows_the_middle_replaced()
    {
        var before = string.Join("\n", Enumerable.Range(0, 2100).Select(i => "a" + i));
        var after = string.Join("\n", Enumerable.Range(0, 2100).Select(i => "b" + i));
        var diff = ConfigText.Diff(before, after, context: 0);
        Assert.Equal(2100, diff.Count(d => d.Kind == DiffKind.Removed));
        Assert.Equal(2100, diff.Count(d => d.Kind == DiffKind.Added));
        Assert.Equal(DiffKind.Removed, diff[0].Kind);
        Assert.Equal(DiffKind.Added, diff[^1].Kind);
    }
}

public class ConfigServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "forge-tests-" + Guid.NewGuid().ToString("N"));
    private readonly FakeClient _client = new();
    private readonly MemoryCredentialStore _creds = new();
    private readonly StateStore _store;
    private static readonly Site Site = new() { Id = "7", ServerId = "101", Name = "app.example.com" };
    private const string EnvPath = "/orgs/acme/servers/101/sites/7/environment";

    public ConfigServiceTests()
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

    private static ForgeEnvelope Doc(string content, bool? auto = null)
    {
        var attrs = new JsonObject { ["content"] = content };
        if (auto is { } a) attrs["auto_source"] = a;
        return FakeClient.Ok(new JsonObject { ["data"] = new JsonObject { ["attributes"] = attrs } });
    }

    [Fact]
    public async Task A_read_keeps_the_file_whole()
    {
        var big = string.Concat(Enumerable.Repeat("LINE=value\r\n", 60_000));
        _client.Route = (_, _) => Doc(big);
        var result = await NewService().ConfigFileAsync("acme", Site, ConfigFile.Environment);
        Assert.Equal(big, result.Value!.Content);
        Assert.Equal(("GET", EnvPath, (string?)null), Assert.Single(_client.Calls));
    }

    [Fact]
    public async Task A_refused_read_names_the_files_scope()
    {
        _client.Route = (_, _) => new ForgeEnvelope { Ok = false, Status = 403 };
        var result = await NewService().ConfigFileAsync("acme", Site, ConfigFile.Nginx);
        Assert.Contains("site:manage-nginx", result.Error);
    }

    [Fact]
    public async Task A_save_checks_puts_and_reads_back()
    {
        var held = "A=1\n";
        _client.Route = (method, _) =>
        {
            if (method == "PUT") { held = "A=2\n"; return new ForgeEnvelope { Ok = true, Status = 200 }; }
            return Doc(held);
        };
        var result = await NewService().SaveConfigAsync("acme", Site, ConfigFile.Environment, new("A=1\n"), "A=2\n");

        Assert.True(result.Ok);
        Assert.Equal("Environment file saved", result.Message);
        Assert.Equal("A=2\n", result.Now!.Content);
        Assert.Equal(["GET", "PUT", "GET"], _client.Calls.Select(c => c.Method));
        Assert.Equal(EnvPath, _client.Calls[1].Path);
        Assert.Equal(JsonNode.Parse("""{"environment":"A=2\n"}""")!.ToJsonString(), _client.Calls[1].Body);
    }

    [Fact]
    public async Task A_file_changed_elsewhere_is_not_overwritten()
    {
        _client.Route = (_, _) => Doc("A=from-the-website\n");
        var result = await NewService().SaveConfigAsync("acme", Site, ConfigFile.Environment, new("A=1\n"), "A=2\n");

        Assert.False(result.Ok);
        Assert.True(result.Conflict);
        Assert.Contains("changed in Forge", result.Message);
        Assert.Equal("A=from-the-website\n", result.Now!.Content);
        Assert.Equal(["GET"], _client.Calls.Select(c => c.Method));
    }

    [Fact]
    public async Task Nothing_changed_sends_nothing()
    {
        var result = await NewService().SaveConfigAsync("acme", Site, ConfigFile.Nginx, new("x"), "x");
        Assert.False(result.Ok);
        Assert.Empty(_client.Calls);
    }

    [Fact]
    public async Task A_refused_save_names_the_files_scope()
    {
        _client.Route = (method, _) => method == "PUT" ? new ForgeEnvelope { Ok = false, Status = 403 } : Doc("echo hi\n");
        var result = await NewService().SaveConfigAsync("acme", Site, ConfigFile.DeployScript, new("echo hi\n"), "echo bye\n");
        Assert.False(result.Ok);
        Assert.Contains("site:manage-deploys", result.Message);
        Assert.Equal(["GET", "PUT"], _client.Calls.Select(c => c.Method));
    }

    [Fact]
    public async Task A_422_carries_forges_own_message()
    {
        _client.Route = (method, _) => method == "PUT"
            ? new ForgeEnvelope { Ok = false, Status = 422, Body = new JsonObject { ["message"] = "The config is invalid." } }
            : Doc("server {}\n");
        var result = await NewService().SaveConfigAsync("acme", Site, ConfigFile.Nginx, new("server {}\n"), "server {\n");
        Assert.Equal("The config is invalid.", result.Message);
    }

    [Fact]
    public async Task The_deploy_scripts_auto_source_is_sent_back_as_forge_holds_it()
    {
        _client.Route = (method, _) => method == "PUT" ? new ForgeEnvelope { Ok = true, Status = 200 } : Doc("echo hi\n", true);
        await NewService().SaveConfigAsync("acme", Site, ConfigFile.DeployScript, new("echo hi\n", true), "echo bye\n");
        Assert.Equal("""{"content":"echo bye\n","auto_source":true}""", _client.Calls[1].Body);
    }
}
