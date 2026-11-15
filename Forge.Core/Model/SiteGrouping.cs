namespace Forge.Core.Model;

/// <summary>
/// The only three ways a server → sites map is built, side by side so the key
/// form cannot drift. Which one runs says how much of the organization the
/// caller has seen: all of it (group), one rotation window (merge), or exactly
/// one server (replace).
/// </summary>
public static class SiteGrouping
{
    public static List<Site> Sort(IEnumerable<Site> sites) =>
        sites.OrderBy(s => s.Name, ForgeText.Collation).ToList();

    /// <summary>The whole site list is known, so an empty server genuinely has no sites. The one publish allowed to delete.</summary>
    public static Dictionary<string, IReadOnlyList<Site>> Group(IEnumerable<Site> sites, IEnumerable<Server> servers)
    {
        var buckets = servers.ToDictionary(s => s.Id, _ => new List<Site>(), StringComparer.Ordinal);
        foreach (var site in Sort(sites))
            if (buckets.TryGetValue(site.ServerId, out var list)) list.Add(site);
        return buckets.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<Site>)kv.Value, StringComparer.Ordinal);
    }

    /// <summary>
    /// One window of a list that arrives in no useful order: observed sites
    /// update in place, every other site stays, and deletions wait for the wrap.
    /// </summary>
    public static Dictionary<string, IReadOnlyList<Site>> Merge(
        IReadOnlyDictionary<string, IReadOnlyList<Site>> previous, IEnumerable<Site> observed, IEnumerable<Server> servers)
    {
        var byServer = observed.GroupBy(s => s.ServerId).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        var result = new Dictionary<string, IReadOnlyList<Site>>(StringComparer.Ordinal);
        foreach (var server in servers)
        {
            var kept = previous.TryGetValue(server.Id, out var k) ? k : [];
            if (!byServer.TryGetValue(server.Id, out var fresh)) { result[server.Id] = kept; continue; }
            var byId = new Dictionary<string, Site>(StringComparer.Ordinal);
            foreach (var s in kept) byId[s.Id] = s;
            foreach (var s in fresh) byId[s.Id] = s;
            result[server.Id] = Sort(byId.Values);
        }
        return result;
    }

    /// <summary>The per-server endpoint is complete for exactly that server.</summary>
    public static Dictionary<string, IReadOnlyList<Site>> Replace(
        IReadOnlyDictionary<string, IReadOnlyList<Site>> previous, string serverId, IEnumerable<Site> sites, IEnumerable<Server> servers)
    {
        var result = new Dictionary<string, IReadOnlyList<Site>>(StringComparer.Ordinal);
        foreach (var server in servers)
            result[server.Id] = server.Id == serverId ? Sort(sites)
                : previous.TryGetValue(server.Id, out var k) ? k : [];
        return result;
    }

    /// <summary>The rotation's memory: everything observed since the walk started, later observation winning.</summary>
    public static List<Site> MergeSweep(IEnumerable<Site> accumulated, IEnumerable<Site> observed)
    {
        var byKey = new Dictionary<string, Site>(StringComparer.Ordinal);
        foreach (var s in accumulated.Concat(observed)) byKey[s.Key] = s;
        return byKey.Values.ToList();
    }

    public static int Count(IReadOnlyDictionary<string, IReadOnlyList<Site>> map) => map.Values.Sum(l => l.Count);
}
