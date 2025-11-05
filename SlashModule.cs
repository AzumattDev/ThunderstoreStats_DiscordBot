using System.Collections.Concurrent;
using System.Text;
using Discord;
using Discord.Interactions;
using Discord.WebSocket;

namespace ThunderstoreStats_DiscordBot;

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
        using IEnumerator<Embed> e = embeds.GetEnumerator();
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

    protected internal async Task SendEmbedsOptionallyPagedAsync(IEnumerable<Embed> embeds, bool paginate = true)
    {
        List<Embed> pages = embeds?.ToList() ?? [];
        if (pages.Count == 0)
        {
            await FollowupAsync("No results.");
            return;
        }

        // If only one page, buttons aren’t helpful.
        if (!paginate || pages.Count == 1)
        {
            await SendEmbedsPagedAsync(pages);
            return;
        }

        string key = Guid.NewGuid().ToString("N");
        PagerStore.Pages[key] = pages;

        MessageComponent comps = ThunderstoreSlash.BuildPagerComponents(key, 0, pages.Count);
        if (Context.Interaction.HasResponded)
            await FollowupAsync(embed: pages[0], components: comps);
        else
            await RespondAsync(embed: pages[0], components: comps);
    }
}

public static class PagerStore
{
    // key -> pages
    public static ConcurrentDictionary<string, IReadOnlyList<Embed>> Pages = new();
}

public class ThunderstoreSlash(ThunderstoreAPI api, Chunking chunk) : AppModuleBase(api, chunk)
{
    [ComponentInteraction("pager:*:*:*")]
    public async Task PagerHandler(string key, int index, string action)
    {
        if (!PagerStore.Pages.TryGetValue(key, out IReadOnlyList<Embed>? pages) || pages.Count == 0)
        {
            await RespondAsync("Pager expired or error in changing page, try again.", ephemeral: true);
            return;
        }

        int newIndex = action switch
        {
            "prev" => Math.Max(0, index - 1),
            "next" => Math.Min(pages.Count - 1, index + 1),
            _ => index
        };

        MessageComponent comps = BuildPagerComponents(key, newIndex, pages.Count);
        await DeferAsync(); // keeps the interaction happy without a new message
        await ModifyOriginalResponseAsync(m =>
        {
            m.Embed = pages[newIndex];
            m.Components = comps;
        });
    }


