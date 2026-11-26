using System.Buffers.Binary;
using System.Text;
using System.Text.Json.Nodes;
using Forge.Core.Api;
using Forge.Core.Model;
using Forge.Core.Secrets;
using Forge.Core.Service;
using Forge.Core.Setup;

namespace Forge.Core.Tests;

internal static class TestKeys
{
    /// <summary>
    /// A well-formed OpenSSH public key line: the blob names <paramref name="type"/> (or <paramref name="blobType"/>)
    /// and carries the key fields that type holds.
    /// </summary>
    public static string Public(string type = "ssh-ed25519", string comment = "me@laptop", string? blobType = null) =>
        Line(type, Blob(blobType ?? type, FieldsOf(blobType ?? type)), comment);

    /// <summary>The fields after the name, as OpenSSH writes them for each type.</summary>
    public static byte[][] FieldsOf(string type) => type switch
    {
        "ssh-ed25519" => [Bytes(32)],
        "ssh-rsa" => [[0x01, 0x00, 0x01], [0x00, .. Bytes(256)]],
        "sk-ssh-ed25519@openssh.com" => [Bytes(32), "ssh:"u8.ToArray()],
        "sk-ecdsa-sha2-nistp256@openssh.com" => ["nistp256"u8.ToArray(), [0x04, .. Bytes(64)], "ssh:"u8.ToArray()],
        _ when type.StartsWith("ecdsa-sha2-", StringComparison.Ordinal) => [Encoding.ASCII.GetBytes(type["ecdsa-sha2-".Length..]), [0x04, .. Bytes(64)]],
        _ => [Bytes(32)],
    };

    /// <summary>A blob of length-prefixed fields: the name, then <paramref name="fields"/>.</summary>
    public static byte[] Blob(string name, params byte[][] fields)
    {
        var all = new[] { Encoding.ASCII.GetBytes(name) }.Concat(fields).ToList();
        var blob = new byte[all.Sum(f => 4 + f.Length)];
        var at = 0;
        foreach (var field in all)
        {
            BinaryPrimitives.WriteUInt32BigEndian(blob.AsSpan(at), (uint)field.Length);
            field.CopyTo(blob, at + 4);
            at += 4 + field.Length;
        }
        return blob;
    }

    public static string Line(string type, byte[] blob, string comment = "") =>
        $"{type} {Convert.ToBase64String(blob)}{(comment == "" ? "" : " " + comment)}";

    private static byte[] Bytes(int n) => Enumerable.Range(0, n).Select(i => (byte)(i * 7 + 1)).ToArray();
}

public class AccessModelTests
{
    private static readonly Server Server = new() { Id = "101", Name = "web-1", Ip = "203.0.113.10", SshPort = 22 };
    private static readonly Site Site = new() { Id = "7", ServerId = "101", Name = "app.example.com" };

    [Fact]
    public void Paths_sit_under_the_server_or_site_and_encode_their_segments()
    {
        Assert.Equal("/orgs/acme/servers/101/ssh-keys?page%5Bsize%5D=30", Paths.SshKeys("acme", "101"));
        Assert.Equal("/orgs/acme/servers/101/ssh-keys", Paths.SshKeyCreate("acme", "101"));
        Assert.Equal("/orgs/acme/servers/101/ssh-keys/5", Paths.SshKey("acme", "101", "5"));
        Assert.Equal("/orgs/acme/servers/101/key", Paths.ServerKey("acme", "101"));
        Assert.Equal("/orgs/acme/servers/101/firewall-rules?page%5Bsize%5D=30", Paths.FirewallRules("acme", "101"));
        Assert.Equal("/orgs/acme/servers/101/firewall-rules", Paths.FirewallRuleCreate("acme", "101"));
        Assert.Equal("/orgs/acme/servers/101/firewall-rules/9", Paths.FirewallRule("acme", "101", "9"));
        Assert.Equal("/orgs/acme/servers/101/sites/7/domains?page%5Bsize%5D=30", Paths.Domains("acme", "101", "7"));
        Assert.Equal("/orgs/acme/servers/101/sites/7/domains/3/certificates/active", Paths.ActiveCertificate("acme", "101", "7", "3"));
        Assert.Equal("/orgs/a%2Fb/servers/1%3F/ssh-keys/x%2Fy", Paths.SshKey("a/b", "1?", "x/y"));
        Assert.Equal("/orgs/a%2Fb/servers/1%3F/firewall-rules/%23", Paths.FirewallRule("a/b", "1?", "#"));
        Assert.Equal("/orgs/a/servers/1/sites/2/domains/x%2Fy/certificates/active", Paths.ActiveCertificate("a", "1", "2", "x/y"));
    }

