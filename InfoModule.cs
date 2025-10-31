/*using Discord.Commands;
using System.Threading.Tasks;
using Discord;
using Discord.Interactions;

namespace DiscordBot;

public class ThunderstoreCommands : ModuleBase<SocketCommandContext>
{
    [Command("help")]
    [Discord.Commands.Summary("Displays a list of available commands.")]
    public async Task Help()
    {
        List<CommandInfo> commands = Program._commands.Commands.ToList();
        int commandsPerPage = 25; // Discord embed limit

        for (int i = 0; i < commands.Count; i += commandsPerPage)
        {
            EmbedBuilder embed = new EmbedBuilder()
            {
                Title = "Available Commands",
                Color = Color.Green
            };

            IEnumerable<CommandInfo> commandsPage = commands.Skip(i).Take(commandsPerPage);
            foreach (CommandInfo? command in commandsPage)
            {
                string parameters = string.Join(", ", command.Parameters.Select(p => p.Name));
                parameters = string.IsNullOrWhiteSpace(parameters) ? "" : $" [{parameters}]";
                embed.AddField($"{Program.CommandPrefix}{command.Name}", $"{command.Summary}\n`Usage: {Program.CommandPrefix}{command.Name}{parameters}`", true);
            }

            await ReplyAsync(embed: embed.Build());
        }
    }

    [Command("authorstats")]
    [SlashCommand("authorstats", "Displays statistics for a specified author.")]
    [Discord.Commands.Summary("Displays statistics for a specified author.")]
    public async Task GetAuthorStats([Remainder] string authorName)
    {
        AuthorStats stats = await ThunderstoreAPI.GetAuthorStats(authorName);
        if (stats == null)
        {
            await ReplyAsync("Author not found.");
            return;
        }
        
        string top5 = string.Join("\n", stats.top5mods.Select((m, i) => $"[{m.name}]({m.package_url}) [{m.versions?.Sum(v => v.downloads):N0}]"));

        EmbedBuilder embed = new EmbedBuilder()
        {
            Title = $"Stats for {authorName}",
            Description = $"**Number of Mods:** {stats.mods_count:N0}\n**Total Downloads:** {stats.total_downloads:N0}\n **Average Downloads:** {stats.average_downloads:N0}\n **Median Downloads** {stats.median_downloads:N0}\n **Median Downloads Multiplied** {stats.medianDownloadsMultiplied:N0}\n **Most Downloaded Mod**: {stats.most_downloaded_mod}\n\n **Top 5 Mods (Most Downloaded)**: \n{top5}",
            Color = Color.Gold
        };

        await ReplyAsync(embed: embed.Build());
    }

    [Command("authorleaderboard")]
    [Discord.Commands.Summary("Displays a leaderboard of mod authors based on average downloads.")]
    public async Task ShowLeaderboard()
    {
        Dictionary<string, AuthorStats> authors = await ThunderstoreAPI.GetAuthorsWithAtLeastFiveMods();
        if (!authors.Any())
        {
            await ReplyAsync("No authors meet the criteria.");
            return;
        }

        // Get the global median downloads from the first author
        int currentHighest = authors.First().Value.global_median_downloads;

        EmbedBuilder embed = new EmbedBuilder()
        {
            Title = "Mod Author Leaderboard",
            Description = "Top authors with at least 5 mods:\n **Global Median Downloads:** " + currentHighest + "\n\n",
            Color = Color.Gold
        };

        foreach (KeyValuePair<string, AuthorStats> author in authors.OrderByDescending(a => a.Value.medianDownloadsMultiplied).Take(10))
        {
            embed.AddField(author.Key, $"Median Downloads Multiplied: {author.Value.medianDownloadsMultiplied:N0}", true);
        }

        await ReplyAsync(embed: embed.Build());
    }


    /*[Command("listmods")]
    [Discord.Commands.Summary("Lists all mods from Thunderstore.")]
    public async Task ListMods()
    {
        List<PackageInfo>? mods = await DiscordBot.ThunderstoreAPI.GetAllModsFromThunderstore();
        if (mods.Count == 0)
        {
            await ReplyAsync("No mods found.");
            return;
        }

        EmbedBuilder embed = new EmbedBuilder()
        {
            Title = "Available Mods",
            Description = "Listing all available mods from Thunderstore",
            Color = Color.Blue
        };

        foreach (PackageInfo mod in mods.Take(10)) // Limit to 10 entries for display
        {
            embed.AddField(mod.name, $"Version: {mod.versions?.FirstOrDefault()?.version_number ?? "N/A"}", true);
        }

        await ReplyAsync(embed: embed.Build());
    }#1#

    [Command("modrating")]
    [Discord.Commands.Summary("Gets the rating score of a specified mod from Thunderstore.")]
    public async Task GetModRatingAsync([Remainder] string modName)
    {
        PackageInfo? modInfo = await ThunderstoreAPI.GetModInfo(modName);
        if (modInfo == null)
        {
            await ReplyAsync("Mod not found.");
            return;
        }

        EmbedBuilder embed = new EmbedBuilder()
        {
            Title = $"{modInfo.name} - Rating",
            Description = $"**Rating Score:** {modInfo.rating_score}",
            Color = Color.Purple
        };

        await ReplyAsync(embed: embed.Build());
    }

    [Command("highestmodrating")]
    [Discord.Commands.Summary("Displays the highest rated mods.")]
    public async Task GetHighestRatedModsAsync()
    {
        List<PackageInfo>? mods = await ThunderstoreAPI.GetAllModsFromThunderstore();
        List<PackageInfo> highestRatedMods = mods.Where(m => !m.is_pinned && !m.name.ToLower().Contains("modpack") && !m.categories.Contains("Modpacks")).OrderByDescending(m => m.rating_score).Take(10).ToList();

        if (!highestRatedMods.Any())
        {
            await ReplyAsync("No highly rated mods found.");
            return;
        }

        EmbedBuilder embed = new EmbedBuilder()
        {
            Title = "Highest Rated Mods",
            Color = Color.Purple
        };

        foreach (PackageInfo mod in highestRatedMods)
        {
            embed.AddField(mod.name, $"Rating: {mod.rating_score}\n[More Info]({mod.package_url})", true);
        }

        await ReplyAsync(embed: embed.Build());
    }

    [Command("highestmodratingbyauthor")]
    [Discord.Commands.Summary("Displays the highest rated mods by a specified author.")]
    public async Task GetHighestRatedModsByAuthorAsync([Remainder] string authorName)
    {
        List<PackageInfo> mods = await ThunderstoreAPI.GetModsByAuthor(authorName);
        List<PackageInfo> highestRatedMods = mods.Where(m => !m.is_pinned && !m.name.ToLower().Contains("modpack") && !m.categories.Contains("Modpacks")).OrderByDescending(m => m.rating_score).Take(10).ToList();

        if (!highestRatedMods.Any())
        {
            await ReplyAsync("No highly rated mods found for this author.");
            return;
        }

        EmbedBuilder embed = new EmbedBuilder()
        {
            Title = $"Highest Rated Mods by {authorName}",
            Color = Color.Purple
        };

        foreach (PackageInfo mod in highestRatedMods)
        {
            embed.AddField(mod.name, $"Rating: {mod.rating_score}\n[More Info]({mod.package_url})", true);
        }

        await ReplyAsync(embed: embed.Build());
    }

    [Command("deprecatedmods")]
    [Discord.Commands.Summary("Lists all deprecated mods by a specified author.")]
    public async Task GetDeprecatedModsAsync([Remainder] string authorName)
    {
        List<PackageInfo> mods = await ThunderstoreAPI.GetModsByAuthor(authorName);
        List<PackageInfo> deprecatedMods = mods.Where(m => m.is_deprecated && !m.is_pinned && !m.name.ToLower().Contains("modpack") && !m.categories.Contains("Modpacks")).ToList();

        if (!deprecatedMods.Any())
        {
            await ReplyAsync("No deprecated mods found for this author.");
            return;
        }

        EmbedBuilder embed = new EmbedBuilder()
        {
            Title = $"Deprecated Mods by {authorName}",
            Color = Color.Red
        };

        foreach (PackageInfo mod in deprecatedMods)
        {
            embed.AddField(mod.name, $"[More Info]({mod.package_url})", true);
        }

        await ReplyAsync(embed: embed.Build());
    }

    [Command("recentmods")]
    [Discord.Commands.Summary("Displays the most recently updated mods.")]
    public async Task GetRecentModsAsync()
    {
        List<PackageInfo>? mods = await ThunderstoreAPI.GetAllModsFromThunderstore();
        List<PackageInfo> recentMods = mods.Where(m => !m.is_pinned && !m.name.ToLower().Contains("modpack") && !m.categories.Contains("Modpacks")).OrderByDescending(m => m.date_updated).Take(10).ToList();

        if (!recentMods.Any())
        {
            await ReplyAsync("No recent mods found.");
            return;
        }

        EmbedBuilder embed = new EmbedBuilder()
        {
            Title = "Most Recent Mods",
            Color = Color.Green
        };

        foreach (PackageInfo mod in recentMods)
        {
            embed.AddField(mod.name, $"Updated: {mod.date_updated}\n[More Info]({mod.package_url})\n{string.Join(',', mod.categories)}", true);
        }

        await ReplyAsync(embed: embed.Build());
    }

    [Command("nsfwmods")]
    [Discord.Commands.Summary("Lists all mods marked as NSFW.")]
    public async Task GetNSFWModsAsync()
    {
        List<PackageInfo>? mods = await ThunderstoreAPI.GetAllModsFromThunderstore();
        List<PackageInfo> nsfwMods = mods.Where(m => m.has_nsfw_content && !m.is_pinned && !m.name.ToLower().Contains("modpack") && !m.categories.Contains("Modpacks")).ToList();

        if (nsfwMods.Count == 0)
        {
            await ReplyAsync("No NSFW mods found.");
            return;
        }

        EmbedBuilder embed = new EmbedBuilder()
        {
            Title = "NSFW Mods",
            Color = Color.DarkRed
        };

        foreach (PackageInfo mod in nsfwMods.Take(25))
        {
            Console.WriteLine("NSFW Mod: " + mod.name);
            embed.AddField(mod.name, $"[More Info]({mod.package_url})", true);
        }

        await ReplyAsync(embed: embed.Build());
    }


    [Command("topmodversions")]
    [Discord.Commands.Summary("Displays the most downloaded versions of a specified mod.")]
    public async Task GetTopModVersionsAsync([Remainder] string modName)
    {
        PackageInfo? modInfo = await ThunderstoreAPI.GetModInfo(modName);
        if (modInfo == null || modInfo.versions == null || !modInfo.versions.Any())
        {
            await ReplyAsync("Mod or versions not found.");
            return;
        }

        List<VersionInfo> topVersions = modInfo.versions.OrderByDescending(v => v.downloads).Take(5).ToList();

        EmbedBuilder embed = new EmbedBuilder()
        {
            Title = $"{modInfo.name} - Top Versions",
            Color = Color.Blue
        };

        foreach (VersionInfo version in topVersions)
        {
            embed.AddField(version.version_number, $"Downloads: {version.downloads}\n[Download]({version.download_url})", true);
        }

        await ReplyAsync(embed: embed.Build());
    }

    [Command("popularmods")]
    [Discord.Commands.Summary("Displays the mods with the highest total downloads.")]
    public async Task GetPopularModsAsync()
    {
        List<PackageInfo>? mods = await ThunderstoreAPI.GetAllModsFromThunderstore();
        List<PackageInfo> popularMods = mods.Where(m => !m.is_pinned && !m.name.ToLower().Contains("modpack") && !m.categories.Contains("Modpacks")).OrderByDescending(m => m.versions.Sum(v => v.downloads)).Take(10).ToList();

        if (!popularMods.Any())
        {
            await ReplyAsync("No popular mods found.");
            return;
        }

        EmbedBuilder embed = new EmbedBuilder()
        {
            Title = "Most Popular Mods (Top 10)",
            Color = Color.Gold
        };

        foreach (PackageInfo mod in popularMods)
        {
            embed.AddField(mod.name, $"Total Downloads: {mod.versions.Sum(v => v.downloads)}\n[More Info]({mod.package_url})", true);
        }

        await ReplyAsync(embed: embed.Build());
    }

    [Command("mostmods")]
    [Discord.Commands.Summary("Displays authors with the most number of mods.")]
    public async Task GetMostActiveAuthorsAsync()
    {
        List<PackageInfo>? mods = await ThunderstoreAPI.GetAllModsFromThunderstore();
        var authorCounts = mods.Where(m => !m.is_pinned && !m.name.ToLower().Contains("modpack") && !m.categories.Contains("Modpacks")).GroupBy(m => m.owner)
            .Select(g => new { Author = g.Key, Count = g.Count() })
            .OrderByDescending(a => a.Count)
            .Take(10)
            .ToList();

        if (!authorCounts.Any())
        {
            await ReplyAsync("No active authors found.");
            return;
        }

        EmbedBuilder embed = new EmbedBuilder()
        {
            Title = "Authors with Most Mods",
            Color = Color.Blue
        };

        foreach (var author in authorCounts)
        {
            embed.AddField(author.Author, $"Number of Mods: {author.Count}", true);
        }

        await ReplyAsync(embed: embed.Build());
    }

    [Command("topratedmods")]
    [Discord.Commands.Summary("Displays the mods with the highest rating scores.")]
    public async Task GetTopRatedModsAsync()
    {
        List<PackageInfo>? mods = await ThunderstoreAPI.GetAllModsFromThunderstore();
        List<PackageInfo> topRatedMods = mods.Where(m => !m.is_pinned && !m.name.ToLower().Contains("modpack") && !m.categories.Contains("Modpacks")).OrderByDescending(m => m.rating_score).Take(10).ToList();

        if (!topRatedMods.Any())
        {
            await ReplyAsync("No top-rated mods found.");
            return;
        }

        EmbedBuilder embed = new EmbedBuilder()
        {
            Title = "Top Rated Mods",
            Color = Color.Purple
        };

        foreach (PackageInfo mod in topRatedMods)
        {
            embed.AddField(mod.name, $"Rating Score: {mod.rating_score}\n[More Info]({mod.package_url})", true);
        }

        await ReplyAsync(embed: embed.Build());
    }

    /*
    [Command("modsbycategory")]
    [Discord.Commands.Summary("Lists mods within a specified category.")]
    public async Task GetModsByCategoryAsync([Remainder] string category)
    {
        List<PackageInfo>? mods = await ThunderstoreAPI.GetAllModsFromThunderstore();
        List<PackageInfo> categoryMods = mods.Where(m => m.categories.Contains(category, StringComparer.OrdinalIgnoreCase)).ToList();

        if (!categoryMods.Any())
        {
            await ReplyAsync($"No mods found in the category '{category}'.");
            return;
        }

        EmbedBuilder embed = new EmbedBuilder()
        {
            Title = $"Mods in Category: {category}",
            Color = Color.Teal
        };

        foreach (PackageInfo mod in categoryMods.Take(10)) // Limit to 10 to prevent spam
        {
            embed.AddField(mod.name, $"[More Info]({mod.package_url})", true);
        }

        await ReplyAsync(embed: embed.Build());
    }#1#

    [Command("largestmod")]
    [Discord.Commands.Summary("Displays the mod with the largest file size.")]
    public async Task GetLargestModAsync()
    {
        List<PackageInfo>? mods = await ThunderstoreAPI.GetAllModsFromThunderstore();
        if (mods != null)
        {
            VersionInfo? largestMod = mods.Where(m => !m.is_pinned && !m.name.ToLower().Contains("modpack") && !m.categories.Contains("Modpacks")).SelectMany(m => m.versions)
                .OrderByDescending(v => v.file_size).FirstOrDefault();

            if (largestMod == null)
            {
                await ReplyAsync("No mods found.");
                return;
            }

            EmbedBuilder embed = new EmbedBuilder()
            {
                Title = "Largest Mod by File Size",
                Description = $"Mod: {largestMod.name}\nFile Size: {largestMod.file_size} bytes\n[Download]({largestMod.download_url})",
                Color = Color.DarkBlue
            };

            await ReplyAsync(embed: embed.Build());
        }
    }

    [Command("modsratingrange")]
    [Discord.Commands.Summary("Lists mods within a specified rating score range.")]
    public async Task GetModsByRatingRangeAsync(int minRating, int maxRating)
    {
        List<PackageInfo>? mods = await ThunderstoreAPI.GetAllModsFromThunderstore();
        if (mods != null)
        {
            List<PackageInfo> ratingRangeMods = mods.Where(m => !m.is_pinned && !m.name.ToLower().Contains("modpack") && !m.categories.Contains("Modpacks")).Where(m => m.rating_score >= minRating && m.rating_score <= maxRating).ToList();

            if (ratingRangeMods.Count == 0)
            {
                await ReplyAsync($"No mods found with rating score between {minRating} and {maxRating}.");
                return;
            }

            EmbedBuilder embed = new EmbedBuilder()
            {
                Title = $"Mods with Rating Score between {minRating} and {maxRating}",
                Color = Color.Orange
            };

            foreach (PackageInfo mod in ratingRangeMods.Take(10))
            {
                embed.AddField(mod.name, $"Rating Score: {mod.rating_score}\n[More Info]({mod.package_url})", true);
            }

            await ReplyAsync(embed: embed.Build());
        }
    }

    [Command("mostversions")]
    [Discord.Commands.Summary("Displays the mod with the most versions.")]
    public async Task GetModWithMostVersionsAsync()
    {
        List<PackageInfo>? mods = await ThunderstoreAPI.GetAllModsFromThunderstore();
        if (mods != null)
        {
            PackageInfo? modWithMostVersions = mods.Where(m => !m.is_pinned && !m.name.ToLower().Contains("modpack") && !m.categories.Contains("Modpacks")).OrderByDescending(m => m.versions.Count).FirstOrDefault();

            if (modWithMostVersions == null)
            {
                await ReplyAsync("No mod found.");
                return;
            }

            EmbedBuilder embed = new EmbedBuilder()
            {
                Title = "Mod with Most Versions",
                Description = $"Mod: {modWithMostVersions.name}\nNumber of Versions: {modWithMostVersions.versions.Count}\n[More Info]({modWithMostVersions.package_url})",
                Color = Color.Purple
            };

            await ReplyAsync(embed: embed.Build());
        }
    }

    [Command("modsfilesizerange")]
    [Discord.Commands.Summary("Lists mods within a specified file size range.")]
    public async Task GetModsByFileSizeRangeAsync(int minSize, int maxSize)
    {
        List<PackageInfo>? mods = await ThunderstoreAPI.GetAllModsFromThunderstore();
        if (mods != null)
        {
            List<VersionInfo> sizeRangeMods = mods.Where(m => !m.is_pinned && !m.name.ToLower().Contains("modpack") && !m.categories.Contains("Modpacks")).SelectMany(m => m.versions)
                .Where(v => v.file_size >= minSize && v.file_size <= maxSize)
                .ToList();

            if (!sizeRangeMods.Any())
            {
                await ReplyAsync($"No mods found with file size between {minSize} and {maxSize} bytes.");
                return;
            }

            EmbedBuilder embed = new EmbedBuilder()
            {
                Title = $"Mods with File Size between {minSize} and {maxSize} bytes",
                Color = Color.DarkBlue
            };

            foreach (VersionInfo version in sizeRangeMods.Take(10))
            {
                embed.AddField(version.name, $"File Size: {version.file_size} bytes\n[Download]({version.download_url})", true);
            }

            await ReplyAsync(embed: embed.Build());
        }
    }

    /*[Command("modsbydaterange")]
    [Discord.Commands.Summary("Lists mods created within a specified date range.")]
    public async Task GetModsByDateRangeAsync(DateTime startDate, DateTime endDate)
    {
        List<PackageInfo>? mods = await ThunderstoreAPI.GetAllModsFromThunderstore();
        List<PackageInfo> dateRangeMods = mods.Where(m => DateTime.Parse(m.date_created) >= startDate && DateTime.Parse(m.date_created) <= endDate).ToList();

        if (!dateRangeMods.Any())
        {
            await ReplyAsync($"No mods found created between {startDate.ToShortDateString()} and {endDate.ToShortDateString()}.");
            return;
        }

        EmbedBuilder embed = new EmbedBuilder()
        {
            Title = $"Mods Created between {startDate.ToShortDateString()} and {endDate.ToShortDateString()}",
            Color = Color.Magenta
        };

        foreach (PackageInfo mod in dateRangeMods.Take(10))
        {
            embed.AddField(mod.name, $"Created: {mod.date_created}\n[More Info]({mod.package_url})");
        }

        await ReplyAsync(embed: embed.Build());
    }#1#

    [Command("modupdatefrequency")]
    [Discord.Commands.Summary("Displays how often a specified mod has been updated.")]
    public async Task GetModUpdateFrequencyAsync([Remainder] string modName)
    {
        PackageInfo? modInfo = await ThunderstoreAPI.GetModInfo(modName);
        if (modInfo == null || modInfo.versions == null || !modInfo.versions.Any())
        {
            await ReplyAsync("Mod or versions not found.");
            return;
        }

        DateTime firstVersionDate = DateTime.Parse(modInfo.versions.Last().date_created);
        DateTime lastVersionDate = DateTime.Parse(modInfo.versions.First().date_created);
        double updateFrequency = (lastVersionDate - firstVersionDate).TotalDays / modInfo.versions.Count;

        EmbedBuilder embed = new EmbedBuilder()
        {
            Title = $"{modInfo.name} - Update Frequency",
            Description = $"Total Versions: {modInfo.versions.Count}\nUpdate Frequency: {updateFrequency:F2} days",
            Color = Color.Blue
        };

        await ReplyAsync(embed: embed.Build());
    }

    [Command("mostdependencies")]
    [Discord.Commands.Summary("Displays the mod/modpack with the most dependencies.")]
    public async Task GetModWithMostDependenciesAsync()
    {
        List<PackageInfo>? mods = await ThunderstoreAPI.GetAllModsFromThunderstore();
        if (mods != null)
        {
            PackageInfo modWithMostDependencies = mods.Where(m => m.versions != null && m.versions.Count != 0 && !m.is_pinned)
                .OrderByDescending(m => m.versions.Max(v => v.dependencies.Count))
                .FirstOrDefault();

            if (modWithMostDependencies == null)
            {
                await ReplyAsync("No mods found.");
                return;
            }

            var versionWithMostDependencies = modWithMostDependencies.versions.OrderByDescending(v => v.dependencies.Count).FirstOrDefault();

            EmbedBuilder embed = new EmbedBuilder()
            {
                Title = "Mod with Most Dependencies",
                Description = $"Mod: {modWithMostDependencies.name}\nVersion: {versionWithMostDependencies.version_number}\nDependencies: {versionWithMostDependencies.dependencies.Count}\n[More Info]({modWithMostDependencies.package_url})",
                Color = Color.Teal
            };

            await ReplyAsync(embed: embed.Build());
        }
    }

    /*[Command("topmodauthors")]
    [Discord.Commands.Summary("Displays the top authors with the most mods.")]
    public async Task GetTopModAuthorsAsync()
    {
        List<PackageInfo>? mods = await ThunderstoreAPI.GetAllModsFromThunderstore();
        var authorCounts = mods.Where(m => !m.is_pinned && !m.name.ToLower().Contains("modpack") && !m.categories.Contains("Modpacks")).GroupBy(m => m.owner)
            .Select(g => new { Author = g.Key, Count = g.Count() })
            .OrderByDescending(a => a.Count)
            .Take(10)
            .ToList();

        if (!authorCounts.Any())
        {
            await ReplyAsync("No authors found.");
            return;
        }

        EmbedBuilder embed = new EmbedBuilder()
        {
            Title = "Top Mod Authors",
            Color = Color.Magenta
        };

        foreach (var author in authorCounts)
        {
            embed.AddField(author.Author, $"Number of Mods: {author.Count}");
        }

        await ReplyAsync(embed: embed.Build());
    }#1#

    /*[Command("modupdatehistory")]
    [Discord.Commands.Summary("Displays the update history of a specified mod.")]
    public async Task GetModUpdateHistoryAsync([Remainder] string modName)
    {
        PackageInfo? modInfo = await ThunderstoreAPI.GetModInfo(modName);
        if (modInfo == null || modInfo.versions == null || !modInfo.versions.Any())
        {
            await ReplyAsync("Mod or versions not found.");
            return;
        }

        EmbedBuilder embed = new EmbedBuilder()
        {
            Title = $"{modInfo.name} - Update History",
            Color = Color.DarkGreen
        };

        foreach (VersionInfo version in modInfo.versions.OrderByDescending(v => v.date_created))
        {
            embed.AddField(version.version_number, $"Updated: {version.date_created}\nDownloads: {version.downloads:N0}", true);
        }

        await ReplyAsync(embed: embed.Build());
    }#1#

    /*[Command("moddownloadtrend")]
    [Discord.Commands.Summary("Displays the download trend of a specified mod over time.")]
    public async Task GetModDownloadTrendAsync([Remainder] string modName)
    {
        PackageInfo? modInfo = await ThunderstoreAPI.GetModInfo(modName);
        if (modInfo == null || modInfo.versions == null || !modInfo.versions.Any())
        {
            await ReplyAsync("Mod or versions not found.");
            return;
        }

        EmbedBuilder embed = new EmbedBuilder()
        {
            Title = $"{modInfo.name} - Download Trend",
            Color = Color.DarkBlue
        };

        foreach (VersionInfo version in modInfo.versions.OrderBy(v => v.date_created))
        {
            embed.AddField(version.version_number, $"Downloads: {version.downloads:N0} on {version.date_created}", true);
        }

        await ReplyAsync(embed: embed.Build());
    }#1#

    /*[Command("modsbyyear")]
    [Discord.Commands.Summary("Displays mods created in a specified year.")]
    public async Task GetModsByYearAsync(int year)
    {
        List<PackageInfo>? mods = await ThunderstoreAPI.GetAllModsFromThunderstore();
        List<PackageInfo> yearMods = mods.Where(m=> !m.is_pinned && !m.name.ToLower().Contains("modpack") && !m.categories.Contains("Modpacks")).Where(m => DateTime.Parse(m.date_created).Year == year).ToList();

        if (!yearMods.Any())
        {
            await ReplyAsync($"No mods found created in the year {year}.");
            return;
        }

        EmbedBuilder embed = new EmbedBuilder()
        {
            Title = $"Mods Created in {year}",
            Color = Color.DarkOrange
        };

        foreach (PackageInfo mod in yearMods.Take(10))
        {
            embed.AddField(mod.name, $"Created: {mod.date_created}\n[More Info]({mod.package_url})", true);
        }

        await ReplyAsync(embed: embed.Build());
    }#1#

    [Command("downloadgrowth")]
    [Discord.Commands.Summary("Displays mods with the highest download growth over the past month.")]
    public async Task GetDownloadGrowthModsAsync()
    {
        var mods = await ThunderstoreAPI.GetAllModsFromThunderstore();
        if (mods != null)
        {
            var recentMods = mods.Where(m => !m.is_pinned && !m.name.ToLower().Contains("modpack") && !m.categories.Contains("Modpacks")).Where(m => DateTime.Parse(m.date_updated) > DateTime.UtcNow.AddDays(-30))
                .OrderByDescending(m => m.versions.Where(p => DateTime.Parse(p.date_created) > DateTime.UtcNow.AddDays(-30)).Sum(v => v.downloads))
                .ToList();

            if (!recentMods.Any())
            {
                await ReplyAsync("No mods with significant download growth found.");
                return;
            }

            var embed = new EmbedBuilder()
            {
                Title = "Mods with Highest Download Growth (Last 30 Days)",
                Color = Color.Purple
            };

            foreach (var mod in recentMods.Take(10))
            {
                embed.AddField(mod.name, $"Downloads: {mod.versions.Where(p => DateTime.Parse(p.date_created) > DateTime.UtcNow.AddDays(-30)).Sum(v => v.downloads):N0}\n[More Info]({mod.package_url})", true);
            }

            await ReplyAsync(embed: embed.Build());
        }
    }

    [Command("popularweekly")]
    [Discord.Commands.Summary("Displays the most popular mod by downloads in the last week.")]
    public async Task GetPopularModByWeeklyDownloadsAsync()
    {
        var mods = await ThunderstoreAPI.GetAllModsFromThunderstore();
        if (mods != null)
        {
            var recentMods = mods.Where(m => !m.is_pinned && !m.name.ToLower().Contains("modpack") && !m.categories.Contains("Modpacks")).Where(m => m.versions.Any(v => DateTime.Parse(v.date_created) > DateTime.UtcNow.AddDays(-7)))
                .OrderByDescending(m => m.versions.Where(m => DateTime.Parse(m.date_created) > DateTime.UtcNow.AddDays(-7)).Sum(v => v.downloads))
                .FirstOrDefault();

            if (recentMods == null)
            {
                await ReplyAsync("No mods found.");
                return;
            }

            var embed = new EmbedBuilder()
            {
                Title = "Most Popular Mod by Weekly Downloads",
                Description = $"Mod: {recentMods.name}\nDownloads this week: {recentMods.versions.Where(m => DateTime.Parse(m.date_created) > DateTime.UtcNow.AddDays(-7)).Sum(v => v.downloads):N0}\n[More Info]({recentMods.package_url})",
                Color = Color.Purple
            };

            await ReplyAsync(embed: embed.Build());
        }
    }

    [Command("toptrending")]
    [Discord.Commands.Summary("Displays the top trending mods with the highest increase in downloads over the past week.")]
    public async Task GetTopTrendingModsAsync()
    {
        var mods = await ThunderstoreAPI.GetAllModsFromThunderstore();
        if (mods != null)
        {
            var recentMods = mods.Where(m => !m.is_pinned && !m.name.ToLower().Contains("modpack") && !m.categories.Contains("Modpacks")).Where(m => m.versions.Any(v => DateTime.Parse(v.date_created) > DateTime.UtcNow.AddDays(-7)))
                .OrderByDescending(m => m.versions.Where(m => DateTime.Parse(m.date_created) > DateTime.UtcNow.AddDays(-7)).Sum(v => v.downloads))
                .Take(10)
                .ToList();

            if (!recentMods.Any())
            {
                await ReplyAsync("No trending mods found.");
                return;
            }

            var embed = new EmbedBuilder()
            {
                Title = "Top Trending Mods",
                Color = Color.DarkBlue
            };

            foreach (var mod in recentMods)
            {
                embed.AddField(mod.name, $"Downloads this week: {mod.versions.Where(m => DateTime.Parse(m.date_created) > DateTime.UtcNow.AddDays(-7)).Sum(v => v.downloads):N0}\n[More Info]({mod.package_url})", true);
            }

            await ReplyAsync(embed: embed.Build());
        }
    }

    [Command("toptoday")]
    [Discord.Commands.Summary("Displays the mods with the highest download growth today.")]
    public async Task GetTopToday()
    {
        List<PackageInfo>? mods = await ThunderstoreAPI.GetAllModsFromThunderstore();
        if (mods is { Count: > 0 })
        {
            List<PackageInfo> recentMods = mods.Where(m => !m.is_pinned && !m.name.ToLower().Contains("modpack") && !m.categories.Contains("Modpacks")).Where(m => DateTime.Parse(m.date_updated) > DateTime.UtcNow.AddDays(-1))
                .OrderByDescending(m => m.versions.Where(p => DateTime.Parse(p.date_created) > DateTime.UtcNow.AddDays(-1)).Sum(v => v.downloads))
                .ToList();

            if (recentMods.Count == 0)
            {
                await ReplyAsync("No mods with significant download growth found.");
                return;
            }

            var embed = new EmbedBuilder()
            {
                Title = "Mods with highest download growth, today",
                Color = Color.Purple
            };

            foreach (PackageInfo mod in recentMods.Take(10))
            {
                embed.AddField(mod.name, $"Downloads: {mod.versions.Where(p => DateTime.Parse(p.date_created) > DateTime.UtcNow.AddDays(-1)).Sum(v => v.downloads):N0}\n[More Info]({mod.package_url})", true);
            }

            await ReplyAsync(embed: embed.Build());
        }
        else
        {
            await ReplyAsync("No mods found.");
        }
    }

    [Command("modinfo")]
    public async Task GetDetailedPackageInfo(string authorName, string packageName)
    {
        var package = (ExperimentalPackageInfo)await ThunderstoreAPI.GetPackageInfo(authorName, packageName);
        var embed = new EmbedBuilder()
            .WithTitle($"{package.name} by {package.owner}")
            .WithDescription(package.latest.description)
            //.AddField("Total Downloads", package.total_downloads.ToString(), true)
            .AddField("Date Created", DateTime.Parse(package.date_created).ToShortDateString(), true)
            .AddField("Date Updated", DateTime.Parse(package.date_updated).ToShortDateString(), true)
            .AddField("Latest Version", package.latest.version_number, true)
            .AddField("Older than Ashlands?", DateTime.Parse(package.date_updated) < DateTime.Parse("2024-05-14") ? "Yes" : "No", true)
            .AddField("Website", package.latest.website_url ?? "No website provided", true)
            .WithThumbnailUrl(package.latest.icon)
            .WithColor(Color.Purple)
            //.WithUrl($"https://thunderstore.io/api/experimental/package/{authorName}/{packageName}/")
            .WithUrl("https://thunderstore.io/c/valheim/p/" + authorName + "/" + packageName + "/")
            .Build();
        var embed2 = new EmbedBuilder()
            .WithTitle("Dependencies")
            .WithColor(Color.Purple);

        /*for (int i = 0; i < string.Join(", ", package.latest.dependencies).Length; i += 1024)
        {
            embed2.AddField("Results", string.Join(", ", package.latest.dependencies).Substring(i, Math.Min(1024, string.Join(", ", package.latest.dependencies).Length - i)));
        }#1#

        if (package.latest.dependencies != null)
            embed2.WithDescription(string.Join("\n", package.latest.dependencies));

        await ReplyAsync(embed: embed);
        await ReplyAsync(embed: embed2.Build());
    }

    /*[Command("search")]
    public async Task SearchPackages(string author, string modName, string version = "")
    {
        var searchResults = await ThunderstoreAPI.SearchPackages(author, modName, version);
        if (searchResults.markdown.Length == 0)
        {
            await ReplyAsync("No packages found.");
            return;
        }

        var embed = new EmbedBuilder()
            .WithTitle($"Search Results for '{author}/{modName}/{version}'")
            .WithColor(Color.DarkGreen);

        // searchResults.markdown can't be longer than 1024 characters. For each set of 1024 characters, put it in a field.
        for (int i = 0; i < searchResults.markdown.Length; i += 1024)
        {
            embed.AddField("Results", searchResults.markdown.Substring(i, Math.Min(1024, searchResults.markdown.Length - i)));
        }


        await ReplyAsync(embed: embed.Build());
    }#1#

    [Command("countmodsthisweek")]
    public async Task GetCountOfModsUpdatedThisWeek()
    {
        var mods = await ThunderstoreAPI.GetAllModsFromThunderstore();
        var countUpdated = mods.Count(m => !m.is_pinned && !m.name.ToLower().Contains("modpack") && !m.categories.Contains("Modpacks") && DateTime.Parse(m.date_updated) > DateTime.UtcNow.AddDays(-7));
        var countUploaded = mods.Count(m => !m.is_pinned && !m.name.ToLower().Contains("modpack") && !m.categories.Contains("Modpacks") && DateTime.Parse(m.date_created) > DateTime.UtcNow.AddDays(-7));
        var count = countUpdated + countUploaded;

        // Check if the count is 0 and return a message
        if (count == 0)
        {
            await ReplyAsync("No mods have been updated in the last week.");
            return;
        }

        var embed = new EmbedBuilder()
            .WithTitle($"Number of mods updated in the last week: {count}")
            .AddField("Number of mods updated", countUpdated.ToString(), true)
            .AddField("Number of mods uploaded", countUploaded.ToString(), true)
            .WithColor(Color.DarkGreen);
        await ReplyAsync(embed: embed.Build());
    }

    /*[Command("subscribe")]
    public async Task SubscribeToPackage(string authorName, string packageName)
    {
        bool success = await SubscriptionManager.Subscribe(Context.User.Id, authorName, packageName);
        if (success)
        {
            await ReplyAsync($"You are now subscribed to updates for {packageName} by {authorName}.");
        }
        else
        {
            await ReplyAsync("Failed to subscribe. You might already be subscribed or an error occurred.");
        }
    }

    [Command("unsubscribe")]
    public async Task UnsubscribeFromPackage(string packageName)
    {
        bool success = SubscriptionManager.Unsubscribe(Context.User.Id, packageName);
        if (success)
        {
            await ReplyAsync($"You are now unsubscribed from updates for {packageName}.");
        }
        else
        {
            await ReplyAsync("Failed to unsubscribe. You might not be subscribed.");
        }
    }#1#

    [Command("purgethisshit")]
    [Discord.Commands.Summary("Purges all messages in the current channel.")]
    [Discord.Commands.RequireUserPermission(GuildPermission.ManageMessages)]
    public async Task PurgeAsync()
    {
        var channel = Context.Channel as ITextChannel;
        if (channel == null)
        {
            await ReplyAsync("This command can only be used in text channels.");
            return;
        }

        var messages = await channel.GetMessagesAsync().FlattenAsync();

        var messagesYoungerThan14Days = messages.Where(m => (DateTimeOffset.UtcNow - m.Timestamp).TotalDays <= 14).ToList();

        var messagesOlderThan14Days = messages.Where(m => (DateTimeOffset.UtcNow - m.Timestamp).TotalDays > 14).ToList();

        // Bulk delete messages younger than 14 days
        if (messagesYoungerThan14Days.Any())
        {
            foreach (var batch in messagesYoungerThan14Days.Batch(100))
            {
                await channel.DeleteMessagesAsync(batch);
            }
        }

        // Individually delete messages older than 14 days
        for (int index = 0; index < messagesOlderThan14Days.Count; index++)
        {
            IMessage? message = messagesOlderThan14Days[index];
            // Delete only 6 at a time to avoid hitting rate limits
            if (index % 6 == 0)
            {
                await Task.Delay(2000);
            }

            // Delete only 6 at a time to avoid hitting rate limits
            if (index % 3 == 0)
            {
                await Task.Delay(1000);
            }

            // Delete only 6 at a time to avoid hitting rate limits
            if (index % 1 == 0)
            {
                await Task.Delay(100);
            }


            await message.DeleteAsync();
        }

        await ReplyAsync("All messages have been purged.");
    }

// Helper extension method to batch messages
}

public static class EnumerableExtensions
{
    public static IEnumerable<IEnumerable<T>> Batch<T>(this IEnumerable<T> source, int size)
    {
        T[] bucket = null;
        var count = 0;

        foreach (var item in source)
        {
            if (bucket == null)
                bucket = new T[size];

            bucket[count++] = item;

            if (count != size)
                continue;

            yield return bucket;

            bucket = null;
            count = 0;
        }

        if (bucket != null && count > 0)
            yield return bucket.Take(count);
    }
}*/