using System.Collections.Concurrent;
using Discord;
using Discord.Interactions;

namespace DiscordBot;

// Base module with handy helpers
public abstract class AppModuleBase : InteractionModuleBase<SocketInteractionContext>
{
    protected ThunderstoreAPI Api { get; }
    protected Chunking Chunk { get; }

    protected AppModuleBase(ThunderstoreAPI api, Chunking chunk)
    {
        Api = api;
        Chunk = chunk;
    }

    protected async Task DeferIfNeeded(bool ephemeral = false)
    {
        if (!Context.Interaction.HasResponded)
            await DeferAsync(ephemeral: ephemeral);
    }

    protected async Task SendEmbedsPagedAsync(IEnumerable<Embed> embeds)
    {
        // First page as original response, rest as followups
        using var e = embeds.GetEnumerator();
        if (!e.MoveNext())
        {
            await FollowupAsync("No results.");
            return;
        }

        if (Context.Interaction.HasResponded)
            await FollowupAsync(embed: e.Current);
        else
            await RespondAsync(embed: e.Current);

        while (e.MoveNext())
            await FollowupAsync(embed: e.Current);
    }
}

public static class PagerStore
{
    // key -> pages
    public static ConcurrentDictionary<string, List<Embed>> Pages = new();
}

public class ThunderstoreSlash : AppModuleBase
{
    public ThunderstoreSlash(ThunderstoreAPI api, Chunking chunk) : base(api, chunk)
    {
    }

    [ComponentInteraction("pager:*:*:*")]
    public async Task PagerHandler(string key, int index, string action)
    {
        if (!PagerStore.Pages.TryGetValue(key, out var pages) || pages.Count == 0)
        {
            await RespondAsync("Pager expired.", ephemeral: true);
            return;
        }

        int newIndex = index;
        if (action == "prev") newIndex = Math.Max(0, index - 1);
        else if (action == "next") newIndex = Math.Min(pages.Count - 1, index + 1);
        else /* noop */ newIndex = index;

        var comps = BuildPagerComponents(key, newIndex, pages.Count);

        // Edit the message the buttons belong to
        await DeferAsync(); // keeps the interaction happy without a new message
        await ModifyOriginalResponseAsync(m =>
        {
            m.Embed = pages[newIndex];
            m.Components = comps;
        });
    }


    [SlashCommand("changelog", "Display a package CHANGELOG with pagination.")]
    public async Task Changelog(
        [Autocomplete(typeof(AuthorAutocomplete))] [Summary("author", "Author/owner")]
        string author,
        [Autocomplete(typeof(PackageAutocomplete))] [Summary("name", "Package name")]
        string name,
        [Autocomplete(typeof(VersionAutocomplete))] [Summary("version", "Exact version (optional)")]
        string version = "",
        [Summary("ephemeral", "Only you can see the response")]
        bool ephemeral = false)
    {
        await DeferIfNeeded(ephemeral);

        var res = await ThunderstoreAPI.GetChangelog(author, name, version);
        var body = string.IsNullOrWhiteSpace(res?.markdown) ? "_No CHANGELOG found._" : res.markdown;

        // Break into embed-sized chunks
        var embeds = Chunk.BuildDescriptionEmbedsWithHeader(
            $"{author}/{name} — CHANGELOG",
            string.IsNullOrWhiteSpace(version) ? "" : $"Version: `{version}`",
            body,
            Color.DarkGreen).ToList();

        if (embeds.Count == 0)
        {
            await FollowupAsync("No content.");
            return;
        }

        var key = Guid.NewGuid().ToString("N");
        PagerStore.Pages[key] = embeds;

        var comps = BuildPagerComponents(key, 0, embeds.Count);
        // First page as the original response
        if (Context.Interaction.HasResponded)
            await FollowupAsync(embed: embeds[0], components: comps);
        else
            await RespondAsync(embed: embeds[0], components: comps);
    }


    public enum ReadmeFormat
    {
        md,
        txt
    }

    [SlashCommand("readmefile", "Download a package README as a file.")]
    public async Task ReadmeFile(
        [Autocomplete(typeof(AuthorAutocomplete))] [Summary("author", "Author/owner")]
        string author,
        [Autocomplete(typeof(PackageAutocomplete))] [Summary("name", "Package name")]
        string name,
        [Autocomplete(typeof(VersionAutocomplete))] [Summary("version", "Exact version")]
        string version = "",
        [Summary("format", "md or txt")] ReadmeFormat format = ReadmeFormat.md,
        [Summary("ephemeral", "Only you can see the response")]
        bool ephemeral = false)
    {
        await DeferIfNeeded(ephemeral);

        var res = await ThunderstoreAPI.SearchPackages(author, name, version);
        var content = string.IsNullOrWhiteSpace(res?.markdown) ? "# README not found\n" : res.markdown;

        if (format == ReadmeFormat.txt)
            content = content.Replace("\r", "").Replace("\n", Environment.NewLine); // quick normalize

        string safe(string s) => string.Join("_", s.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries));
        var fileName = $"{safe(author)}-{safe(name)}{(string.IsNullOrWhiteSpace(version) ? "" : "-" + safe(version))}.readme.{format}";

        var bytes = System.Text.Encoding.UTF8.GetBytes(content);
        using var ms = new MemoryStream(bytes, writable: false);
        var file = new FileAttachment(ms, fileName);