    [Fact]
    public void Public_keys_are_accepted_by_type_and_blob()
    {
        Assert.Equal("", Actions.SshKeyProblem(TestKeys.Public()));
        Assert.Equal("", Actions.SshKeyProblem(TestKeys.Public(comment: "")));
        Assert.Equal("", Actions.SshKeyProblem("  " + TestKeys.Public("ssh-rsa") + "\n"));
        Assert.Equal("", Actions.SshKeyProblem(TestKeys.Public("ecdsa-sha2-nistp256")));
        Assert.Equal("", Actions.SshKeyProblem(TestKeys.Public("sk-ssh-ed25519@openssh.com")));
        Assert.Equal("", Actions.SshKeyProblem(TestKeys.Public("sk-ecdsa-sha2-nistp256@openssh.com")));
        Assert.Equal("", Actions.SshKeyProblem(TestKeys.Public("ecdsa-sha2-nistp521")));
    }

    /// <summary>A paste cut at a base64 boundary can still decode: the blob must hold the type's key fields, and nothing else.</summary>
    [Fact]
    public void A_blob_without_its_key_data_is_refused()
    {
        var ed = TestKeys.FieldsOf("ssh-ed25519");
        var rsa = TestKeys.FieldsOf("ssh-rsa");
        string Problem(string type, byte[] blob) => Actions.SshKeyProblem(TestKeys.Line(type, blob));

        // The review's repro: 00 00 00 0b "ssh-ed25519" and nothing after it.
        Assert.Contains("damaged", Actions.SshKeyProblem("ssh-ed25519 AAAAC3NzaC1lZDI1NTE5"));
        Assert.Contains("damaged", Problem("ssh-rsa", TestKeys.Blob("ssh-rsa", rsa[0])));
        Assert.Contains("damaged", Problem("ssh-ed25519", TestKeys.Blob("ssh-ed25519", ed[0][..31])));
        Assert.Contains("damaged", Problem("ssh-ed25519", TestKeys.Blob("ssh-ed25519", ed[0], [1])));
        Assert.Contains("damaged", Problem("ssh-ed25519", [.. TestKeys.Blob("ssh-ed25519", ed[0]), 0, 0]));
        Assert.Contains("damaged", Problem("ssh-rsa", TestKeys.Blob("ssh-rsa", rsa[0], [])));
        var full = TestKeys.Blob("ssh-rsa", rsa);
        Assert.Contains("damaged", Problem("ssh-rsa", full[..^9]));
    }

    [Theory]
    [InlineData("", "Paste a public key first")]
    [InlineData("-----BEGIN OPENSSH PRIVATE KEY-----\nb3BlbnNzaC1rZXktdjEAAAAA\n-----END OPENSSH PRIVATE KEY-----", "private key")]
    [InlineData("-----BEGIN RSA PRIVATE KEY-----", "private key")]
    [InlineData("ssh-foo AAAAB3NzaC1yc2E me@x", "isn't an OpenSSH public key")]
    [InlineData("ssh-ed25519", "isn't an OpenSSH public key")]
    [InlineData("ssh-ed25519 not*base64", "damaged")]
    [InlineData("ssh-ed25519 AAAA", "damaged")]
    public void Anything_but_one_public_key_is_refused(string text, string reason) =>
        Assert.Contains(reason, Actions.SshKeyProblem(text));

    [Fact]
    public void Two_keys_or_a_mismatched_blob_are_refused()
    {
        Assert.Contains("one line", Actions.SshKeyProblem(TestKeys.Public() + "\n" + TestKeys.Public()));
        Assert.Contains("damaged", Actions.SshKeyProblem(TestKeys.Public("ssh-rsa", blobType: "ssh-ed25519")));
        var cut = TestKeys.Public(comment: "");
        Assert.Contains("damaged", Actions.SshKeyProblem(cut[..20]));
    }

