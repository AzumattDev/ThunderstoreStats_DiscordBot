// ThunderstoreCache.cs

namespace ThunderstoreStats_DiscordBot;

public sealed class ThunderstoreCache : IDisposable
{
    private readonly TimeSpan _refreshInterval;
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;

    // Snapshots (entire references swapped atomically)
    private List<PackageInfo> _packages = [];
    private List<string> _authors = []; // ordered by total downloads desc

    private Dictionary<string, List<string>> _modsByAuthor = new(StringComparer.OrdinalIgnoreCase); // author -> mod names (ordered)

    private Dictionary<string, List<string>> _versionsByKey = new(StringComparer.OrdinalIgnoreCase); // "author|mod" lower -> versions (newest first)

    // Lowercased mirrors for fast case-insensitive contains() without allocations
    private List<string> _authorsLower = [];

    private Dictionary<string, List<string>> _modsByAuthorLower = new(StringComparer.OrdinalIgnoreCase);

    private Dictionary<string, List<string>> _versionsByKeyLower = new(StringComparer.OrdinalIgnoreCase);

    private List<string> _categories = []; // ordered by popularity (count of packages)
    private List<string> _categoriesLower = []; // same order, lowercased for fast contains


    public ThunderstoreCache(TimeSpan? refreshInterval = null)
    {
        _refreshInterval = refreshInterval ?? TimeSpan.FromHours(1);
    }

    public void Start()
    {
        if (_loop != null) return;
        _loop = Task.Run(RefreshLoopAsync);
    }

    public void Dispose()
    {
        try
        {
            _cts.Cancel();
        }
        catch
        {
        }

        try
        {
            _loop?.Wait(TimeSpan.FromSeconds(2));
        }
        catch
        {
        }

        _cts.Dispose();
    }

    private async Task RefreshLoopAsync()
    {
        await SafeRefreshAsync("initial");

        while (!_cts.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_refreshInterval, _cts.Token);
            }
            catch (TaskCanceledException)
            {
                break;
            }