    [SlashCommand("diag_here", "Diagnose why slash commands may be hidden or failing in this channel.")]
    [CommandContextType(InteractionContextType.Guild, InteractionContextType.PrivateChannel)]
    [DefaultMemberPermissions(GuildPermission.Administrator)]
    public async Task DiagHere([Summary("user", "User to simulate (optional)")] IUser? who = null)
    {
        await DeferAsync(ephemeral: true);

        if (Context.Guild is null || Context.Channel is not SocketGuildChannel ch)
        {
            await FollowupAsync("Run this in a server text channel (not DM).", ephemeral: true);
            return;
        }

        SocketGuild? guild = Context.Guild;
        IGuildUser? me = await ((IGuild)guild).GetCurrentUserAsync();

        // Pick target = provided user or the invoker
        SocketGuildUser? target = who != null ? guild.GetUser(who.Id) : Context.User as SocketGuildUser;

        if (target is null)
        {
            await FollowupAsync("That user isn’t in this server.", ephemeral: true);
            return;
        }

        // Effective perms in this channel
        ChannelPermissions userPerms = target.GetPermissions(ch);
        ChannelPermissions botPerms = me.GetPermissions(ch);

        List<string> lines = [];

        // Member-side gates (visibility/ability to invoke)
        Line(userPerms.UseApplicationCommands, $"**{target.DisplayName}** has **Use Application Commands** in this channel.");

        // Bot-side gates (ability to respond)
        Line(botPerms.ViewChannel, "Bot can **View Channel**.");
        Line(botPerms.SendMessages, "Bot can **Send Messages**.");
        Line(botPerms.EmbedLinks, "Bot can **Embed Links**.");
        Line(botPerms.AttachFiles, "Bot can **Attach Files**.");

        // Admin bypass note (for the simulated target)
        if (target.GuildPermissions.Administrator)
            lines.Add("ℹ️ Target is **Administrator** and bypasses channel denies. Regular members won’t.");

        // Surface channel overwrites explicitly denying Use Application Commands
        List<string> denyTargets = [];
        try
        {
            foreach (Overwrite ow in ch.PermissionOverwrites)
            {
                OverwritePermissions p = ow.Permissions;
                if (p.UseApplicationCommands == PermValue.Deny)
                {
                    bool applies = ow.TargetType switch
                    {
                        PermissionTarget.User => ow.TargetId == target.Id,
                        PermissionTarget.Role => target.Roles.Any(r => r.Id == ow.TargetId),
                        _ => false
                    };
                    if (!applies) continue;

                    string name = ow.TargetType == PermissionTarget.Role
                        ? (guild.GetRole(ow.TargetId)?.Name ?? $"Role {ow.TargetId}")
                        : (guild.GetUser(ow.TargetId)?.Username ?? $"User {ow.TargetId}");
                    denyTargets.Add(name);
                }
            }
        }
        catch
        {
            /* older libs may not expose UseApplicationCommands on overwrites */
        }

        if (denyTargets.Count > 0)
            lines.Add("⚠️ **Explicit denies for target:** " + string.Join(", ", denyTargets.Select(n => $"`{n}`")));

        // Integration permissions hint (server-wide command restrictions)
        lines.Add("ℹ️ Also check **Server Settings → Integrations → Your App → Command Permissions**. If restricted, only allowed roles/users can run slash commands.");

        EmbedBuilder? eb = new EmbedBuilder()
            .WithTitle($"Diagnostics for #{ch.Name} — {(who != null ? target.DisplayName : "you")}")
            .WithDescription(string.Join("\n", lines))
            .WithColor(lines.Any(l => l.StartsWith("❌")) || denyTargets.Count > 0 ? Color.DarkRed : Color.DarkGreen);

        await FollowupAsync(embed: eb.Build(), ephemeral: true);
        return;

        void Line(bool ok, string text) => lines.Add($"{(ok ? "✅" : "❌")} {text}");
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

        MarkdownPackage? res = await ThunderstoreAPI.GetChangelog(author, name, version);
        string body = string.IsNullOrWhiteSpace(res?.markdown) ? "_No CHANGELOG found._" : res.markdown;

        // Break into embed-sized chunks
        List<Embed> embeds = Chunk.BuildDescriptionEmbedsWithHeader(
            $"{author}/{name} — CHANGELOG",
            string.IsNullOrWhiteSpace(version) ? "" : $"Version: `{version}`",
            body,
            Color.DarkGreen).ToList();

        if (embeds.Count == 0)
        {
            await FollowupAsync("No content.");
            return;
        }

        string key = Guid.NewGuid().ToString("N");
        PagerStore.Pages[key] = embeds;

        MessageComponent comps = BuildPagerComponents(key, 0, embeds.Count);
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

        MarkdownPackage? res = await ThunderstoreAPI.SearchPackages(author, name, version);
        string content = string.IsNullOrWhiteSpace(res?.markdown) ? "# README not found\n" : res.markdown;

        if (format == ReadmeFormat.txt)
            content = content.Replace("\r", "").Replace("\n", Environment.NewLine); // quick normalize

        string fileName = $"{Safe(author)}-{Safe(name)}{(string.IsNullOrWhiteSpace(version) ? "" : "-" + Safe(version))}.readme.{format}";

        byte[] bytes = Encoding.UTF8.GetBytes(content);
        using MemoryStream ms = new(bytes, writable: false);
        FileAttachment file = new(ms, fileName);

        // If already responded with DeferAsync, use FollowupWithFileAsync
        await FollowupWithFileAsync(file, text: $"Here’s the README for **{author}/{name}**{(string.IsNullOrWhiteSpace(version) ? "" : $" `{version}`")}.");
        return;

        string Safe(string s) => string.Join("_", s.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries));
    }

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

        string? md = await ThunderstoreAPI.GetReadmeMarkdown(author, name, string.IsNullOrWhiteSpace(version) ? null : version);
        if (string.IsNullOrWhiteSpace(md))
        {
            await FollowupAsync("README not found for that package/version.");
            return;
        }

        List<(int LineNo, string Excerpt)> hits = ReadmeSearch.Find(md!, query, context, maxMatches, caseInsensitive: true);
        if (hits.Count == 0)
        {
            await FollowupAsync($"No matches for `{query}` in {(string.IsNullOrWhiteSpace(version) ? "latest" : version)} README.");
            return;
        }


        string header = $"**{author}/{name}** — {(string.IsNullOrWhiteSpace(version) ? "latest" : version)}\nMatches for `{query}` (showing {hits.Count}):\n";

        // Each hit goes into a field; field values must be <= 1024.
        IEnumerable<(string Name, string Value, bool Inline)> parts = hits.Select(h => (
            Name: $"Line {h.LineNo}",
            Value: Chunking.TruncField(h.Excerpt, 1000),
            Inline: false
        ));

        IEnumerable<Embed> embeds = Chunk.BuildPagedEmbeds("README Search", header, parts, Color.DarkGreen);


        if (attach)
        {
            // Create a compact .md report for download
            string text = $"# README Search — {author}/{name} ({(string.IsNullOrWhiteSpace(version) ? "latest" : version)})\n" +
                          $"Query: {query}\n\n" +
                          string.Join("\n\n---\n\n", hits.Select(h => $"Line {h.LineNo}\n{h.Excerpt}"));

            byte[] bytes = Encoding.UTF8.GetBytes(text);
            using MemoryStream ms = new(bytes, writable: false);
            FileAttachment file = new(ms, $"{author}-{name}-readme-search.md");

            // first page + file
            using IEnumerator<Embed> enumer = embeds.GetEnumerator();
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

        MarkdownPackage? res = await ThunderstoreAPI.SearchPackages(author, name, version);
        string body = string.IsNullOrWhiteSpace(res?.markdown) ? "_No README found._" : res.markdown;

        // Break into embed-sized chunks
        List<Embed> embeds = Chunk.BuildDescriptionEmbedsWithHeader(
            $"{author}/{name} — README",
            string.IsNullOrWhiteSpace(version) ? "" : $"Version: `{version}`",
            body,
            Color.DarkGreen).ToList();

        if (embeds.Count == 0)
        {
            await FollowupAsync("No content.");
            return;
        }

        string key = Guid.NewGuid().ToString("N");
        PagerStore.Pages[key] = embeds;

        MessageComponent comps = BuildPagerComponents(key, 0, embeds.Count);
        // First page as the original response
        if (Context.Interaction.HasResponded)
            await FollowupAsync(embed: embeds[0], components: comps);
        else
            await RespondAsync(embed: embeds[0], components: comps);
    }

    public static MessageComponent BuildPagerComponents(string key, int index, int total)
    {
        bool prevDisabled = index <= 0;
        bool nextDisabled = index >= total - 1;

        ActionRowBuilder? row = new ActionRowBuilder()
            .WithButton("⟨ Prev", $"pager:{key}:{index}:prev", ButtonStyle.Secondary, disabled: prevDisabled)
            .WithButton($"{index + 1}/{total}", $"pager:{key}:{index}:noop", ButtonStyle.Secondary, disabled: true)
            .WithButton("Next ⟩", $"pager:{key}:{index}:next", ButtonStyle.Secondary, disabled: nextDisabled);

        return new ComponentBuilder().AddRow(row).Build();
    }

    [SlashCommand("moddiff", "Compare two versions of a mod")]
    public async Task ModDiff([Autocomplete(typeof(AuthorAutocomplete))] string author, [Autocomplete(typeof(PackageAutocomplete))] string name, [Autocomplete(typeof(VersionAutocomplete))] string from, [Autocomplete(typeof(VersionAutocomplete))] string to, bool ephemeral = false)
    {
        await DeferAsync(ephemeral: ephemeral);
        ExperimentalPackageInfo? pkg = await Api.GetPackageInfo(author, name);
        VersionInfo? v1 = pkg?.latest is null
            ? null
            : (await ThunderstoreAPI.GetAllModsFromThunderstore())
            .FirstOrDefault(p => p.owner == author && p.name == name)?.versions?.FirstOrDefault(v => v.version_number == from);
        VersionInfo v2 = (await ThunderstoreAPI.GetAllModsFromThunderstore())
            .First(p => p.owner == author && p.name == name).versions!.First(v => v.version_number == to);

        List<string> added = v2.dependencies?.Except(v1?.dependencies ?? []).ToList() ?? [];
        List<string> removed = (v1?.dependencies ?? []).Except(v2.dependencies ?? []).ToList();
        EmbedBuilder? eb = new EmbedBuilder().WithTitle($"{author}/{name} — {from} → {to}")
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
        List<PackageInfo> all = await ThunderstoreAPI.GetAllModsFromThunderstore();
        List<PackageInfo> ag = all.Where(x => x.owner.Equals(a, StringComparison.OrdinalIgnoreCase)).ToList();
        List<PackageInfo> bg = all.Where(x => x.owner.Equals(b, StringComparison.OrdinalIgnoreCase)).ToList();
        if (ag.Count == 0 || bg.Count == 0)
        {
            await FollowupAsync("One or both authors not found.");
            return;
        }

        int sum(List<PackageInfo> g) => g.Sum(m => m.versions?.Sum(v => v.downloads) ?? 0);
        int med(List<PackageInfo> g) => g.Select(m => m.versions?.Sum(v => v.downloads) ?? 0).OrderBy(x => x).DefaultIfEmpty().ElementAt(g.Count / 2);

        Embed? ea = new EmbedBuilder().WithTitle($"Author: {a}")
            .AddField("Mods", ag.Count, true).AddField("Total DL", sum(ag).ToString("N0"), true)
            .AddField("Median DL", med(ag).ToString("N0"), true)
            .AddField("Top 5", string.Join("\n", ag.OrderByDescending(m => m.versions?.Sum(v => v.downloads) ?? 0).Take(5).Select(m => $"[{m.name}]({m.package_url})")), false)
            .WithColor(Color.DarkGreen).Build();

        Embed? eb = new EmbedBuilder().WithTitle($"Author: {b}")
            .AddField("Mods", bg.Count, true).AddField("Total DL", sum(bg).ToString("N0"), true)
            .AddField("Median DL", med(bg).ToString("N0"), true)
            .AddField("Top 5", string.Join("\n", bg.OrderByDescending(m => m.versions?.Sum(v => v.downloads) ?? 0).Take(5).Select(m => $"[{m.name}]({m.package_url})")), false)
            .WithColor(Color.DarkBlue).Build();

        await SendEmbedsPagedAsync([ea, eb]);
    }


    [SlashCommand("authorleaderboard", "Top authors by (median downloads × mod count).")]
    public async Task AuthorLeaderboard(
        [Summary("limit", "How many (1-25)")] int limit = 10,
        [Summary("ephemeral", "Only you can see the response")]
        bool ephemeral = false)
    {
        limit = Math.Clamp(limit, 1, 25);
        await DeferIfNeeded(ephemeral);

        Dictionary<string, AuthorStats>? map = await ThunderstoreAPI.GetAuthorsWithAtLeastFiveMods();
        if (map == null || map.Count == 0)
        {
            await FollowupAsync("No authors meet the criteria.");
            return;
        }

        // Get the global median downloads from the first author (since it's the same on all authors)
        int currentHighest = map.First().Value.global_median_downloads;
        IEnumerable<(string Name, string Value, bool Inline)> fields = map
            .OrderByDescending(kv => kv.Value.medianDownloadsMultiplied)
            .Take(limit)
            .Select(kv => (
                Name: kv.Key,
                Value: $"• Mods: {kv.Value.mods_count:N0}\n• Median: {kv.Value.median_downloads:N0}\n• Median×Count: {kv.Value.medianDownloadsMultiplied:N0}",
                Inline: true
            ));

        string header = "Top authors with at least 5 mods:\n **Global Median Downloads:** " + currentHighest + "\n\n";

        IEnumerable<Embed> embeds = Chunk.BuildPagedEmbeds("Mod Author Leaderboard", header, fields, Color.Gold);
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
        await DeferIfNeeded(ephemeral);

        AuthorStats? stats = await ThunderstoreAPI.GetAuthorStats(author);
        if (stats == null || stats.mods_count == 0)
        {
            await FollowupAsync("Author not found or has no mods.");
            return;
        }

        IEnumerable<string> topLines = stats.topModsSorted.Where(x => deprecated ? x.is_deprecated || !x.is_deprecated : !x.is_deprecated).Take(top).Select((m, i) =>
            $"{i + 1}. [{m.name}]({m.package_url}) — {m.versions?.Sum(v => v.downloads):N0} downloads");

        string desc =
            $"**Mods:** {stats.mods_count:N0}\n" +
            $"**Total Downloads:** {stats.total_downloads:N0}\n" +
            $"**Average:** {stats.average_downloads:N0}\n" +
            $"**Median:** {stats.median_downloads:N0}\n" +
            $"**Median × Count:** {stats.medianDownloadsMultiplied:N0}\n" +
            $"**Most Downloaded:** {stats.most_downloaded_mod}\n\n" +
            $"**Top Mods:**\n{string.Join("\n", topLines)}";

        IEnumerable<Embed> embeds = Chunk.BuildDescriptionEmbeds($"Stats for {author}", desc, Color.Gold);
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

        List<PackageInfo>? mods = await ThunderstoreAPI.GetModsByAuthor(author);
        if (mods == null || mods.Count == 0)
        {
            await FollowupAsync("Author not found or has no mods.");
            return;
        }

        IEnumerable<(string? Name, string Value, bool Inline)> filtered = mods
            .Where(m => includeModpacks
                        || (!m.name.Contains("modpack", StringComparison.OrdinalIgnoreCase) && !m.categories.Contains("Modpacks")))
            .OrderByDescending(m => m.versions?.Sum(v => v.downloads) ?? 0)
            .Take(limit)
            .Select(m => (
                Name: m.name,
                Value: $"Downloads: {(m.versions?.Sum(v => v.downloads) ?? 0):N0}\n[More Info]({m.package_url})",
                Inline: true
            ));

        IEnumerable<Embed> embeds = Chunk.BuildPagedEmbeds($"Top Mods by {author}", $"Limit: {limit} • Include Modpacks: {includeModpacks}", filtered, Color.Gold);
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

        List<PackageInfo>? all = await ThunderstoreAPI.GetAllModsFromThunderstore();
        if (all == null || all.Count == 0)
        {
            await FollowupAsync("No packages available.");
            return;
        }

        // simple wildcard split: author/name or single token
        string authorPat = "*", namePat = "*";
        if (query.Contains('/'))
        {
            string[] parts = query.Split('/', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
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
            string p = "^" + System.Text.RegularExpressions.Regex.Escape(pat).Replace("\\*", ".*") + "$";
            return System.Text.RegularExpressions.Regex.IsMatch(text ?? "", p, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        }

        IEnumerable<(string Name, string Value, bool Inline)> filtered = all
            .Where(m => includeModpacks || (!m.name.Contains("modpack", StringComparison.OrdinalIgnoreCase) && !m.categories.Contains("Modpacks")))
            .Where(m => Like(m.owner, authorPat) && Like(m.name, namePat))
            .Take(limit)
            .Select(m => (
                Name: $"{m.owner}/{m.name}",
                Value: $"Downloads: {m.versions?.Sum(v => v.downloads) ?? 0:N0}\n[More Info]({m.package_url})",
                Inline: true
            ));

        IEnumerable<Embed> embeds = Chunk.BuildPagedEmbeds("Search Results", $"Query: `{query}` • Limit: {limit}", filtered, Color.DarkGreen);
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

        List<PackageInfo>? mods = await ThunderstoreAPI.GetModsByAuthor(author);
        List<(string? name, string, bool)>? dep = mods?
            .Where(m => m.is_deprecated)
            .Select(m => (m.name, $"[More Info]({m.package_url})", true))
            .ToList();

        if (dep == null || dep.Count == 0)
        {
            await FollowupAsync("No deprecated mods found.");
            return;
        }

        IEnumerable<Embed> embeds = Chunk.BuildPagedEmbeds($"Deprecated by {author}", "Marked deprecated on Thunderstore", dep, Color.Red);
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

        ExperimentalPackageInfo? pkg = await Api.GetPackageInfo(author, name);
        if (string.IsNullOrWhiteSpace(pkg?.name))
        {
            await FollowupAsync("Package not found.");
            return;
        }

        List<string> deps = pkg.latest?.dependencies ?? [];
        if (deps.Count == 0)
        {
            await FollowupAsync("This package has no dependencies.");
            return;
        }

        string text = string.Join("\n", deps);
        IEnumerable<Embed> embeds = Chunk.BuildDescriptionEmbeds($"{pkg.name} — Dependencies", text, Color.Purple);
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

        List<PackageInfo>? all = await ThunderstoreAPI.GetAllModsFromThunderstore();
        if (all == null || all.Count == 0)
        {
            await FollowupAsync("No packages available.");
            return;
        }

        // Dependency strings look like "Author-Name-x.y.z" — we match the prefix "Author-Name-"
        string prefix = $"{author}-{name}-";

        IEnumerable<(string Name, string Value, bool Inline)> rows = all
            .Where(m => m.versions?.Any(v => v.dependencies?.Any(d => d.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) == true) == true)
            .OrderByDescending(m => m.versions?.Sum(v => v.downloads) ?? 0)
            .Take(limit)
            .Select(m => (
                Name: $"{m.owner}/{m.name}",
                Value: $"Total Downloads: {m.versions?.Sum(v => v.downloads) ?? 0:N0}\n[More Info]({m.package_url})",
                Inline: true
            ));

        IEnumerable<Embed> embeds = Chunk.BuildPagedEmbeds($"Dependents of {author}/{name}", $"Limit: {limit}", rows, Color.Teal);
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

        DateTime since = DateTime.UtcNow.AddDays(-days);
        List<PackageInfo> mods = await ThunderstoreAPI.GetAllModsFromThunderstore();

        IEnumerable<(string? name, string, bool)> rising = mods
            .Where(m => includeModpacks || (!m.name.Contains("modpack", StringComparison.OrdinalIgnoreCase) && !m.categories.Contains("Modpacks")))
            .Where(m => DateTime.TryParse(m.date_created, out DateTime dc) && dc >= since)
            .OrderByDescending(m => m.versions?.Sum(v => v.downloads) ?? 0)
            .Take(limit)
            .Select(m => (m.name, $"Since created: {(m.versions?.Sum(v => v.downloads) ?? 0):N0} downloads\n[More Info]({m.package_url})", true));

        IEnumerable<Embed> embeds = Chunk.BuildPagedEmbeds($"Rising (last {days} days)", $"Limit: {limit} • Include Modpacks: {includeModpacks}", rising, Color.Orange);
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

        DateTime cutoff = DateTime.UtcNow.AddDays(-days);
        List<PackageInfo> mods = await ThunderstoreAPI.GetAllModsFromThunderstore();

        IEnumerable<(string? Name, string Value, bool Inline)> stale = mods
            .Where(m => includeModpacks || (!m.name.Contains("modpack", StringComparison.OrdinalIgnoreCase) && !m.categories.Contains("Modpacks")))
            .Where(m => DateTime.TryParse(m.date_updated, out DateTime du) && du < cutoff)
            .OrderByDescending(m => m.versions?.Sum(v => v.downloads) ?? 0)
            .Take(limit)
            .Select(m => (
                Name: m.name,
                Value: $"Last updated: {TryDate(m.date_updated)}\nTotal Downloads: {m.versions?.Sum(v => v.downloads) ?? 0:N0}\n[More Info]({m.package_url})",
                Inline: true));

        IEnumerable<Embed> embeds = Chunk.BuildPagedEmbeds($"Stale (>{days} days old)", $"Limit: {limit} • Include Modpacks: {includeModpacks}", stale, Color.DarkRed);
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

        List<PackageInfo> mods = await ThunderstoreAPI.GetAllModsFromThunderstore();
        List<PackageInfo> filtered = mods.Where(m => !m.is_pinned && (includeModpacks || (!m.name.Contains("modpack", StringComparison.OrdinalIgnoreCase) && !m.categories.Contains("Modpacks"))))
            .OrderByDescending(m => m.versions.Sum(v => v.downloads))
            .Take(limit)
            .ToList();

        if (filtered.Count == 0)
        {
            await FollowupAsync("No mods found.");
            return;
        }

        IEnumerable<(string? Name, string Value, bool Inline)> fields = filtered.Select(m =>
            (Name: m.name,
                Value: $"Total Downloads: {m.versions.Sum(v => v.downloads):N0}\n[More Info]({m.package_url})",
                Inline: true));

        IEnumerable<Embed> embeds = Chunk.BuildPagedEmbeds("Most Popular Mods", $"Limit: {limit} • Include Modpacks: {includeModpacks}", fields, Color.Gold);
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

        ExperimentalPackageInfo? pkg = await Api.GetPackageInfo(author, package);
        if (string.IsNullOrWhiteSpace(pkg?.name))
        {
            await FollowupAsync("Package not found.");
            return;
        }

        Embed? main = new EmbedBuilder()
            .WithTitle($"{pkg.name} by {pkg.owner}")
            .WithDescription(pkg.latest?.description ?? "")
            .AddField("Created", TryDate(pkg.date_created), true)
            .AddField("Updated", TryDate(pkg.date_updated), true)
            .AddField("Latest Version", pkg.latest?.version_number ?? "N/A", true)
            .AddField("Website", string.IsNullOrWhiteSpace(pkg.latest?.website_url) ? "—" : pkg.latest.website_url, true)
            .AddField("Thunderstore", $"{ThunderstoreAPI.BaseTsUrl}c/valheim/p/{author}/{package}/")
            .WithThumbnailUrl(pkg.latest?.icon)
            .WithColor(Color.Purple)
            .Build();

        IEnumerable<Embed> embeds;
        if (pkg.latest?.dependencies != null && pkg.latest.dependencies.Count > 0)
        {
            string depsText = string.Join("\n", pkg.latest.dependencies);
            List<Embed> depEmbeds = [main];
            depEmbeds.AddRange(Chunk.BuildDescriptionEmbeds("Dependencies", depsText, Color.Purple));
            embeds = depEmbeds;
        }
        else
        {
            embeds = [main];
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

        List<PackageInfo> mods = await ThunderstoreAPI.GetAllModsFromThunderstore();
        DateTime since = DateTime.UtcNow.AddDays(-1);

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

        IEnumerable<(string? name, string, bool)> fields = recent.Select(x => (x.name, $"Downloads today: {x.Downloads:N0}\n[More Info]({x.package_url})", true));
        IEnumerable<Embed> embeds = Chunk.BuildPagedEmbeds("Top Download Growth (Today)", $"Since {since:u}", fields, Color.Purple);
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

        List<PackageInfo> mods = await ThunderstoreAPI.GetAllModsFromThunderstore();
        IEnumerable<(string? name, string, bool)> list = mods
            .Where(m => includeModpacks || (!m.name.Contains("modpack", StringComparison.OrdinalIgnoreCase) && !(m.categories?.Contains("Modpacks") ?? false)))
            .OrderByDescending(m => m.versions?.Count ?? 0)
            .Take(limit)
            .Select(m => (m.name, $"Versions: {m.versions?.Count ?? 0}\n[More Info]({m.package_url})", true));

        IEnumerable<Embed> embeds = Chunk.BuildPagedEmbeds("Mod with Most Versions", $"Limit: {limit}", list, Color.Purple);
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

        List<PackageInfo> mods = await ThunderstoreAPI.GetAllModsFromThunderstore();
        IEnumerable<(string? name, string, bool)> list = mods
            .Where(m => m.is_deprecated)
            .OrderByDescending(m => DateTime.TryParse(m.date_updated, out DateTime du) ? du : DateTime.MinValue)
            .Take(limit)
            .Select(m => (m.name, $"Updated: {TryDate(m.date_updated)} • [More Info]({m.package_url})", true));

        IEnumerable<Embed> embeds = Chunk.BuildPagedEmbeds("Deprecated Mods (Recently Updated)", $"Limit: {limit}", list, Color.DarkRed);
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

        List<PackageInfo> mods = await ThunderstoreAPI.GetModsByAuthor(author) ?? [];
        if (mods.Count == 0)
        {
            await FollowupAsync("Author not found or has no mods.");
            return;
        }

        DateTime start = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1).AddMonths(-11);
        List<DateTime> months = Enumerable.Range(0, 12).Select(i => start.AddMonths(i)).ToList();

        int[] uploads = new int[12];
        int[] updates = new int[12];

        foreach (PackageInfo m in mods)
        {
            if (DateTime.TryParse(m.date_created, out DateTime dc))
            {
                int idx = (dc.Year - start.Year) * 12 + (dc.Month - start.Month);
                if (idx is >= 0 and < 12) ++uploads[idx];
            }

            if (!DateTime.TryParse(m.date_updated, out DateTime du)) continue;
            {
                int idx = (du.Year - start.Year) * 12 + (du.Month - start.Month);
                if (idx is >= 0 and < 12) ++updates[idx];
            }
        }

        IEnumerable<string> lines = months.Select((m, i) => $"{m:yyyy-MM}: uploads {uploads[i]}, updates {updates[i]}");
        string desc = string.Join("\n", lines);

        IEnumerable<Embed> embeds = Chunk.BuildDescriptionEmbeds($"Activity for {author}", desc, Color.DarkGrey);
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

        List<PackageInfo> all = await ThunderstoreAPI.GetAllModsFromThunderstore();
        PackageInfo? pkg = all.FirstOrDefault(p => p.owner.Equals(author, StringComparison.OrdinalIgnoreCase) && p.name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (pkg == null)
        {
            await FollowupAsync("Package not found.");
            return;
        }

        VersionInfo? latest = LatestVersion(pkg);
        List<string> latestDeps = latest?.dependencies ?? [];
        if (latestDeps.Count == 0)
        {
            await FollowupAsync("This package has no dependencies.");
            return;
        }

        Dictionary<string, PackageInfo> map = all
            .Where(p => !string.IsNullOrWhiteSpace(p.owner) && !string.IsNullOrWhiteSpace(p.name))
            .GroupBy(p => MapKey(p.owner!, p.name!), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g => g.OrderByDescending(m => DateTime.TryParse(m.date_updated, out DateTime du) ? du : DateTime.MinValue)
                    .ThenByDescending(m => DateTime.TryParse(m.date_created, out DateTime dc) ? dc : DateTime.MinValue)
                    .First(),
                StringComparer.OrdinalIgnoreCase);

        StringBuilder sb = new();
        HashSet<string> visited = new(StringComparer.OrdinalIgnoreCase);

        sb.AppendLine($"{author}/{name}");
        Walk(latestDeps, 1);

        IEnumerable<Embed> embeds = Chunk.BuildDescriptionEmbeds($"{author}/{name} — Dependencies (depth {depth})", sb.ToString(), Color.DarkTeal);
        await SendEmbedsPagedAsync(embeds);
        return;

        (string owner, string pkg) ParseDep(string dep)
        {
            // "Author-Name-x.y.z" -> owner, name
            string[] parts = dep.Split('-', 3);
            return parts.Length < 2 ? ("", "") : (parts[0], parts[1]);
        }

        void Walk(IEnumerable<string> deps, int d)
        {
            if (deps == null || d > depth) return;
            foreach (string dep in deps)
            {
                (string own, string nm) = ParseDep(dep);
                if (string.IsNullOrWhiteSpace(own) || string.IsNullOrWhiteSpace(nm)) continue;
                string key = MapKey(own, nm);
                if (!visited.Add(key)) continue;

                sb.AppendLine($"{new string(' ', (d - 1) * 2)}• {own}/{nm}");
                if (map.TryGetValue(key, out PackageInfo? child))
                {
                    VersionInfo? childLatest = LatestVersion(child);
                    List<string> childDeps = childLatest?.dependencies ?? [];
                    Walk(childDeps, d + 1);
                }
            }
        }

        string MapKey(string owner, string nm) => $"{owner}/{nm}".ToLowerInvariant();

        VersionInfo? LatestVersion(PackageInfo p) =>
            (p.versions ?? [])
            .OrderByDescending(v => DateTime.TryParse(v.date_created, out DateTime dt) ? dt : DateTime.MinValue)
            .ThenByDescending(v => v.version_number)
            .FirstOrDefault();
    }

    [SlashCommand("nsfwmods", "List mods marked NSFW.")]
    public async Task NsfwMods(
        [Summary("limit", "How many (1-50)")] int limit = 25,
        [Summary("ephemeral", "Only you can see the response")]
        bool ephemeral = false)
    {
        limit = Math.Clamp(limit, 1, 50);
        await DeferIfNeeded(ephemeral);

        List<PackageInfo> mods = await ThunderstoreAPI.GetAllModsFromThunderstore();
        IEnumerable<(string? name, string, bool)> list = mods
            .Where(m => m.has_nsfw_content)
            .OrderByDescending(m => m.versions?.Sum(v => v.downloads) ?? 0)
            .Take(limit)
            .Select(m => (m.name, $"[More Info]({m.package_url})", true));

        IEnumerable<Embed> embeds = Chunk.BuildPagedEmbeds("NSFW Mods", $"Limit: {limit}", list, Color.DarkRed);
        await SendEmbedsPagedAsync(embeds);
    }

    [SlashCommand("modsbycategory", "List mods within a category.")]
    public async Task ModsByCategory(
        [Autocomplete(typeof(CategoryAutocomplete))] [Summary("category", "Exact category name")]
        string category,
        [Summary("limit", "How many (1-50)")] int limit = 25,
        [Summary("include_modpacks", "Include modpacks?")]
        bool includeModpacks = false,
        [Summary("ephemeral", "Only you can see the response")]
        bool ephemeral = false)
    {
        limit = Math.Clamp(limit, 1, 50);
        await DeferIfNeeded(ephemeral);

        List<PackageInfo> mods = await ThunderstoreAPI.GetAllModsFromThunderstore();
        IEnumerable<(string? name, string, bool)> list = mods
            .Where(m => includeModpacks || (!m.name.Contains("modpack", StringComparison.OrdinalIgnoreCase) && !(m.categories?.Contains("Modpacks") ?? false)))
            .Where(m => (m.categories ?? []).Contains(category, StringComparer.OrdinalIgnoreCase))
            .OrderByDescending(m => m.versions?.Sum(v => v.downloads) ?? 0)
            .Take(limit)
            .Select(m => (m.name, $"Downloads: {m.versions?.Sum(v => v.downloads) ?? 0:N0}\n[More Info]({m.package_url})", true));

        IEnumerable<Embed> embeds = Chunk.BuildPagedEmbeds($"Mods in Category: {category}", $"Limit: {limit}", list, Color.Teal);
        await SendEmbedsPagedAsync(embeds);
    }

    private async Task SendEmbedsPagedEmbedsAsync(IEnumerable<Embed> embeds) => await SendEmbedsPagedAsync(embeds);


    private static string TryDate(string? iso) => DateTime.TryParse(iso, out DateTime dt) ? dt.ToShortDateString() : "—";
}