    [Fact]
    public void Key_comment_and_short_form_come_from_the_line()
    {
        var key = TestKeys.Public(comment: "me@laptop with spaces");
        Assert.Equal("me@laptop with spaces", Actions.SshKeyComment(key));
        Assert.Equal("", Actions.SshKeyComment(TestKeys.Public(comment: "")));
        Assert.StartsWith("ssh-ed25519 …", Actions.SshKeyShort(key));
        Assert.Equal("ssh-ed25519 …".Length + 12, Actions.SshKeyShort(key).Length);
    }

    [Theory]
    [InlineData("forge", "")]
    [InlineData("site_user-2", "")]
    [InlineData("", "Name the server user")]
    [InlineData("Forge", "isn't a server user")]
    [InlineData("9lives", "isn't a server user")]
    [InlineData("-x", "isn't a server user")]
    [InlineData("forge;rm", "isn't a server user")]
    public void Server_users_are_checked(string user, string problem)
    {
        if (problem == "") Assert.Equal("", Actions.SshUserProblem(user));
        else Assert.Contains(problem, Actions.SshUserProblem(user));
    }

    [Fact]
    public void Adding_a_key_posts_name_key_and_user_and_says_who_can_sign_in()
    {
        var key = TestKeys.Public();
        var spec = Actions.AddSshKey("acme", Server, " LAPTOP ", key + "\n", " forge ");
        Assert.True(spec.Available);
        Assert.Equal("POST", spec.Method);
        Assert.Equal("/orgs/acme/servers/101/ssh-keys", spec.Path);
        Assert.Equal("LAPTOP", Json.Str(spec.Body!["name"]));
        Assert.Equal(key, Json.Str(spec.Body!["key"]));
        Assert.Equal(3, spec.Body!.Count);
        Assert.Contains(spec.Body!, p => Json.Str(p.Value) == "forge");
        Assert.Equal(Actions.SshKeyCreateScopeMessage, spec.ScopeMessage);
        var (title, body, cta) = spec.Wording!.Value;
        Assert.Equal("Add this key to web-1?", title);
        Assert.Contains("as forge", body);
        Assert.Contains("(me@laptop)", body);
        Assert.Equal("Add key", cta);
    }

    [Fact]
    public void A_key_without_a_name_or_with_a_bad_user_is_not_available()
    {
        Assert.Equal("Give the key a name", Actions.AddSshKey("acme", Server, " ", TestKeys.Public(), "forge").Reason);
        Assert.Contains("server user", Actions.AddSshKey("acme", Server, "laptop", TestKeys.Public(), "").Reason);
        Assert.Contains("private key", Actions.AddSshKey("acme", Server, "laptop", "-----BEGIN OPENSSH PRIVATE KEY-----", "forge").Reason);
        Assert.Equal("not listed", Actions.AddSshKey("acme", null, "laptop", TestKeys.Public(), "forge").Reason);
        Assert.Equal("", Actions.AddSshKey("acme", null, "laptop", TestKeys.Public(), "forge").Path);
    }

    [Fact]
    public void Removing_a_key_deletes_it_by_id_and_names_it()
    {
        var key = new SshKey("5", "laptop", "forge", "installed", "Me", "");
        var spec = Actions.DeleteSshKey("acme", Server, key);
        Assert.Equal("DELETE", spec.Method);
        Assert.Equal("/orgs/acme/servers/101/ssh-keys/5", spec.Path);
        Assert.Equal("5", spec.ArmIntent);
        Assert.Equal(Actions.SshKeyDeleteScopeMessage, spec.ScopeMessage);
        Assert.Contains("\"laptop\"", spec.Wording!.Value.Title);
        Assert.Contains("web-1 as forge", spec.Wording!.Value.Body);
        Assert.False(Actions.DeleteSshKey("acme", Server, null).Available);
    }

    [Theory]
    [InlineData("22", "")]
    [InlineData(" 443 ", "")]
    [InlineData("8000:8010", "")]
    [InlineData("", "Type a port")]
    [InlineData("0", "1 to 65535")]
    [InlineData("65536", "1 to 65535")]
    [InlineData("+22", "1 to 65535")]
    [InlineData("ssh", "1 to 65535")]
    [InlineData("10:5", "lower port")]
    [InlineData("10:10", "lower port")]
    [InlineData("1:2:3", "range like")]
    public void Ports_are_one_port_or_a_range(string port, string problem)
    {
        if (problem == "") Assert.Equal("", Actions.FirewallPortProblem(port));
        else Assert.Contains(problem, Actions.FirewallPortProblem(port));
    }

