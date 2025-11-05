using Discord;
using Discord.Interactions;
using Microsoft.Extensions.DependencyInjection;

namespace ThunderstoreStats_DiscordBot;

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
    public override Task<AutocompletionResult> GenerateSuggestionsAsync(IInteractionContext context, IAutocompleteInteraction interaction, IParameterInfo parameter, IServiceProvider services)
    {
        try
        {
            ThunderstoreCache cache = services.GetRequiredService<ThunderstoreCache>();
            string needle = AutoUtil.Needle(interaction);
            IEnumerable<AutocompleteResult> results = cache.SuggestAuthors(needle, 20).Select(a => new AutocompleteResult(a, a));
            return Task.FromResult(AutocompletionResult.FromSuccess(results));
        }
        catch (Exception)
        {
            return Task.FromResult(AutocompletionResult.FromSuccess([]));
        }
    }
}

public class PackageAutocomplete : AutocompleteHandler
{
    public override Task<AutocompletionResult> GenerateSuggestionsAsync(IInteractionContext context, IAutocompleteInteraction interaction, IParameterInfo parameter, IServiceProvider services)
    {
        try
        {
            ThunderstoreCache cache = services.GetRequiredService<ThunderstoreCache>();
            string author = AutoUtil.Opt(interaction, "author");
            string needle = AutoUtil.Needle(interaction);
            IEnumerable<AutocompleteResult> results = cache.SuggestPackages(author, needle, 20).Select(n => new AutocompleteResult(n, n));
            return Task.FromResult(AutocompletionResult.FromSuccess(results));
        }
        catch (Exception)
        {
            return Task.FromResult(AutocompletionResult.FromSuccess([]));
        }
    }
}

public class VersionAutocomplete : AutocompleteHandler
{
    public override Task<AutocompletionResult> GenerateSuggestionsAsync(IInteractionContext context, IAutocompleteInteraction interaction, IParameterInfo parameter, IServiceProvider services)
    {
        try
        {
            ThunderstoreCache cache = services.GetRequiredService<ThunderstoreCache>();
            string author = AutoUtil.Opt(interaction, "author");
            string pkg = AutoUtil.Opt(interaction, "name");
            if (string.IsNullOrWhiteSpace(pkg)) pkg = AutoUtil.Opt(interaction, "package");

            IEnumerable<AutocompleteResult> results = [];
            if (!string.IsNullOrWhiteSpace(author) && !string.IsNullOrWhiteSpace(pkg))
                results = cache.SuggestVersions(author, pkg, AutoUtil.Needle(interaction), 20).Select(v => new AutocompleteResult(v, v));

            return Task.FromResult(AutocompletionResult.FromSuccess(results));
        }
        catch (Exception)
        {
            return Task.FromResult(AutocompletionResult.FromSuccess([]));
        }
    }
}

public class CategoryAutocomplete : AutocompleteHandler
{
    public override Task<AutocompletionResult> GenerateSuggestionsAsync(IInteractionContext context, IAutocompleteInteraction interaction, IParameterInfo parameter, IServiceProvider services)
    {
        try
        {
            ThunderstoreCache cache = services.GetRequiredService<ThunderstoreCache>();
            string needle = AutoUtil.Needle(interaction);
            bool includeModpacks = bool.TryParse(AutoUtil.Opt(interaction, "include_modpacks"), out bool b) && b;

            IEnumerable<AutocompleteResult> cats = cache.SuggestCategories(needle, includeModpacks, max: 25).Select(c => new AutocompleteResult(c, c));

            return Task.FromResult(AutocompletionResult.FromSuccess(cats));
        }
        catch
        {
            // prevent double-ack on exceptions
            return Task.FromResult(AutocompletionResult.FromSuccess([]));
        }
    }
}