            await SafeRefreshAsync("periodic");
        }
    }

    private async Task SafeRefreshAsync(string reason)
    {
        try
        {
            Console.WriteLine($"[ThunderstoreCache] Refresh ({reason})…");
            List<PackageInfo> all = await ThunderstoreAPI.GetAllModsFromThunderstore();
            BuildIndexes(all);
            Console.WriteLine($"[ThunderstoreCache] Refresh complete: packages={all.Count}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ThunderstoreCache] Refresh failed: {ex}");
        }
    }

    private void BuildIndexes(List<PackageInfo> all)
    {
        List<PackageInfo> pkgs = all
            .Where(p => !string.IsNullOrWhiteSpace(p.owner) && !string.IsNullOrWhiteSpace(p.name))
            .ToList();

        // Authors ordered by total downloads
        var totals = pkgs
            .GroupBy(p => p.owner!, StringComparer.OrdinalIgnoreCase)
            .Select(g => new { Author = g.Key, Total = g.Sum(m => m.versions?.Sum(v => v.downloads) ?? 0) })
            .OrderByDescending(x => x.Total)
            .ToList();

        List<string> authors = totals.Select(x => x.Author).ToList();
        List<string> authorsLower = authors.Select(a => a.ToLowerInvariant()).ToList();

        // Mods per author (ordered by downloads desc)
        Dictionary<string, List<string>> modsByAuthor = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, List<string>> modsByAuthorLower = new(StringComparer.OrdinalIgnoreCase);

        foreach (IGrouping<string, PackageInfo> g in pkgs.GroupBy(p => p.owner!, StringComparer.OrdinalIgnoreCase))
        {
            List<string> ordered = g
                .OrderByDescending(p => p.versions?.Sum(v => v.downloads) ?? 0)
                .Select(p => p.name!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            modsByAuthor[g.Key] = ordered;
            modsByAuthorLower[g.Key.ToLowerInvariant()] = ordered.Select(s => s.ToLowerInvariant()).ToList();
        }

        // Versions per (author|mod), newest first
        Dictionary<string, List<string>> versionsByKey = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, List<string>> versionsByKeyLower = new(StringComparer.OrdinalIgnoreCase);

        foreach (PackageInfo p in pkgs)
        {
            string keyLower = $"{p.owner}|{p.name}".ToLowerInvariant();
            List<string> ordered = (p.versions ?? [])
                .OrderByDescending(v => DateTime.TryParse(v.date_created, out DateTime dt) ? dt : DateTime.MinValue)
                .ThenByDescending(v => v.version_number)
                .Select(v => v.version_number ?? "")
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .ToList();

            versionsByKey[keyLower] = ordered;
            versionsByKeyLower[keyLower] = ordered.Select(s => s.ToLowerInvariant()).ToList();
        }

        Dictionary<string, int> catCount = new(StringComparer.OrdinalIgnoreCase);
        foreach (PackageInfo p in pkgs)
        {
            List<string> cats = p.categories ?? [];
            foreach (string c in cats)
            {
                if (string.IsNullOrWhiteSpace(c)) continue;
                catCount[c] = catCount.TryGetValue(c, out int n) ? n + 1 : 1;
            }
        }

        List<string> categories = catCount
            .OrderByDescending(kv => kv.Value)
            .Select(kv => kv.Key)
            .ToList();
        List<string> categoriesLower = categories.Select(c => c.ToLowerInvariant()).ToList();


        // Atomic swaps (reference assignments are atomic)
        _packages = pkgs;
        _authors = authors;
        _authorsLower = authorsLower;
        _modsByAuthor = modsByAuthor;
        _modsByAuthorLower = modsByAuthorLower;
        _versionsByKey = versionsByKey;
        _versionsByKeyLower = versionsByKeyLower;
        _categories = categories;
        _categoriesLower = categoriesLower;
    }

    // ===== Fast suggestion helpers =====

    public IEnumerable<string> SuggestAuthors(string needle, int max = 20)
    {
        if (string.IsNullOrWhiteSpace(needle))
            return _authors.Take(max);

        string n = needle.ToLowerInvariant();
        List<string> outList = new(max);
        for (int i = 0; i < _authors.Count && outList.Count < max; ++i)
            if (_authorsLower[i].Contains(n))
                outList.Add(_authors[i]);
        return outList;
    }

    public IEnumerable<string> SuggestPackages(string? authorOrNull, string needle, int max = 20)
    {
        if (string.IsNullOrWhiteSpace(authorOrNull))
        {
            string n = needle?.ToLowerInvariant() ?? "";
            HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
            List<string> outList = new(max);
            foreach (PackageInfo p in _packages)
            {
                if (outList.Count >= max) break;
                string name = p.name!;
                if (!string.IsNullOrEmpty(n) && !name.ToLowerInvariant().Contains(n)) continue;
                if (seen.Add(name)) outList.Add(name);
            }

            return outList;
        }
        else
        {
            string aLower = authorOrNull.ToLowerInvariant();
            if (!_modsByAuthorLower.TryGetValue(aLower, out List<string>? modsLower))
                return [];

            List<string> display = _modsByAuthor.TryGetValue(authorOrNull, out List<string>? mods) ? mods : [];
            string n = needle?.ToLowerInvariant() ?? "";

            List<string> outList = new(max);
            for (int i = 0; i < display.Count && outList.Count < max; ++i)
                if (string.IsNullOrEmpty(n) || modsLower[i].Contains(n))
                    outList.Add(display[i]);
            return outList;
        }
    }

    public IEnumerable<string> SuggestVersions(string author, string name, string needle, int max = 20)
    {
        string keyLower = $"{author}|{name}".ToLowerInvariant();
        if (!_versionsByKeyLower.TryGetValue(keyLower, out List<string>? versLower))
            return [];

        List<string> display = _versionsByKey.TryGetValue(keyLower, out List<string>? v) ? v : [];
        string n = needle?.ToLowerInvariant() ?? "";

        List<string> outList = new(max);
        for (int i = 0; i < display.Count && outList.Count < max; ++i)
            if (string.IsNullOrEmpty(n) || versLower[i].Contains(n))
                outList.Add(display[i]);
        return outList;
    }

    public IEnumerable<string> SuggestCategories(string needle, bool includeModpacks, int max = 25)
    {
        string n = needle?.ToLowerInvariant() ?? "";
        List<string> outList = new(max);

        for (int i = 0; i < _categories.Count && outList.Count < max; ++i)
        {
            string cat = _categories[i];
            if (!includeModpacks && cat.Equals("Modpacks", StringComparison.OrdinalIgnoreCase))
                continue;
            if (string.IsNullOrEmpty(n) || _categoriesLower[i].Contains(n))
                outList.Add(cat);
        }

        return outList;
    }
}