    [Theory]
    [InlineData("", "")]
    [InlineData("203.0.113.7", "")]
    [InlineData("198.51.100.0/24", "")]
    [InlineData("203.0.113.7, 2001:db8::1 ,198.51.100.0/24", "")]
    [InlineData("999.1.1.1", "\"999.1.1.1\"")]
    [InlineData("example.com", "\"example.com\"")]
    [InlineData("203.0.113.7/99", "\"203.0.113.7/99\"")]
    [InlineData("2001:DB8::1", "")]
    [InlineData("fe80::1", "")]
    [InlineData("2001:db8::/32", "")]
    [InlineData("22", "\"22\"")]
    [InlineData("192.168.1", "\"192.168.1\"")]
    [InlineData("0x7f000001", "\"0x7f000001\"")]
    [InlineData("010.0.0.1", "\"010.0.0.1\"")]
    [InlineData("fe80::1%eth0", "\"fe80::1%eth0\"")]
    [InlineData("10.1/8", "\"10.1/8\"")]
    [InlineData("10.0.0.0/+8", "\"10.0.0.0/+8\"")]
    public void Addresses_are_ips_or_ranges(string ip, string problem)
    {
        if (problem == "") Assert.Equal("", Actions.FirewallAddressProblem(ip));
        else Assert.Contains(problem, Actions.FirewallAddressProblem(ip));
    }

    [Fact]
    public void Adding_a_rule_names_port_and_address_and_leaves_out_a_blank_address()
    {
        var any = Actions.AddFirewallRule("acme", Server, "Web", "443", " ", allow: true);
        Assert.True(any.Available);
        Assert.Equal("/orgs/acme/servers/101/firewall-rules", any.Path);
        Assert.Equal("""{"name":"Web","port":"443","type":"allow"}""", any.Body!.ToJsonString());
        Assert.Equal("Allow port 443 from any address?", any.Wording!.Value.Title);
        Assert.Equal(Actions.FirewallScopeMessage, any.ScopeMessage);

        var office = Actions.AddFirewallRule("acme", Server, "Office", "8000:8010", "203.0.113.7, 198.51.100.0/24", allow: true);
        Assert.Equal("203.0.113.7,198.51.100.0/24", Json.Str(office.Body!["ip_address"]));
        Assert.Equal("Allow ports 8000–8010 from 203.0.113.7, 198.51.100.0/24?", office.Wording!.Value.Title);
    }

    [Fact]
    public void A_deny_rule_on_the_ssh_port_warns_of_a_lock_out()
    {
        var deny = Actions.AddFirewallRule("acme", Server, "Block", "22", "", allow: false);
        Assert.Equal("deny", Json.Str(deny.Body!["type"]));
        Assert.Equal("Block port 22 from any address?", deny.Wording!.Value.Title);
        Assert.Contains("locked out", deny.Wording!.Value.Body);
        Assert.DoesNotContain("locked out", Actions.AddFirewallRule("acme", Server, "Block", "80", "", allow: false).Wording!.Value.Body);
    }

    [Fact]
    public void Invalid_rules_are_not_available()
    {
        Assert.Equal("Give the rule a name", Actions.AddFirewallRule("acme", Server, "", "22", "", true).Reason);
        Assert.Equal("Type a port", Actions.AddFirewallRule("acme", Server, "x", "", "", true).Reason);
        Assert.Contains("\"nope\"", Actions.AddFirewallRule("acme", Server, "x", "22", "nope", true).Reason);
    }

    [Fact]
    public void Deleting_a_rule_names_port_and_address_and_warns_when_it_is_the_ssh_port()
    {
        var ssh = new FirewallRule("9", "SSH", "22", "", "allow", "installed", "");
        var spec = Actions.DeleteFirewallRule("acme", Server, ssh);
        Assert.Equal("DELETE", spec.Method);
        Assert.Equal("/orgs/acme/servers/101/firewall-rules/9", spec.Path);
        Assert.Equal("Delete the rule that allows port 22 from any address?", spec.Wording!.Value.Title);
        Assert.Contains("locked out", spec.Wording!.Value.Body);

        var web = new FirewallRule("10", "Web", "443", "203.0.113.7", "allow", "installed", "");
        var webSpec = Actions.DeleteFirewallRule("acme", Server, web);
        Assert.Equal("Delete the rule that allows port 443 from 203.0.113.7?", webSpec.Wording!.Value.Title);
        Assert.DoesNotContain("locked out", webSpec.Wording!.Value.Body);

        var block = new FirewallRule("11", "Block", "22", "198.51.100.9", "deny", "installed", "");
        Assert.Equal("Delete the rule that blocks port 22 from 198.51.100.9?", Actions.DeleteFirewallRule("acme", Server, block).Wording!.Value.Title);
    }