        // If you've already responded with DeferAsync, use FollowupWithFileAsync
        await FollowupWithFileAsync(file, text: $"Here’s the README for **{author}/{name}**{(string.IsNullOrWhiteSpace(version) ? "" : $" `{version}`")}.");
    }

    // ThunderstoreSlash.cs (inside ThunderstoreSlash)
    [SlashCommand("readmesearch", "Search a mod's README and return highlighted excerpts.")]
    public async Task ReadmeSearchCmd(
        [Autocomplete(typeof(AuthorAutocomplete))] [Summary("author", "Author/owner")]
        string author,
        [Autocomplete(typeof(PackageAutocomplete))] [Summary("name", "Package name")]
        string name,
        [Summary("query", "Text to find in README")]
        string query,
        [Autocomplete(typeof(VersionAutocomplete))] [Summary("version", "Exact version (optional)")]
        string version = "",
        [Summary("context", "Lines of context around each hit (0-5)")]
        int context = 2,
        [Summary("max_matches", "Max results (1-20)")]
        int maxMatches = 10,
        [Summary("ephemeral", "Only you can see the response")]
        bool ephemeral = false,
        [Summary("attach", "Also attach a .md of matches")]
        bool attach = false)
    {
        context = Math.Clamp(context, 0, 5);
        maxMatches = Math.Clamp(maxMatches, 1, 20);

        await DeferIfNeeded(ephemeral);

        var md = await ThunderstoreAPI.GetReadmeMarkdown(author, name, string.IsNullOrWhiteSpace(version) ? null : version);
        if (string.IsNullOrWhiteSpace(md))
        {
            await FollowupAsync("README not found for that package/version.");
            return;
        }

        var hits = ReadmeSearch.Find(md!, query, context, maxMatches, caseInsensitive: true);
        if (hits.Count == 0)
        {
            await FollowupAsync($"No matches for `{query}` in {(string.IsNullOrWhiteSpace(version) ? "latest" : version)} README.");
            return;
        }


        var header = $"**{author}/{name}** — {(string.IsNullOrWhiteSpace(version) ? "latest" : version)}\nMatches for `{query}` (showing {hits.Count}):\n";

        // Each hit goes into a field; field values must be <= 1024.
        // Use the safety helper which also tidies code fences if they get cut.
        var parts = hits.Select(h => (
            Name: $"Line {h.LineNo}",
            Value: Chunking.TruncField(h.Excerpt, 1000),
            Inline: false
        ));

        // Build pages while respecting *all* embed limits (incl. 6000 total).
        var embeds = Chunk.BuildPagedEmbeds("README Search", header, parts, Color.DarkGreen);


        if (attach)
        {
            // Create a compact .md report for download
            var text = $"# README Search — {author}/{name} ({(string.IsNullOrWhiteSpace(version) ? "latest" : version)})\n" +
                       $"Query: {query}\n\n" +
                       string.Join("\n\n---\n\n", hits.Select(h => $"Line {h.LineNo}\n{h.Excerpt}"));

            var bytes = System.Text.Encoding.UTF8.GetBytes(text);
            using var ms = new MemoryStream(bytes, writable: false);
            var file = new FileAttachment(ms, $"{author}-{name}-readme-search.md");

            // first page + file
            using var enumer = embeds.GetEnumerator();
            if (!enumer.MoveNext())
            {
                await FollowupWithFileAsync(file, text: "No results.");
                return;
            }

            if (Context.Interaction.HasResponded)
                await FollowupWithFileAsync(file, embed: enumer.Current);
            else
                await RespondWithFileAsync(file, embed: enumer.Current);

            while (enumer.MoveNext())
                await FollowupAsync(embed: enumer.Current);
        }
        else
        {
            await SendEmbedsPagedAsync(embeds);
        }
    }


    [SlashCommand("readme", "Display a package README with pagination.")]
    public async Task Readme(
        [Autocomplete(typeof(AuthorAutocomplete))] [Summary("author", "Author/owner")]
        string author,
        [Autocomplete(typeof(PackageAutocomplete))] [Summary("name", "Package name")]
        string name,
        [Autocomplete(typeof(VersionAutocomplete))] [Summary("version", "Exact version (optional)")]
        string version = "",
        [Summary("ephemeral", "Only you can see the response")]
        bool ephemeral = false)
    {
        await DeferIfNeeded(ephemeral);

        var res = await ThunderstoreAPI.SearchPackages(author, name, version);
        var body = string.IsNullOrWhiteSpace(res?.markdown) ? "_No README found._" : res.markdown;

        // Break into embed-sized chunks
        var embeds = Chunk.BuildDescriptionEmbedsWithHeader(
            $"{author}/{name} — README",
            string.IsNullOrWhiteSpace(version) ? "" : $"Version: `{version}`",
            body,
            Color.DarkGreen).ToList();

        if (embeds.Count == 0)
        {
            await FollowupAsync("No content.");
            return;
        }

        var key = Guid.NewGuid().ToString("N");
        PagerStore.Pages[key] = embeds;

        var comps = BuildPagerComponents(key, 0, embeds.Count);
        // First page as the original response
        if (Context.Interaction.HasResponded)
            await FollowupAsync(embed: embeds[0], components: comps);
        else
            await RespondAsync(embed: embeds[0], components: comps);
    }

    private static MessageComponent BuildPagerComponents(string key, int index, int total)
    {
        var prevDisabled = index <= 0;
        var nextDisabled = index >= total - 1;

        var row = new ActionRowBuilder()
            .WithButton("⟨ Prev", $"pager:{key}:{index}:prev", ButtonStyle.Secondary, disabled: prevDisabled)
            .WithButton($"{index + 1}/{total}", $"pager:{key}:{index}:noop", ButtonStyle.Secondary, disabled: true)
            .WithButton("Next ⟩", $"pager:{key}:{index}:next", ButtonStyle.Secondary, disabled: nextDisabled);

        return new ComponentBuilder().AddRow(row).Build();
    }

    [SlashCommand("moddiff", "Compare two versions of a mod")]
    public async Task ModDiff([Autocomplete(typeof(AuthorAutocomplete))] string author, [Autocomplete(typeof(PackageAutocomplete))] string name, [Autocomplete(typeof(VersionAutocomplete))] string from, [Autocomplete(typeof(VersionAutocomplete))] string to, bool ephemeral = false)
    {
        await DeferAsync(ephemeral: ephemeral);
        var pkg = await Api.GetPackageInfo(author, name);
        var v1 = pkg?.latest is null
            ? null
            : (await ThunderstoreAPI.GetAllModsFromThunderstore())
            .FirstOrDefault(p => p.owner == author && p.name == name)?.versions?.FirstOrDefault(v => v.version_number == from);
        var v2 = (await ThunderstoreAPI.GetAllModsFromThunderstore())
            .First(p => p.owner == author && p.name == name).versions!.First(v => v.version_number == to);

        var added = v2.dependencies?.Except(v1?.dependencies ?? new()).ToList() ?? new();
        var removed = (v1?.dependencies ?? new()).Except(v2.dependencies ?? new()).ToList();
        var eb = new EmbedBuilder().WithTitle($"{author}/{name} — {from} → {to}")
            .AddField("Added deps", added.Count == 0 ? "—" : string.Join("\n", added), false)
            .AddField("Removed deps", removed.Count == 0 ? "—" : string.Join("\n", removed), false)
            .AddField("File size Δ", $"{(v2.file_size - (v1?.file_size ?? 0)):N0} bytes", true)
            .WithColor(Color.DarkTeal);
        await FollowupAsync(embed: eb.Build());
    }

    [SlashCommand("author_compare", "Compare two authors")]
    public async Task AuthorCompare(
        [Autocomplete(typeof(AuthorAutocomplete))] [Summary("FirstAuthor", "First author for the comparison")]
        string a,
        [Autocomplete(typeof(AuthorAutocomplete))] [Summary("SecondAuthor", "Second author for the comparison")]
        string b,
        [Summary("ephemeral", "Only you can see the response")]
        bool ephemeral = false)
    {
        await DeferIfNeeded(ephemeral);
        var all = await ThunderstoreAPI.GetAllModsFromThunderstore();
        var ag = all.Where(x => x.owner.Equals(a, StringComparison.OrdinalIgnoreCase)).ToList();
        var bg = all.Where(x => x.owner.Equals(b, StringComparison.OrdinalIgnoreCase)).ToList();
        if (ag.Count == 0 || bg.Count == 0)
        {
            await FollowupAsync("One or both authors not found.");
            return;
        }

        int sum(List<PackageInfo> g) => g.Sum(m => m.versions?.Sum(v => v.downloads) ?? 0);
        int med(List<PackageInfo> g) => (int)g.Select(m => m.versions?.Sum(v => v.downloads) ?? 0).OrderBy(x => x).DefaultIfEmpty().ElementAt(g.Count / 2);

        var ea = new EmbedBuilder().WithTitle($"Author: {a}")
            .AddField("Mods", ag.Count, true).AddField("Total DL", sum(ag).ToString("N0"), true)
            .AddField("Median DL", med(ag).ToString("N0"), true)
            .AddField("Top 5", string.Join("\n", ag.OrderByDescending(m => m.versions?.Sum(v => v.downloads) ?? 0).Take(5).Select(m => $"[{m.name}]({m.package_url})")), false)
            .WithColor(Color.DarkGreen).Build();

        var eb = new EmbedBuilder().WithTitle($"Author: {b}")
            .AddField("Mods", bg.Count, true).AddField("Total DL", sum(bg).ToString("N0"), true)
            .AddField("Median DL", med(bg).ToString("N0"), true)
            .AddField("Top 5", string.Join("\n", bg.OrderByDescending(m => m.versions?.Sum(v => v.downloads) ?? 0).Take(5).Select(m => $"[{m.name}]({m.package_url})")), false)
            .WithColor(Color.DarkBlue).Build();

        await SendEmbedsPagedAsync(new[] { ea, eb });
    }


    [SlashCommand("authorleaderboard", "Top authors by (median downloads × mod count).")]
    public async Task AuthorLeaderboard(
        [Summary("limit", "How many (1-25)")] int limit = 10,
        [Summary("ephemeral", "Only you can see the response")]
        bool ephemeral = false)
    {
        limit = Math.Clamp(limit, 1, 25);
        await DeferIfNeeded(ephemeral);

        var map = await ThunderstoreAPI.GetAuthorsWithAtLeastFiveMods();
        if (map == null || map.Count == 0)
        {
            await FollowupAsync("No authors meet the criteria.");
            return;
        }

        // Get the global median downloads from the first author (since it's the same on all authors)
        int currentHighest = map.First().Value.global_median_downloads;
        var fields = map
            .OrderByDescending(kv => kv.Value.medianDownloadsMultiplied)
            .Take(limit)
            .Select(kv => (
                Name: kv.Key,
                Value: $"• Mods: {kv.Value.mods_count:N0}\n• Median: {kv.Value.median_downloads:N0}\n• Median×Count: {kv.Value.medianDownloadsMultiplied:N0}",
                Inline: true
            ));

        var header = "Top authors with at least 5 mods:\n **Global Median Downloads:** " + currentHighest + "\n\n";

        var embeds = Chunk.BuildPagedEmbeds("Mod Author Leaderboard", header, fields, Color.Gold);
        await SendEmbedsPagedAsync(embeds);
    }

    [SlashCommand("authorstats", "Show author stats (downloads, medians, top mods).")]
    public async Task AuthorStats(
        [Autocomplete(typeof(AuthorAutocomplete))] [Summary("author", "Thunderstore author (owner) name")]
        string author,
        [Summary("top", "How many top mods to show (1-20)")]
        int top = 5,
        [Summary("ephemeral", "Only you can see the response")]
        bool ephemeral = false,
        [Summary("include_deprecated", "Include Deprecated mods?")]
        bool deprecated = true)
    {
        //top = Math.Clamp(top, 1, 20);
        await DeferIfNeeded(ephemeral);

        var stats = await DiscordBot.ThunderstoreAPI.GetAuthorStats(author);
        if (stats == null || stats.mods_count == 0)
        {
            await FollowupAsync("Author not found or has no mods.");
            return;
        }

        var topLines = stats.topModsSorted.Where(x => deprecated ? x.is_deprecated || !x.is_deprecated : !x.is_deprecated).Take(top).Select((m, i) =>
            $"{i + 1}. [{m.name}]({m.package_url}) — {m.versions?.Sum(v => v.downloads):N0} downloads");

        var desc =
            $"**Mods:** {stats.mods_count:N0}\n" +
            $"**Total Downloads:** {stats.total_downloads:N0}\n" +
            $"**Average:** {stats.average_downloads:N0}\n" +
            $"**Median:** {stats.median_downloads:N0}\n" +
            $"**Median × Count:** {stats.medianDownloadsMultiplied:N0}\n" +
            $"**Most Downloaded:** {stats.most_downloaded_mod}\n\n" +
            $"**Top Mods:**\n{string.Join("\n", topLines)}";

        var embeds = Chunk.BuildDescriptionEmbeds($"Stats for {author}", desc, Color.Gold);
        await SendEmbedsPagedAsync(embeds);
    }

    [SlashCommand("authortopmods", "List an author's top mods by downloads.")]
    public async Task AuthorTopMods(
        [Autocomplete(typeof(AuthorAutocomplete))] [Summary("author", "Thunderstore author")]
        string author,
        [Summary("limit", "How many results (1-50)")]
        int limit = 25,
        [Summary("include_modpacks", "Include modpacks?")]
        bool includeModpacks = false,
        [Summary("ephemeral", "Only you can see the response")]
        bool ephemeral = false)
    {
        limit = Math.Clamp(limit, 1, 50);
        await DeferIfNeeded(ephemeral);

        var mods = await ThunderstoreAPI.GetModsByAuthor(author);
        if (mods == null || mods.Count == 0)
        {
            await FollowupAsync("Author not found or has no mods.");
            return;
        }

        var filtered = mods
            .Where(m => includeModpacks
                        || (!m.name.Contains("modpack", StringComparison.OrdinalIgnoreCase) && !m.categories.Contains("Modpacks")))
            .OrderByDescending(m => m.versions?.Sum(v => v.downloads) ?? 0)
            .Take(limit)
            .Select(m => (
                Name: m.name,
                Value: $"Downloads: {(m.versions?.Sum(v => v.downloads) ?? 0):N0}\n[More Info]({m.package_url})",
                Inline: true
            ));

        var embeds = Chunk.BuildPagedEmbeds($"Top Mods by {author}", $"Limit: {limit} • Include Modpacks: {includeModpacks}", filtered, Color.Gold);
        await SendEmbedsPagedAsync(embeds);
    }

    [SlashCommand("search", "Search packages by author/name (wildcards ok).")]
    public async Task Search(
        [Summary("query", "Examples: azumatt/*  or  */craft*  or  azumatt/craft*")]
        string query,
        [Summary("limit", "How many results (1-50)")]
        int limit = 25,
        [Summary("include_modpacks", "Include modpacks?")]
        bool includeModpacks = false,
        [Summary("ephemeral", "Only you can see the response")]
        bool ephemeral = false)
    {
        limit = Math.Clamp(limit, 1, 50);
        await DeferIfNeeded(ephemeral);

        var all = await ThunderstoreAPI.GetAllModsFromThunderstore();
        if (all == null || all.Count == 0)
        {
            await FollowupAsync("No packages available.");
            return;
        }

        // simple wildcard split: author/name or single token
        string authorPat = "*", namePat = "*";
        if (query.Contains('/'))
        {
            var parts = query.Split('/', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            authorPat = parts.ElementAtOrDefault(0) ?? "*";
            namePat = parts.ElementAtOrDefault(1) ?? "*";
        }
        else
        {
            namePat = query;
        }

        bool Like(string text, string pat)
        {
            // very small wildcard matcher: * means any, case-insensitive
            var p = "^" + System.Text.RegularExpressions.Regex.Escape(pat).Replace("\\*", ".*") + "$";
            return System.Text.RegularExpressions.Regex.IsMatch(text ?? "", p, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        }

        var filtered = all
            .Where(m => includeModpacks || (!m.name.Contains("modpack", StringComparison.OrdinalIgnoreCase) && !m.categories.Contains("Modpacks")))
            .Where(m => Like(m.owner, authorPat) && Like(m.name, namePat))
            .Take(limit)
            .Select(m => (
                Name: $"{m.owner}/{m.name}",
                Value: $"Downloads: {m.versions?.Sum(v => v.downloads) ?? 0:N0}\n[More Info]({m.package_url})",
                Inline: true
            ));

        var embeds = Chunk.BuildPagedEmbeds("Search Results", $"Query: `{query}` • Limit: {limit}", filtered, Color.DarkGreen);
        await SendEmbedsPagedAsync(embeds);
    }

    [SlashCommand("authordeprecated", "List an author's deprecated mods.")]
    public async Task AuthorDeprecated(
        [Autocomplete(typeof(AuthorAutocomplete))] [Summary("author", "Thunderstore author")]
        string author,
        [Summary("ephemeral", "Only you can see the response")]
        bool ephemeral = false)
    {
        await DeferIfNeeded(ephemeral);

        var mods = await ThunderstoreAPI.GetModsByAuthor(author);
        var dep = mods?
            .Where(m => m.is_deprecated)
            .Select(m => (m.name, $"[More Info]({m.package_url})", true))
            .ToList();

        if (dep == null || dep.Count == 0)
        {
            await FollowupAsync("No deprecated mods found.");
            return;
        }

        var embeds = Chunk.BuildPagedEmbeds($"Deprecated by {author}", "Marked deprecated on Thunderstore", dep, Color.Red);
        await SendEmbedsPagedAsync(embeds);
    }

    [SlashCommand("depends", "List direct dependencies of a mod.")]
    public async Task Depends(
        [Autocomplete(typeof(AuthorAutocomplete))] [Summary("author", "Author/owner")]
        string author,
        [Autocomplete(typeof(PackageAutocomplete))] [Summary("name", "Package name")]
        string name,
        [Summary("ephemeral", "Only you can see the response")]
        bool ephemeral = false)
    {
        await DeferIfNeeded(ephemeral);

        var pkg = await Api.GetPackageInfo(author, name);
        if (string.IsNullOrWhiteSpace(pkg?.name))
        {
            await FollowupAsync("Package not found.");
            return;
        }

        var deps = pkg.latest?.dependencies ?? new();
        if (deps.Count == 0)
        {
            await FollowupAsync("This package has no dependencies.");
            return;
        }

        var text = string.Join("\n", deps);
        var embeds = Chunk.BuildDescriptionEmbeds($"{pkg.name} — Dependencies", text, Color.Purple);
        await SendEmbedsPagedAsync(embeds);
    }

    [SlashCommand("dependents", "List packages that depend on this package.")]
    public async Task Dependents(
        [Autocomplete(typeof(AuthorAutocomplete))] [Summary("author", "Author/owner")]
        string author,
        [Autocomplete(typeof(PackageAutocomplete))] [Summary("name", "Package name")]
        string name,
        [Summary("limit", "How many (1-50)")] int limit = 25,
        [Summary("ephemeral", "Only you can see the response")]
        bool ephemeral = false)
    {
        limit = Math.Clamp(limit, 1, 50);
        await DeferIfNeeded(ephemeral);

        var all = await ThunderstoreAPI.GetAllModsFromThunderstore();
        if (all == null || all.Count == 0)
        {
            await FollowupAsync("No packages available.");
            return;
        }

        // Dependency strings look like "Author-Name-x.y.z" — we match the prefix "Author-Name-"
        var prefix = $"{author}-{name}-";

        var rows = all
            .Where(m => m.versions?.Any(v => v.dependencies?.Any(d => d.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) == true) == true)
            .OrderByDescending(m => m.versions?.Sum(v => v.downloads) ?? 0)
            .Take(limit)
            .Select(m => (
                Name: $"{m.owner}/{m.name}",
                Value: $"Total Downloads: {m.versions?.Sum(v => v.downloads) ?? 0:N0}\n[More Info]({m.package_url})",
                Inline: true
            ));

        var embeds = Chunk.BuildPagedEmbeds($"Dependents of {author}/{name}", $"Limit: {limit}", rows, Color.Teal);
        await SendEmbedsPagedAsync(embeds);
    }

    [SlashCommand("rising", "New packages in the last N days, sorted by downloads.")]
    public async Task Rising(
        [Summary("days", "Window in days (1-90)")]
        int days = 30,
        [Summary("limit", "How many (1-50)")] int limit = 25,
        [Summary("include_modpacks", "Include modpacks?")]
        bool includeModpacks = false,
        [Summary("ephemeral", "Only you can see the response")]
        bool ephemeral = false)
    {
        days = Math.Clamp(days, 1, 90);
        limit = Math.Clamp(limit, 1, 50);
        await DeferIfNeeded(ephemeral);

        var since = DateTime.UtcNow.AddDays(-days);
        var mods = await ThunderstoreAPI.GetAllModsFromThunderstore();

        var rising = mods
            .Where(m => includeModpacks || (!m.name.Contains("modpack", StringComparison.OrdinalIgnoreCase) && !m.categories.Contains("Modpacks")))
            .Where(m => DateTime.TryParse(m.date_created, out var dc) && dc >= since)
            .OrderByDescending(m => m.versions?.Sum(v => v.downloads) ?? 0)
            .Take(limit)
            .Select(m => (m.name, $"Since created: {(m.versions?.Sum(v => v.downloads) ?? 0):N0} downloads\n[More Info]({m.package_url})", true));

        var embeds = Chunk.BuildPagedEmbeds($"Rising (last {days} days)", $"Limit: {limit} • Include Modpacks: {includeModpacks}", rising, Color.Orange);
        await SendEmbedsPagedAsync(embeds);
    }

    [SlashCommand("stale", "Packages not updated in the last N days.")]
    public async Task Stale(
        [Summary("days", "How many days (90-1000)")]
        int days = 365,
        [Summary("limit", "How many (1-50)")] int limit = 25,
        [Summary("include_modpacks", "Include modpacks?")]
        bool includeModpacks = false,
        [Summary("ephemeral", "Only you can see the response")]
        bool ephemeral = false)
    {
        days = Math.Clamp(days, 90, 1000);
        limit = Math.Clamp(limit, 1, 50);
        await DeferIfNeeded(ephemeral);

        var cutoff = DateTime.UtcNow.AddDays(-days);
        var mods = await ThunderstoreAPI.GetAllModsFromThunderstore();

        var stale = mods
            .Where(m => includeModpacks || (!m.name.Contains("modpack", StringComparison.OrdinalIgnoreCase) && !m.categories.Contains("Modpacks")))
            .Where(m => DateTime.TryParse(m.date_updated, out var du) && du < cutoff)
            .OrderByDescending(m => m.versions?.Sum(v => v.downloads) ?? 0)
            .Take(limit)
            .Select(m => (
                Name: m.name,
                Value: $"Last updated: {TryDate(m.date_updated)}\nTotal Downloads: {m.versions?.Sum(v => v.downloads) ?? 0:N0}\n[More Info]({m.package_url})",
                Inline: true));

        var embeds = Chunk.BuildPagedEmbeds($"Stale (>{days} days old)", $"Limit: {limit} • Include Modpacks: {includeModpacks}", stale, Color.DarkRed);
        await SendEmbedsPagedAsync(embeds);
    }


    [SlashCommand("popularmods", "Top mods by total downloads.")]
    public async Task PopularMods(
        [Summary("limit", "How many results (1-50)")]
        int limit = 10,
        [Summary("include_modpacks", "Include modpacks in results?")]
        bool includeModpacks = false,
        [Summary("ephemeral", "Only you can see the response")]
        bool ephemeral = false)
    {
        limit = Math.Clamp(limit, 1, 50);
        await DeferIfNeeded(ephemeral);

        var mods = await ThunderstoreAPI.GetAllModsFromThunderstore();
        var filtered = mods.Where(m => !m.is_pinned && (includeModpacks || (!m.name.Contains("modpack", StringComparison.OrdinalIgnoreCase) && !m.categories.Contains("Modpacks"))))
            .OrderByDescending(m => m.versions.Sum(v => v.downloads))
            .Take(limit)
            .ToList();

        if (filtered.Count == 0)
        {
            await FollowupAsync("No mods found.");
            return;
        }

        var fields = filtered.Select(m =>
            (Name: m.name,
                Value: $"Total Downloads: {m.versions.Sum(v => v.downloads):N0}\n[More Info]({m.package_url})",
                Inline: true));

        var embeds = Chunk.BuildPagedEmbeds("Most Popular Mods", $"Limit: {limit} • Include Modpacks: {includeModpacks}", fields, Color.Gold);
        await SendEmbedsPagedAsync(embeds);
    }

    [SlashCommand("modinfo", "Detailed package info by author/name.")]
    public async Task ModInfo(
        [Autocomplete(typeof(AuthorAutocomplete))] [Summary("author", "Author/owner name")]
        string author,
        [Autocomplete(typeof(PackageAutocomplete))] [Summary("name", "Package name")]
        string package,
        [Summary("ephemeral", "Only you can see the response")]
        bool ephemeral = false)
    {
        await DeferIfNeeded(ephemeral);

        var pkg = await Api.GetPackageInfo(author, package);
        if (string.IsNullOrWhiteSpace(pkg?.name))
        {
            await FollowupAsync("Package not found.");
            return;
        }

        var main = new EmbedBuilder()
            .WithTitle($"{pkg.name} by {pkg.owner}")
            .WithDescription(pkg.latest?.description ?? "")
            .AddField("Created", TryDate(pkg.date_created), true)
            .AddField("Updated", TryDate(pkg.date_updated), true)
            .AddField("Latest Version", pkg.latest?.version_number ?? "N/A", true)
            .AddField("Website", string.IsNullOrWhiteSpace(pkg.latest?.website_url) ? "—" : pkg.latest.website_url, true)
            .AddField("Thunderstore", $"https://thunderstore.io/c/valheim/p/{author}/{package}/")
            .WithThumbnailUrl(pkg.latest?.icon)
            .WithColor(Color.Purple)
            .Build();

        IEnumerable<Embed> embeds;
        if (pkg.latest?.dependencies != null && pkg.latest.dependencies.Count > 0)
        {
            var depsText = string.Join("\n", pkg.latest.dependencies);
            var depEmbeds = new List<Embed> { main };
            depEmbeds.AddRange(Chunk.BuildDescriptionEmbeds("Dependencies", depsText, Color.Purple));
            embeds = depEmbeds;
        }
        else
        {
            embeds = new[] { main };
        }

        await SendEmbedsPagedAsync(embeds);
    }

    [SlashCommand("toptoday", "Mods with highest download growth today.")]
    public async Task TopToday(
        [Summary("limit", "How many results (1-25)")]
        int limit = 10,
        [Summary("ephemeral", "Only you can see the response")]
        bool ephemeral = false)
    {
        limit = Math.Clamp(limit, 1, 25);
        await DeferIfNeeded(ephemeral);

        var mods = await ThunderstoreAPI.GetAllModsFromThunderstore();
        var since = DateTime.UtcNow.AddDays(-1);

        var recent = mods.Where(m => !m.is_pinned && !m.name.Contains("modpack", StringComparison.OrdinalIgnoreCase) && !m.categories.Contains("Modpacks"))
            .Select(m => new
            {
                m.name,
                m.package_url,
                Downloads = m.versions.Where(v => DateTime.Parse(v.date_created) > since).Sum(v => v.downloads)
            })
            .OrderByDescending(x => x.Downloads)
            .Take(limit)
            .ToList();

        if (recent.Count == 0)
        {
            await FollowupAsync("No mods with significant download growth today.");
            return;
        }

        var fields = recent.Select(x => (x.name, $"Downloads today: {x.Downloads:N0}\n[More Info]({x.package_url})", true));
        var embeds = Chunk.BuildPagedEmbeds("Top Download Growth (Today)", $"Since {since:u}", fields, Color.Purple);
        await SendEmbedsPagedAsync(embeds);
    }

    [SlashCommand("mostversions", "Packages with the most versions.")]
    public async Task MostVersions(
        [Summary("limit", "How many (1-25)")] int limit = 10,
        [Summary("include_modpacks", "Include modpacks?")]
        bool includeModpacks = false,
        [Summary("ephemeral", "Only you can see the response")]
        bool ephemeral = false)
    {
        limit = Math.Clamp(limit, 1, 25);
        await DeferIfNeeded(ephemeral);

        var mods = await ThunderstoreAPI.GetAllModsFromThunderstore();
        var list = mods
            .Where(m => includeModpacks || (!m.name.Contains("modpack", StringComparison.OrdinalIgnoreCase) && !(m.categories?.Contains("Modpacks") ?? false)))
            .OrderByDescending(m => m.versions?.Count ?? 0)
            .Take(limit)
            .Select(m => (m.name, $"Versions: {m.versions?.Count ?? 0}\n[More Info]({m.package_url})", true));

        var embeds = Chunk.BuildPagedEmbeds("Mod with Most Versions", $"Limit: {limit}", list, Color.Purple);
        await SendEmbedsPagedAsync(embeds);
    }

    [SlashCommand("recentdeprecated", "Recently active deprecated mods (ordered by update).")]
    public async Task RecentDeprecated(
        [Summary("limit", "How many (1-50)")] int limit = 25,
        [Summary("ephemeral", "Only you can see the response")]
        bool ephemeral = false)
    {
        limit = Math.Clamp(limit, 1, 50);
        await DeferIfNeeded(ephemeral);

        var mods = await ThunderstoreAPI.GetAllModsFromThunderstore();
        var list = mods
            .Where(m => m.is_deprecated)
            .OrderByDescending(m => DateTime.TryParse(m.date_updated, out var du) ? du : DateTime.MinValue)
            .Take(limit)
            .Select(m => (m.name, $"Updated: {TryDate(m.date_updated)} • [More Info]({m.package_url})", true));

        var embeds = Chunk.BuildPagedEmbeds("Deprecated Mods (Recently Updated)", $"Limit: {limit}", list, Color.DarkRed);
        await SendEmbedsPagedAsync(embeds);
    }

    [SlashCommand("authoractivity", "Activity summary for an author (last 12 months).")]
    public async Task AuthorActivity(
        [Autocomplete(typeof(AuthorAutocomplete))] [Summary("author", "Thunderstore author")]
        string author,
        [Summary("ephemeral", "Only you can see the response")]
        bool ephemeral = false)
    {
        await DeferIfNeeded(ephemeral);

        var mods = await ThunderstoreAPI.GetModsByAuthor(author) ?? new List<PackageInfo>();
        if (mods.Count == 0)
        {
            await FollowupAsync("Author not found or has no mods.");
            return;
        }

        var start = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1).AddMonths(-11);
        var months = Enumerable.Range(0, 12).Select(i => start.AddMonths(i)).ToList();

        int Upld(DateTime dt) => (dt >= start) ? 1 : 0;
        var uploads = new int[12];
        var updates = new int[12];

        foreach (var m in mods)
        {
            if (DateTime.TryParse(m.date_created, out var dc))
            {
                var idx = (dc.Year - start.Year) * 12 + (dc.Month - start.Month);
                if (idx is >= 0 and < 12) uploads[idx]++;
            }

            if (DateTime.TryParse(m.date_updated, out var du))
            {
                var idx = (du.Year - start.Year) * 12 + (du.Month - start.Month);
                if (idx is >= 0 and < 12) updates[idx]++;
            }
        }

        var lines = months.Select((m, i) => $"{m:yyyy-MM}: uploads {uploads[i]}, updates {updates[i]}");
        var desc = string.Join("\n", lines);

        var embeds = Chunk.BuildDescriptionEmbeds($"Activity for {author}", desc, Color.DarkGrey);
        await SendEmbedsPagedAsync(embeds);
    }

    [SlashCommand("modpackinspect", "Show a depth-limited dependency tree.")]
    public async Task ModpackInspect(
        [Autocomplete(typeof(AuthorAutocomplete))] [Summary("author", "Author/owner")]
        string author,
        [Autocomplete(typeof(PackageAutocomplete))] [Summary("name", "Package name")]
        string name,
        [Summary("depth", "Max depth (1-5)")] int depth = 2,
        [Summary("ephemeral", "Only you can see the response")]
        bool ephemeral = false)
    {
        depth = Math.Clamp(depth, 1, 5);
        await DeferIfNeeded(ephemeral);

        var all = await ThunderstoreAPI.GetAllModsFromThunderstore();
        var pkg = all.FirstOrDefault(p => p.owner.Equals(author, StringComparison.OrdinalIgnoreCase) && p.name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (pkg == null)
        {
            await FollowupAsync("Package not found.");
            return;
        }

        var latestDeps = pkg.versions?.FirstOrDefault()?.dependencies ?? new List<string>();
        if (latestDeps.Count == 0)
        {
            await FollowupAsync("This package has no dependencies.");
            return;
        }

        string MapKey(string owner, string nm) => $"{owner}/{nm}".ToLowerInvariant();
        var map = all.Distinct().ToDictionary(p => MapKey(p.owner, p.name), p => p, StringComparer.OrdinalIgnoreCase);

        (string owner, string pkg) ParseDep(string dep)
        {
            // "Author-Name-x.y.z" -> owner, name
            var parts = dep.Split('-', 3);
            if (parts.Length < 2) return ("", "");
            return (parts[0], parts[1]);
        }

        var sb = new System.Text.StringBuilder();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Walk(IEnumerable<string> deps, int d)
        {
            if (deps == null || d > depth) return;
            foreach (var dep in deps)
            {
                var (own, nm) = ParseDep(dep);
                if (string.IsNullOrWhiteSpace(own) || string.IsNullOrWhiteSpace(nm)) continue;
                var key = MapKey(own, nm);
                if (!visited.Add(key)) continue;

                sb.AppendLine($"{new string(' ', (d - 1) * 2)}• {own}/{nm}");
                if (map.TryGetValue(key, out var child))
                {
                    var childDeps = child.versions?.FirstOrDefault()?.dependencies ?? new List<string>();
                    Walk(childDeps, d + 1);
                }
            }
        }

        sb.AppendLine($"{author}/{name}");
        Walk(latestDeps, 1);

        var embeds = Chunk.BuildDescriptionEmbeds($"{author}/{name} — Dependencies (depth {depth})", sb.ToString(), Color.DarkTeal);
        await SendEmbedsPagedAsync(embeds);
    }

    [SlashCommand("nsfwmods", "List mods marked NSFW.")]
    public async Task NsfwMods(
        [Summary("limit", "How many (1-50)")] int limit = 25,
        [Summary("ephemeral", "Only you can see the response")]
        bool ephemeral = false)
    {
        limit = Math.Clamp(limit, 1, 50);
        await DeferIfNeeded(ephemeral);

        var mods = await ThunderstoreAPI.GetAllModsFromThunderstore();
        var list = mods
            .Where(m => m.has_nsfw_content)
            .OrderByDescending(m => m.versions?.Sum(v => v.downloads) ?? 0)
            .Take(limit)
            .Select(m => (m.name, $"[More Info]({m.package_url})", true));

        var embeds = Chunk.BuildPagedEmbeds("NSFW Mods", $"Limit: {limit}", list, Color.DarkRed);
        await SendEmbedsPagedAsync(embeds);
    }

    [SlashCommand("modsbycategory", "List mods within a category.")]
    public async Task ModsByCategory(
        [Summary("category", "Exact category name")]
        string category,
        [Summary("limit", "How many (1-50)")] int limit = 25,
        [Summary("include_modpacks", "Include modpacks?")]
        bool includeModpacks = false,
        [Summary("ephemeral", "Only you can see the response")]
        bool ephemeral = false)
    {
        limit = Math.Clamp(limit, 1, 50);
        await DeferIfNeeded(ephemeral);

        var mods = await ThunderstoreAPI.GetAllModsFromThunderstore();
        var list = mods
            .Where(m => includeModpacks || (!m.name.Contains("modpack", StringComparison.OrdinalIgnoreCase) && !(m.categories?.Contains("Modpacks") ?? false)))
            .Where(m => (m.categories ?? new List<string>()).Contains(category, StringComparer.OrdinalIgnoreCase))
            .OrderByDescending(m => m.versions?.Sum(v => v.downloads) ?? 0)
            .Take(limit)
            .Select(m => (m.name, $"Downloads: {m.versions?.Sum(v => v.downloads) ?? 0:N0}\n[More Info]({m.package_url})", true));

        var embeds = Chunk.BuildPagedEmbeds($"Mods in Category: {category}", $"Limit: {limit}", list, Color.Teal);
        await SendEmbedsPagedAsync(embeds);
    }

    private async Task SendEmbedsPagedEmbedsAsync(IEnumerable<Embed> embeds) => await SendEmbedsPagedAsync(embeds);


    private static string TryDate(string? iso) => DateTime.TryParse(iso, out var dt) ? dt.ToShortDateString() : "—";
}