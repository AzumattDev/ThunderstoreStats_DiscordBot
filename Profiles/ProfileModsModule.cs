using Discord;
using Discord.Interactions;

namespace ThunderstoreStats_DiscordBot.Profiles;

public class ProfileModsModule(ThunderstoreAPI api, Chunking chunk) : AppModuleBase(api, chunk)
{
    [SlashCommand("profile_mods", "List mods contained in a Thunderstore r2modman profile code")]
    public async Task ProfileModsAsync(
        [Summary("code", "The legacy profile code (e.g., 019a4cb3-3df0-1999-bea0-f823c41cf8bf)")]
        string? code,
        [Summary("paginate", "Use button pagination (default: true)")]
        bool paginate = true,
        [Summary("attach", "Attach a file of the content (default: true)")]
        bool attach = true)
    {
        await DeferAsync(ephemeral: false);

        IReadOnlyList<ModRef> mods;
        string community;
        string? profileName;
        try
        {
            using CancellationTokenSource cts = new(TimeSpan.FromSeconds(20));
            (IReadOnlyList<ModRef> Mods, string? Community, string? ProfileName) parsed = await ThunderstoreProfileReader.GetModsAsync(code, cts.Token);
            mods = parsed.Mods;
            community = string.IsNullOrWhiteSpace(parsed.Community) ? "valheim" : parsed.Community!;
            profileName = parsed.ProfileName;
        }
        catch (KeyNotFoundException nf)
        {
            await FollowupAsync(embed: Error("Profile not found", nf.Message));
            return;
        }
        catch (HttpRequestException hre)
        {
            await FollowupAsync(embed: Error("Network/API error", hre.Message));
            return;
        }
        catch (InvalidDataException ide)
        {
            await FollowupAsync(embed: Error("Invalid profile data", ide.Message));
            return;
        }
        catch (Exception ex)
        {
            await FollowupAsync(embed: Error("Unexpected error", ex.Message));
            return;
        }

        if (mods.Count == 0)
        {
            await FollowupAsync(embed: new EmbedBuilder()
                .WithTitle("No mods found in profile")
                .WithDescription("Could not locate a manifest with dependencies in this export. It might be a minimal or non-standard export.")
                .WithColor(Color.Orange)
                .Build());
            return;
        }

        IReadOnlyList<EnrichedMod> enriched = await ModResolver.EnrichAsync(mods, community: community, ct: default);
        string title = string.IsNullOrWhiteSpace(profileName) ? "Mods in Profile" : $"Mods in {profileName}";
        IReadOnlyList<Embed> listEmbeds = EmbedListPager.BuildModListEmbeds(
            mods: enriched,
            title: title,
            community: community,
            color: Color.DarkBlue,
            softPageBudget: 3500,
            header: title,
            footer: "Profile Mods");
        if (paginate && listEmbeds.Count > 0 && attach)
        {
            IEnumerable<string> lines = enriched.Select(m => $"{m.Author}-{m.Name} {m.Version}".TrimEnd());
            using MemoryStream ms = new(System.Text.Encoding.UTF8.GetBytes(string.Join("\n", lines)), writable: false);

            string key = Guid.NewGuid().ToString("N");
            PagerStore.Pages[key] = listEmbeds;
            MessageComponent comps = ThunderstoreSlash.BuildPagerComponents(key, 0, listEmbeds.Count);

            if (Context.Interaction.HasResponded)
                await FollowupWithFileAsync(new FileAttachment(ms, "profile-mods.txt"),
                    embed: listEmbeds[0], components: comps,
                    text: $"Mods for **{title}** ({enriched.Count} total)");
            else
                await RespondWithFileAsync(new FileAttachment(ms, "profile-mods.txt"),
                    embed: listEmbeds[0], components: comps,
                    text: $"Mods for **{title}** ({enriched.Count} total)");

            return;
        }

        await SendEmbedsOptionallyPagedAsync(listEmbeds, paginate);
        if (attach)
        {
            IEnumerable<string> lines = enriched.Select(m => $"{m.Author}-{m.Name} {m.Version}".TrimEnd());
            using MemoryStream ms = new(System.Text.Encoding.UTF8.GetBytes(string.Join("\n", lines)), writable: false);

            if (!paginate && listEmbeds.Count == 1)
            {
                if (Context.Interaction.HasResponded)
                    await FollowupWithFileAsync(new FileAttachment(ms, "profile-mods.txt"), embed: listEmbeds[0], text: $"Mods for **{title}** ({enriched.Count})");
                else
                    await RespondWithFileAsync(new FileAttachment(ms, "profile-mods.txt"), embed: listEmbeds[0], text: $"Mods for **{title}** ({enriched.Count})");
            }
            else
            {
                await FollowupWithFileAsync(new FileAttachment(ms, "profile-mods.txt"), text: $"Mods for **{title}** ({enriched.Count})");
            }
        }
    }

    private static Embed Error(string title, string message) => new EmbedBuilder().WithTitle(title).WithDescription(message.Truncate(1000)).WithColor(Color.Red).Build();
}

internal static class StringUtil
{
    public static string Truncate(this string s, int max) => string.IsNullOrEmpty(s) || s.Length <= max ? s : s.Substring(0, max - 1) + "…";
}