    [Fact]
    public void The_ssh_port_is_the_servers_own()
    {
        var custom = Server with { SshPort = 2222 };
        Assert.True(Actions.FirewallCoversSsh(new FirewallRule("1", "", "22", "", "allow", "", ""), Server));
        Assert.False(Actions.FirewallCoversSsh(new FirewallRule("1", "", "22", "", "allow", "", ""), custom));
        Assert.True(Actions.FirewallCoversSsh(new FirewallRule("1", "", "2000:3000", "", "allow", "", ""), custom));
    }

    /// <summary>A rule the app can't read for sure still warns: blank is every port, and a list is checked part by part.</summary>
    [Theory]
    [InlineData("", true)]
    [InlineData(" ", true)]
    [InlineData("22,80", true)]
    [InlineData("80, 443", false)]
    [InlineData("80,20:30", true)]
    [InlineData("weird", true)]
    [InlineData("80,x", true)]
    [InlineData("1:2:3", true)]
    public void Unreadable_or_all_port_rules_count_as_ssh(string port, bool covers) =>
        Assert.Equal(covers, Actions.FirewallCoversSsh(port, Server));

    [Theory]
    [InlineData("22", "port 22")]
    [InlineData("8000:8010", "ports 8000–8010")]
    [InlineData("", "all ports")]
    [InlineData("22,80", "ports 22, 80")]
    public void Ports_read_as_text(string port, string text) => Assert.Equal(text, Actions.PortText(port));

    [Fact]
    public void Deleting_an_all_ports_allow_rule_warns_of_a_lock_out()
    {
        var spec = Actions.DeleteFirewallRule("acme", Server, new FirewallRule("4", "Any", "", "", "allow", "installed", ""));
        Assert.Equal("Delete the rule that allows all ports from any address?", spec.Wording!.Value.Title);
        Assert.Contains("locked out", spec.Wording!.Value.Body);
    }

    [Fact]
    public void Keys_rules_and_domains_parse_and_drop_rows_without_an_id()
    {
        var keys = Parse.SshKeys(JsonNode.Parse("""
            {"data":[
              {"id":5,"attributes":{"name":"laptop","user":"forge","status":"installed","created_by":"Me","created_at":"2026-11-20T10:00:00Z"}},
              {"attributes":{"name":"no id"}}]}
            """));
        Assert.Equal(new SshKey("5", "laptop", "forge", "installed", "Me", "2026-11-20T10:00:00Z"), Assert.Single(keys));

        var rules = Parse.FirewallRules(JsonNode.Parse("""
            {"data":[
              {"id":"3","attributes":{"name":"HTTPS","port":"443","ip_address":null,"type":"ALLOW","status":"installed"}},
              {"id":"1","attributes":{"name":"SSH","port":22,"ip_address":"","type":"allow","status":"installed"}},
              {"id":"2","attributes":{"name":"Range","port":"8000:8010","ip_address":"203.0.113.7","type":"deny","status":"installing"}},
              {"attributes":{"name":"no id","port":"1"}}]}
            """));
        Assert.Equal(["SSH", "HTTPS", "Range"], rules.Select(r => r.Name));
        Assert.Equal("22", rules[0].Port);
        Assert.Equal("allow", rules[1].Type);
        Assert.Equal("", rules[1].IpAddress);

        var domains = Parse.Domains(JsonNode.Parse("""
            {"data":[
              {"id":"2","attributes":{"name":"www.example.com","status":"enabled","type":"alias"}},
              {"id":"1","attributes":{"name":"example.com","status":"enabled","type":"primary"}},
              {"id":"3","attributes":{"name":""}}]}
            """));
        Assert.Equal(["example.com", "www.example.com"], domains.Select(d => d.Name));
    }

