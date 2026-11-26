using System.Net;
using System.Net.Http;

namespace Forge.App.Services;

/// <summary>
/// This machine's public address, for the firewall's "Look up my IP". It asks a third party
/// (api.ipify.org), so it runs only when the button is pressed, never on its own, and the
/// answer is neither logged nor kept: it only fills the box for the reader to check.
/// </summary>
internal static class PublicIp
{
    public const string Service = "api.ipify.org";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(5) };

    /// <summary>The address, or null with the reason it could not be found.</summary>
    public static async Task<(string? Ip, string? Error)> LookUpAsync(CancellationToken ct = default)
    {
        try
        {
            var text = (await Http.GetStringAsync("https://" + Service, ct).ConfigureAwait(false)).Trim();
            return IPAddress.TryParse(text, out var ip) ? (ip.ToString(), null) : (null, Service + " answered something that isn't an address");
        }
        catch (TaskCanceledException)
        {
            return (null, Service + " didn't answer in time");
        }
        catch (HttpRequestException e)
        {
            return (null, "Could not reach " + Service + ": " + e.Message);
        }
    }
}
