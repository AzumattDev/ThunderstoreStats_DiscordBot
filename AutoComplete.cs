using Discord;
using Discord.Interactions;

namespace DiscordBot;

static class AutoUtil
{
    public static string Needle(IAutocompleteInteraction interaction) => interaction?.Data?.Current?.Value?.ToString() ?? string.Empty;

    public static string Opt(IAutocompleteInteraction interaction, string name) =>
        interaction?.Data?.Options?.FirstOrDefault(o => string.Equals(o.Name, name, StringComparison.OrdinalIgnoreCase))
            ?.Value?.ToString() ?? string.Empty;

    public static IEnumerable<T> Top25<T>(IEnumerable<T> src) => src.Take(25);
}

public class AuthorAutocomplete : AutocompleteHandler
{
    public override async Task<AutocompletionResult> GenerateSuggestionsAsync(
        IInteractionContext context,
        IAutocompleteInteraction interaction,
        IParameterInfo parameter,
        IServiceProvider services)
    {
        var all = await ThunderstoreAPI.GetAllModsFromThunderstore();
        var needle = AutoUtil.Needle(interaction).ToLowerInvariant();

        var owners = all
            .GroupBy(p => p.owner ?? "", StringComparer.OrdinalIgnoreCase)
            .Select(g => new
            {
                Owner = g.Key,
                Total = g.Sum(p => p.versions?.Sum(v => v.downloads) ?? 0)
            })
            .Where(x => !string.IsNullOrWhiteSpace(x.Owner))
            .OrderByDescending(x => x.Total);

        if (!string.IsNullOrWhiteSpace(needle))
            owners = owners.Where(x => x.Owner.ToLowerInvariant().Contains(needle)).OrderByDescending(x => x.Total);

        var results = AutoUtil.Top25(owners).Select(x => new AutocompleteResult(x.Owner, x.Owner));

        return AutocompletionResult.FromSuccess(results);
    }
}

public class PackageAutocomplete : AutocompleteHandler
{
    public override async Task<AutocompletionResult> GenerateSuggestionsAsync(
        IInteractionContext context,
        IAutocompleteInteraction interaction,
        IParameterInfo parameter,
        IServiceProvider services)
    {
        var all = await ThunderstoreAPI.GetAllModsFromThunderstore();

        // If the command also has an "author" option, use it to scope results
        var author = AutoUtil.Opt(interaction, "author");
        var needle = AutoUtil.Needle(interaction).ToLowerInvariant();

        IEnumerable<PackageInfo> source = all;
        if (!string.IsNullOrWhiteSpace(author))
            source = source.Where(p => (p.owner ?? "").Equals(author, StringComparison.OrdinalIgnoreCase));

        var ordered = source.OrderByDescending(p => p.versions?.Sum(v => v.downloads) ?? 0);

        if (!string.IsNullOrWhiteSpace(needle))
            ordered = ordered.Where(p => (p.name ?? "").ToLowerInvariant().Contains(needle)).OrderByDescending(p => p.versions?.Sum(v => v.downloads) ?? 0);

        var results = AutoUtil.Top25(ordered).Select(p => new AutocompleteResult(p.name ?? "", p.name ?? ""));

        return AutocompletionResult.FromSuccess(results);
    }
}

public class VersionAutocomplete : AutocompleteHandler
{
    public override async Task<AutocompletionResult> GenerateSuggestionsAsync(
        IInteractionContext context,
        IAutocompleteInteraction interaction,
        IParameterInfo parameter,
        IServiceProvider services)
    {
        var all = await ThunderstoreAPI.GetAllModsFromThunderstore();

        // Read sibling parameters commonly used in your commands
        var author = AutoUtil.Opt(interaction, "author");
        var pkg = AutoUtil.Opt(interaction, "name");
        if (string.IsNullOrWhiteSpace(pkg))
            pkg = AutoUtil.Opt(interaction, "package"); // some commands use "package"

        var target = all.FirstOrDefault(p => (p.owner ?? "").Equals(author, StringComparison.OrdinalIgnoreCase) && (p.name ?? "").Equals(pkg, StringComparison.OrdinalIgnoreCase));

        var needle = AutoUtil.Needle(interaction).ToLowerInvariant();

        var versions = target?.versions ?? new List<VersionInfo>();
        var ordered = versions
            .OrderByDescending(v => DateTime.TryParse(v.date_created, out var d) ? d : DateTime.MinValue)
            .ThenByDescending(v => v.version_number);

        if (!string.IsNullOrWhiteSpace(needle))
            ordered = ordered.Where(v => (v.version_number ?? "").ToLowerInvariant().Contains(needle))
                .OrderByDescending(v => DateTime.TryParse(v.date_created, out var d) ? d : DateTime.MinValue);

        var results = AutoUtil.Top25(ordered).Select(v => new AutocompleteResult(v.version_number ?? "", v.version_number ?? ""));

        return AutocompletionResult.FromSuccess(results);
    }
}