    [Fact]
    public void The_active_certificate_parses_or_is_null()
    {
        var cert = Parse.ActiveCertificate(JsonNode.Parse("""
            {"data":{"id":"8","attributes":{"type":"letsencrypt","status":"installed","request_status":"created","key_type":"ecdsa","active":true,"expires_at":"2027-01-10T00:00:00Z"}}}
            """));
        Assert.Equal(new Certificate("8", "letsencrypt", "installed", "created", "ecdsa", true, "2027-01-10T00:00:00Z"), cert);
        Assert.Null(Parse.ActiveCertificate(JsonNode.Parse("""{"data":null}""")));
        Assert.Null(Parse.ActiveCertificate(JsonNode.Parse("""{"data":[]}""")));
        Assert.Equal("", Parse.ActiveCertificate(JsonNode.Parse("""{"data":{"id":"8","attributes":{"status":"installed"}}}"""))!.ExpiresAt);
    }

    private static readonly SiteDomain Domain = new("1", "example.com", "enabled", "primary");
    private static readonly long Now = DateTimeOffset.Parse("2026-11-26T12:00:00Z").ToUnixTimeMilliseconds();

    private static DomainCertificate Row(string status = "installed", string request = "created", bool active = true, string expires = "") =>
        new(Domain, new Certificate("8", "letsencrypt", status, request, "ecdsa", active, expires), "");

    [Fact]
    public void A_failed_request_is_bad_and_comes_first()
    {
        Assert.Equal(("request failed", Tone.Bad), Status.CertificateStatus(Row(request: "failed"), Now));
        Assert.Equal(Tone.Bad, Status.CertificateStatus(Row(request: "verification_failed"), Now).Tone);
        Assert.Equal(Tone.Bad, Status.CertificateStatus(Row(status: "failed"), Now).Tone);
        Assert.Equal(Tone.Busy, Status.CertificateStatus(Row(request: "pending"), Now).Tone);
        Assert.Equal(Tone.Busy, Status.CertificateStatus(Row(status: "installing"), Now).Tone);
        Assert.Equal(("installed", Tone.Ok), Status.CertificateStatus(Row(), Now));
        Assert.True(Status.CertificateRank(Row(request: "failed"), Now) < Status.CertificateRank(Row(), Now));
    }

    [Fact]
    public void Expiry_is_judged_only_when_forge_reports_it()
    {
        Assert.Equal(("expires in 5 days", Tone.Warn), Status.CertificateStatus(Row(expires: "2026-12-01T06:00:00Z"), Now));
        Assert.Equal(("expired", Tone.Bad), Status.CertificateStatus(Row(expires: "2026-11-01T00:00:00Z"), Now));
        Assert.Equal(Tone.Ok, Status.CertificateStatus(Row(expires: "2027-02-01T00:00:00Z"), Now).Tone);
        Assert.Equal(Tone.Ok, Status.CertificateStatus(Row(expires: ""), Now).Tone);
    }

    [Fact]
    public void A_domain_without_a_certificate_or_a_failed_read_is_quiet()
    {
        Assert.Equal(("no certificate", Tone.Idle), Status.CertificateStatus(new DomainCertificate(Domain, null, ""), Now));
        Assert.Equal(("Forge rejected the token", Tone.Idle), Status.CertificateStatus(new DomainCertificate(Domain, null, "Forge rejected the token"), Now));
        Assert.Equal("Let's Encrypt · ECDSA", Status.CertificateDetail(Row().Certificate));
        Assert.Equal("", Status.CertificateDetail(null));
    }

    [Fact]
    public void Access_statuses_read_as_tones()
    {
        Assert.Equal(("installed", Tone.Ok), Status.AccessStatus("installed"));
        Assert.Equal(("installing", Tone.Busy), Status.AccessStatus("installing"));
        Assert.Equal(("removing", Tone.Busy), Status.AccessStatus("removing"));
        Assert.Equal(Tone.Bad, Status.AccessStatus("failed").Tone);
        Assert.Equal(("", Tone.Idle), Status.AccessStatus(""));
    }

    [Fact]
    public void Flyout_rows_open_the_window_tabs()
    {
        Assert.Contains(Actions.ServerActions("acme", Server), a => a.Id == "ssh-keys" && a.Available && !a.Armable);
        Assert.Contains(Actions.ServerActions("acme", Server), a => a.Id == "firewall" && a.Available && !a.Armable);
        Assert.Contains(Actions.SiteActions("acme", Site), a => a.Id == "ssl" && a.Available && !a.Armable);
    }
}

