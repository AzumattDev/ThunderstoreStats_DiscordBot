// ThunderstoreCache.cs

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DiscordBot;

public sealed class ThunderstoreCache : IDisposable
{
    private readonly TimeSpan _refreshInterval;
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;

    // Snapshots (entire references swapped atomically)
    private List<PackageInfo> _packages = new();
    private List<string> _authors = new(); // ordered by total downloads desc

    private Dictionary<string, List<string>> _modsByAuthor = new(StringComparer.OrdinalIgnoreCase); // author -> mod names (ordered)

    private Dictionary<string, List<string>> _versionsByKey = new(StringComparer.OrdinalIgnoreCase); // "author|mod" lower -> versions (newest first)

    // Lowercased mirrors for fast case-insensitive contains() without allocations
    private List<string> _authorsLower = new();

    private Dictionary<string, List<string>> _modsByAuthorLower = new(StringComparer.OrdinalIgnoreCase);

    private Dictionary<string, List<string>> _versionsByKeyLower = new(StringComparer.OrdinalIgnoreCase);

    private List<string> _categories = new(); // ordered by popularity (count of packages)
    private List<string> _categoriesLower = new(); // same order, lowercased for fast contains


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
            var all = await ThunderstoreAPI.GetAllModsFromThunderstore();
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
        var pkgs = all
            .Where(p => !string.IsNullOrWhiteSpace(p.owner) && !string.IsNullOrWhiteSpace(p.name))
            .ToList();

        // Authors ordered by total downloads
        var totals = pkgs
            .GroupBy(p => p.owner!, StringComparer.OrdinalIgnoreCase)
            .Select(g => new { Author = g.Key, Total = g.Sum(m => m.versions?.Sum(v => v.downloads) ?? 0) })
            .OrderByDescending(x => x.Total)
            .ToList();

        var authors = totals.Select(x => x.Author).ToList();
        var authorsLower = authors.Select(a => a.ToLowerInvariant()).ToList();

        // Mods per author (ordered by downloads desc)
        var modsByAuthor = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var modsByAuthorLower = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var g in pkgs.GroupBy(p => p.owner!, StringComparer.OrdinalIgnoreCase))
        {
            var ordered = g
                .OrderByDescending(p => p.versions?.Sum(v => v.downloads) ?? 0)
                .Select(p => p.name!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            modsByAuthor[g.Key] = ordered;
            modsByAuthorLower[g.Key.ToLowerInvariant()] = ordered.Select(s => s.ToLowerInvariant()).ToList();
        }

        // Versions per (author|mod), newest first
        var versionsByKey = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var versionsByKeyLower = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var p in pkgs)
        {
            var keyLower = $"{p.owner}|{p.name}".ToLowerInvariant();
            var ordered = (p.versions ?? new List<VersionInfo>())
                .OrderByDescending(v => DateTime.TryParse(v.date_created, out var dt) ? dt : DateTime.MinValue)
                .ThenByDescending(v => v.version_number)
                .Select(v => v.version_number ?? "")
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .ToList();

            versionsByKey[keyLower] = ordered;
            versionsByKeyLower[keyLower] = ordered.Select(s => s.ToLowerInvariant()).ToList();
        }

        var catCount = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in pkgs)
        {
            var cats = p.categories ?? new List<string>();
            foreach (var c in cats)
            {
                if (string.IsNullOrWhiteSpace(c)) continue;
                catCount[c] = catCount.TryGetValue(c, out var n) ? n + 1 : 1;
            }
        }

        var categories = catCount
            .OrderByDescending(kv => kv.Value)
            .Select(kv => kv.Key)
            .ToList();
        var categoriesLower = categories.Select(c => c.ToLowerInvariant()).ToList();


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

        var n = needle.ToLowerInvariant();
        var outList = new List<string>(max);
        for (int i = 0; i < _authors.Count && outList.Count < max; i++)
            if (_authorsLower[i].Contains(n))
                outList.Add(_authors[i]);
        return outList;
    }

    public IEnumerable<string> SuggestPackages(string? authorOrNull, string needle, int max = 20)
    {
        if (string.IsNullOrWhiteSpace(authorOrNull))
        {
            var n = needle?.ToLowerInvariant() ?? "";
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var outList = new List<string>(max);
            foreach (var p in _packages)
            {
                if (outList.Count >= max) break;
                var name = p.name!;
                if (!string.IsNullOrEmpty(n) && !name.ToLowerInvariant().Contains(n)) continue;
                if (seen.Add(name)) outList.Add(name);
            }

            return outList;
        }
        else
        {
            var aLower = authorOrNull.ToLowerInvariant();
            if (!_modsByAuthorLower.TryGetValue(aLower, out var modsLower))
                return Array.Empty<string>();

            var display = _modsByAuthor.TryGetValue(authorOrNull, out var mods) ? mods : new List<string>();
            var n = needle?.ToLowerInvariant() ?? "";

            var outList = new List<string>(max);
            for (int i = 0; i < display.Count && outList.Count < max; i++)
                if (string.IsNullOrEmpty(n) || modsLower[i].Contains(n))
                    outList.Add(display[i]);
            return outList;
        }
    }

    public IEnumerable<string> SuggestVersions(string author, string name, string needle, int max = 20)
    {
        var keyLower = $"{author}|{name}".ToLowerInvariant();
        if (!_versionsByKeyLower.TryGetValue(keyLower, out var versLower))
            return Array.Empty<string>();

        var display = _versionsByKey.TryGetValue(keyLower, out var v) ? v : new List<string>();
        var n = needle?.ToLowerInvariant() ?? "";

        var outList = new List<string>(max);
        for (int i = 0; i < display.Count && outList.Count < max; i++)
            if (string.IsNullOrEmpty(n) || versLower[i].Contains(n))
                outList.Add(display[i]);
        return outList;
    }

    public IEnumerable<string> SuggestCategories(string needle, bool includeModpacks, int max = 25)
    {
        var n = needle?.ToLowerInvariant() ?? "";
        var outList = new List<string>(max);

        for (int i = 0; i < _categories.Count && outList.Count < max; ++i)
        {
            var cat = _categories[i];
            if (!includeModpacks && cat.Equals("Modpacks", StringComparison.OrdinalIgnoreCase))
                continue;
            if (string.IsNullOrEmpty(n) || _categoriesLower[i].Contains(n))
                outList.Add(cat);
        }

        return outList;
    }
}