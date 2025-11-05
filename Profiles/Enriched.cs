namespace ThunderstoreStats_DiscordBot.Profiles;

public readonly record struct EnrichedMod(
    string Author,
    string Name,
    string Display, // "Author-Name"
    string Version, // resolved (profile exact or latest)
    string? IconUrl,
    string? Description
)
{
    public override string ToString() => $"{Author}-{Name}-{Version}";
}

public static class ModResolver
{
    public static async Task<IReadOnlyList<EnrichedMod>> EnrichAsync(IEnumerable<ModRef> mods, string community = "valheim", CancellationToken ct = default)
    {
        List<ModRef> list = mods.ToList();
        if (list.Count == 0) return [];

        using ThunderstoreMetadataClient client = new(community);
        TS_Package[] all = await client.GetAllPackagesAsync(ct);

        // Build a de-duped index by "Author-Name" (case-insensitive)
        // Prefer the package that has MORE version entries; if equal, keep the first.
        Dictionary<string, TS_Package> byFull = new(StringComparer.OrdinalIgnoreCase);

        static string Full(TS_Package p)
        {
            string ns = (p.Namespace ?? "").Trim();
            string nm = (p.Name ?? "").Trim();
            string full = (p.Full_Name ?? $"{ns}-{nm}").Trim();
            return full;
        }

        foreach (TS_Package p in all)
        {
            string key = Full(p);
            if (string.IsNullOrWhiteSpace(key)) continue;

            if (!byFull.TryGetValue(key, out TS_Package? existing))
            {
                byFull[key] = p;
            }
            else
            {
                int existingCount = existing.Versions?.Length ?? 0;
                int candidateCount = p.Versions?.Length ?? 0;
                if (candidateCount > existingCount)
                    byFull[key] = p;
            }
        }

        List<EnrichedMod> result = new(list.Count);

        foreach (ModRef m in list)
        {
            string key = $"{m.Author}-{m.Name}";
            if (!byFull.TryGetValue(key, out TS_Package? pkg))
            {
                result.Add(new EnrichedMod(m.Author, m.Name, $"{m.Author}-{m.Name}", m.Version ?? "unknown", null, null));
                continue;
            }

            // Exact version if provided; otherwise pick a "latest"-ish version.
            TS_Version? ver = null;
            if (!string.IsNullOrWhiteSpace(m.Version))
            {
                ver = pkg.Versions.FirstOrDefault(v => string.Equals(v.Version_Number, m.Version, StringComparison.OrdinalIgnoreCase));
            }

            ver ??= pkg.Versions.OrderByDescending(v => ParseSemVerSafe(v.Version_Number)).FirstOrDefault();

            string resolvedVersion = ver?.Version_Number ?? (m.Version ?? "unknown");

            result.Add(new EnrichedMod(Author: m.Author, Name: m.Name, Display: (pkg.Full_Name ?? key), Version: resolvedVersion, IconUrl: ver?.Icon, Description: ver?.Description));
        }

        return result.OrderBy(x => x.Author).ThenBy(x => x.Name).ToArray();
    }

    private static (int major, int minor, int patch, string rest) ParseSemVerSafe(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return (0, 0, 0, string.Empty);
        string[] parts = s.Split('.', 4);
        int m = parts.Length > 0 && int.TryParse(parts[0], out int M) ? M : 0;
        int n = parts.Length > 1 && int.TryParse(parts[1], out int N) ? N : 0;
        int p = parts.Length > 2 && int.TryParse(parts[2], out int P) ? P : 0;
        string rest = parts.Length > 3 ? parts[3] : string.Empty;
        return (m, n, p, rest);
    }
}