public class LocalSshKeysTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "forge-ssh-" + Guid.NewGuid().ToString("N"));

    public LocalSshKeysTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private void Put(string name, string text) => File.WriteAllText(Path.Combine(_dir, name), text);

    [Fact]
    public void Only_valid_pub_files_at_the_top_of_the_folder_are_offered()
    {
        Put("id_ed25519", "-----BEGIN OPENSSH PRIVATE KEY-----\nsecret\n-----END OPENSSH PRIVATE KEY-----\n");
        Put("id_ed25519.pub", TestKeys.Public(comment: "me@laptop") + "\n");
        Put("id_rsa.pub", TestKeys.Public("ssh-rsa", comment: "") + "\n");
        Put("known_hosts", "example.com " + TestKeys.Public());
        Put("config", "Host *\n");
        Put("bogus.pub", "not a key");
        Put("leaked.pub", "-----BEGIN OPENSSH PRIVATE KEY-----\nsecret\n");
        Put("id_ed25519.pubx", TestKeys.Public());
        Directory.CreateDirectory(Path.Combine(_dir, "nested"));
        File.WriteAllText(Path.Combine(_dir, "nested", "other.pub"), TestKeys.Public());

        var found = LocalSshKeys.Find(_dir);

        Assert.Equal(["id_ed25519.pub", "id_rsa.pub"], found.Select(k => k.FileName));
        Assert.Equal("me@laptop", found[0].Comment);
        Assert.DoesNotContain(found, k => k.Key.Contains("PRIVATE", StringComparison.Ordinal));
    }

    [Fact]
    public void A_chosen_pub_file_is_read_and_anything_else_refused()
    {
        Put("work.pub", TestKeys.Public(comment: "me@work") + "\r\n");
        Put("id_ed25519", "-----BEGIN OPENSSH PRIVATE KEY-----\nsecret\n");
        Put("leaked.pub", "-----BEGIN OPENSSH PRIVATE KEY-----\nsecret\n");
        Put("notes.pub", "hello");

        var (key, problem) = LocalSshKeys.Read(Path.Combine(_dir, "work.pub"));
        Assert.Equal("", problem);
        Assert.Equal(new LocalSshKey("work.pub", TestKeys.Public(comment: "me@work"), "me@work"), key);

        Assert.Contains(".pub", LocalSshKeys.Read(Path.Combine(_dir, "id_ed25519")).Problem);
        Assert.Contains("private key", LocalSshKeys.Read(Path.Combine(_dir, "leaked.pub")).Problem);
        Assert.Contains("isn't an OpenSSH public key", LocalSshKeys.Read(Path.Combine(_dir, "notes.pub")).Problem);
        Assert.Equal("That file is gone", LocalSshKeys.Read(Path.Combine(_dir, "missing.pub")).Problem);
    }

    [Fact]
    public void A_symlinked_pub_file_is_refused()
    {
        Put("real.pub", TestKeys.Public());
        var link = Path.Combine(_dir, "link.pub");
        try { File.CreateSymbolicLink(link, Path.Combine(_dir, "real.pub")); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return; } // no symlink privilege here
        Assert.Contains("link", LocalSshKeys.Read(link).Problem);
        Assert.Equal("", LocalSshKeys.Read(Path.Combine(_dir, "real.pub")).Problem);
    }

    [Fact]
    public void A_missing_folder_offers_nothing()
    {
        Assert.Empty(LocalSshKeys.Find(Path.Combine(_dir, "missing")));
    }

    [Fact]
    public void An_oversized_pub_file_is_skipped()
    {
        Put("huge.pub", TestKeys.Public() + new string(' ', 20_000));
        Assert.Empty(LocalSshKeys.Find(_dir));
    }
}

public class AccessServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "forge-tests-" + Guid.NewGuid().ToString("N"));
    private readonly FakeClient _client = new();
    private readonly MemoryCredentialStore _creds = new();
    private readonly StateStore _store;
    private static readonly Site Site = new() { Id = "7", ServerId = "101", Name = "app.example.com" };
    private static readonly SiteDomain Domain = new("3", "app.example.com", "enabled", "primary");

    public AccessServiceTests()
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

    [Fact]
    public async Task A_domain_with_no_certificate_is_not_an_error()
    {
        _client.Route = (_, _) => new ForgeEnvelope { Ok = false, Status = 404 };
        var result = await NewService().ActiveCertificateAsync("acme", Site, Domain);
        Assert.True(result.Ok);
        Assert.Null(result.Value);
        Assert.Equal(("GET", "/orgs/acme/servers/101/sites/7/domains/3/certificates/active", (string?)null), Assert.Single(_client.Calls));
    }

    [Fact]
    public async Task A_404_elsewhere_is_still_an_error()
    {
        _client.Route = (_, _) => new ForgeEnvelope { Ok = false, Status = 404 };
        Assert.False((await NewService().SshKeysAsync("acme", "101")).Ok);
    }

    [Fact]
    public async Task The_server_key_is_read_from_public_key()
    {
        _client.Route = (_, _) => FakeClient.Ok(new JsonObject
        {
            ["data"] = new JsonObject { ["id"] = "101", ["attributes"] = new JsonObject { ["public_key"] = "ssh-rsa AAAA worker@web-1\n" } },
        });
        var result = await NewService().ServerKeyAsync("acme", "101");
        Assert.Equal("ssh-rsa AAAA worker@web-1", result.Value);
        Assert.Equal("/orgs/acme/servers/101/key", Assert.Single(_client.Calls).Path);
    }

    [Fact]
    public async Task Refused_reads_name_their_scopes()
    {
        _client.Route = (_, _) => new ForgeEnvelope { Ok = false, Status = 403 };
        var service = NewService();
        Assert.Equal(Actions.SshKeyReadScopeMessage, (await service.SshKeysAsync("acme", "101")).Error);
        Assert.Equal(Actions.FirewallReadScopeMessage, (await service.FirewallRulesAsync("acme", "101")).Error);
        Assert.Equal(Actions.CertificateReadScopeMessage, (await service.DomainsAsync("acme", Site)).Error);
        Assert.Equal(Actions.CertificateReadScopeMessage, (await service.ActiveCertificateAsync("acme", Site, Domain)).Error);
    }

    /// <summary>The SSL tab stops its batch on a refusal or a held bucket, and goes on past any other error.</summary>
    [Fact]
    public async Task Certificate_reads_say_whether_the_batch_should_stop()
    {
        var service = NewService();

        _client.Route = (_, _) => new ForgeEnvelope { Ok = false, Status = 403 };
        var refused = await service.ActiveCertificateAsync("acme", Site, Domain);
        Assert.True(refused.Fatal);
        Assert.False(refused.Throttled);

        _client.Route = (_, _) => new ForgeEnvelope { Ok = false, Status = 500 };
        var broken = await service.ActiveCertificateAsync("acme", Site, Domain);
        Assert.False(broken.Ok);
        Assert.False(broken.Fatal || broken.Throttled);

        // A 429 holds the bucket: the next read isn't sent, and says it was held back.
        _client.Route = (_, _) => new ForgeEnvelope { Ok = false, Status = 429, RateResetSec = 30 };
        await service.ActiveCertificateAsync("acme", Site, Domain);
        var calls = _client.Calls.Count;
        var held = await service.ActiveCertificateAsync("acme", Site, Domain);
        Assert.True(held.Throttled);
        Assert.False(held.Fatal);
        Assert.Equal(calls, _client.Calls.Count);
        Assert.True((await service.ServerKeyAsync("acme", "101")).Throttled);
        Assert.True((await service.DomainsAsync("acme", Site)).Throttled);
    }

    [Fact]
    public async Task A_refused_firewall_write_names_its_scope_and_stays_on_the_row()
    {
        _client.Route = (_, _) => new ForgeEnvelope { Ok = false, Status = 403 };
        var service = NewService();
        var server = new Server { Id = "101", Name = "web-1" };
        var outcome = await service.SendActionAsync("acme", "101", Actions.AddFirewallRule("acme", server, "Web", "443", "", true));
        Assert.Equal(Actions.FirewallScopeMessage, outcome.Message);
        Assert.Equal("", service.Snapshot.Org("acme")!.LastError);
        Assert.Equal(("POST", "/orgs/acme/servers/101/firewall-rules", """{"name":"Web","port":"443","type":"allow"}"""), _client.Calls[0]);
    }
}
