using Newtonsoft.Json;

namespace ThunderstoreStats_DiscordBot;

public class ThunderstoreAPI
{
    private static readonly HttpClient client = new();
    public const string BaseTsUrl = "https://thunderstore.io/";
    public static readonly string BaseApiUrl = $"{BaseTsUrl}api/v1/";
    public static readonly string BaseApiExpUrl = $"{BaseTsUrl}api/experimental/";
    public static readonly string ThunderstoreAllUrl = $"{BaseTsUrl}c/valheim/api/v1/package/";


    public async Task<string> GetPackageInfoRaw(string packageName)
    {
        try
        {
            string url = $"{BaseApiUrl}package/{packageName}/";
            HttpResponseMessage res = await client.GetAsync(url);
            res.EnsureSuccessStatusCode();
            return await res.Content.ReadAsStringAsync();
        }
        catch (HttpRequestException ex)
        {
            return $"Error fetching package info: {ex.Message}";
        }
    }

    public async Task<ExperimentalPackageInfo> GetPackageInfo(string authorName, string packageName)
    {
        HttpResponseMessage res = await client.GetAsync($"{BaseApiExpUrl}package/{authorName}/{packageName}/");
        if (!res.IsSuccessStatusCode) return new ExperimentalPackageInfo();
        string json = await res.Content.ReadAsStringAsync();
        return JsonConvert.DeserializeObject<ExperimentalPackageInfo>(json) ?? new ExperimentalPackageInfo();
    }

    public static async Task<MarkdownPackage> SearchPackages(string author, string modName, string version)
    {
        // Example https://thunderstore.io/api/experimental/package/Azumatt/AzuCraftyBoxes/1.6.1/readme/
        HttpResponseMessage response = await client.GetAsync($"{BaseApiExpUrl}package/{author}/{modName}/{version}/readme/");
        if (!response.IsSuccessStatusCode)
            return new MarkdownPackage();

        string json = await response.Content.ReadAsStringAsync();
        MarkdownPackage? result = JsonConvert.DeserializeObject<MarkdownPackage>(json);
        return result;
    }

    public static async Task<MarkdownPackage> GetChangelog(string author, string modName, string version)
    {
        // Example https://thunderstore.io/api/experimental/package/Azumatt/AzuCraftyBoxes/1.6.1/readme/
        HttpResponseMessage response = await client.GetAsync($"{BaseApiExpUrl}package/{author}/{modName}/{version}/changelog/");
        if (!response.IsSuccessStatusCode)
            return new MarkdownPackage();

        string json = await response.Content.ReadAsStringAsync();
        MarkdownPackage? result = JsonConvert.DeserializeObject<MarkdownPackage>(json);
        return result;
    }

    public static async Task<List<PackageInfo>> GetAllModsFromThunderstore()
    {
        try
        {
            if (Program.LastRan.AddHours(1) < DateTime.UtcNow || Program.AllPackages is null || Program.AllPackages.Count == 0)
            {
                Console.WriteLine("Fetching package data from Thunderstore...");
                HttpResponseMessage res = await client.GetAsync(ThunderstoreAllUrl);
                res.EnsureSuccessStatusCode();
                string json = await res.Content.ReadAsStringAsync();
                List<PackageInfo> packages = JsonConvert.DeserializeObject<List<PackageInfo>>(json) ?? [];
                Program.AllPackages = packages;
                Program.LastRan = DateTime.UtcNow;
            }

            return Program.AllPackages!;
        }
        catch (Exception ex)
        {
            Console.WriteLine("Failed to fetch or parse package data: " + ex.Message);
            return [];
        }
    }

    public static async Task<List<PackageInfo>> GetLatestMods()
    {
        List<PackageInfo> packages = await GetAllModsFromThunderstore();
        return packages.OrderByDescending(p => p.date_created).Take(10).ToList();
    }


    public async Task<PackageInfo?> GetModInfo(string modName)
    {
        List<PackageInfo> packages = await GetAllModsFromThunderstore();
        return packages.FirstOrDefault(p => p.name.Equals(modName, StringComparison.OrdinalIgnoreCase));
    }

    public static async Task<List<PackageInfo>> GetModsByAuthor(string author)
    {
        List<PackageInfo> packages = await GetAllModsFromThunderstore();
        return packages.Where(p => p.owner.Equals(author, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    public static async Task<AuthorStats> GetAuthorStats(string authorName)
    {
        try
        {
            List<PackageInfo> packages = await GetAllModsFromThunderstore();

            // Filter the packages by author name
            List<PackageInfo> authorPackages = packages.Where(p => p.owner == authorName).ToList();

            if (authorPackages == null || !authorPackages.Any())
                return new AuthorStats { mods_count = 0, total_downloads = 0, average_downloads = 0, median_downloads = 0, medianDownloadsMultiplied = 0, most_downloaded_mod = "", topModsSorted = [] };

            // Calculating average downloads of top 3 mods
            double topThreeAverage = authorPackages.Where(m => !m.is_pinned && !m.name.ToLower().Contains("modpack") && !m.categories.Contains("Modpacks"))
                .OrderByDescending(p => p.versions.Sum(v => v.downloads))
                .Take(3)
                .Average(p => p.versions.Sum(v => v.downloads));


            int total_downloads = authorPackages.Where(m => !m.categories.Contains("Modpacks")).Sum(p => p.versions?.Sum(v => v.downloads) ?? 0);
            int average_downloads = total_downloads / authorPackages.Count;

            List<int> downloads = authorPackages.Where(m => !m.categories.Contains("Modpacks")).Select(p => p.versions?.Sum(v => v.downloads) ?? 0).ToList();
            int median_downloads = CalculateMedian(downloads);
            int medianDownloadsMultiplied = median_downloads * authorPackages.Count;
            /*downloads.Sort();
            int median_downloads = downloads.Count % 2 == 0
                ? (downloads[downloads.Count / 2] + downloads[downloads.Count / 2 - 1]) / 2
                : downloads[downloads.Count / 2];*/

            AuthorStats stats = new()
            {
                mods_count = authorPackages.Count,
                total_downloads = total_downloads,
                average_downloads = average_downloads,
                median_downloads = median_downloads,
                medianDownloadsMultiplied = medianDownloadsMultiplied,
                most_downloaded_mod = " [" + (authorPackages.MaxBy(p => p.versions?.Sum(v => v.downloads) ?? 0)?.name ?? "") + "](" + authorPackages.MaxBy(p => p.versions?.Sum(v => v.downloads) ?? 0)?.package_url + ")" ?? "",
                topModsSorted = authorPackages.Where(m => m.categories != null && m is { name: not null, is_pinned: false } && !m.name.Contains("modpack", StringComparison.CurrentCultureIgnoreCase) && !m.categories.Contains("Modpacks"))
                    .OrderByDescending(p => p.versions?.Sum(v => v.downloads))
                    .ToList()
            };

            Console.WriteLine("Packages count for " + authorName + ": " + authorPackages.Count);
            Console.WriteLine("Total downloads for " + authorName + ": " + total_downloads);
            Console.WriteLine("Average downloads for " + authorName + ": " + average_downloads);
            Console.WriteLine("Median downloads for " + authorName + ": " + median_downloads);
            Console.WriteLine("Median downloads multiplied for " + authorName + ": " + medianDownloadsMultiplied);
            Console.WriteLine("Most downloaded mod for " + authorName + ": " + stats.most_downloaded_mod);
            Console.WriteLine("Top mods for " + authorName + ": " + string.Join(", ", stats.topModsSorted.Select(m => m.name)));

            return stats;
        }
        catch (Exception ex)
        {
            Console.WriteLine("Failed to fetch or parse author package data: " + ex.Message);
            return new AuthorStats { mods_count = 0, total_downloads = 0, average_downloads = 0, median_downloads = 0, medianDownloadsMultiplied = 0, most_downloaded_mod = "", topModsSorted = [] };
        }
    }

    public static async Task<Dictionary<string, AuthorStats>> GetAuthorsWithAtLeastFiveMods()
    {
        try
        {
            List<PackageInfo> packages = await GetAllModsFromThunderstore();

            Dictionary<string, List<PackageInfo>> authorGroups = packages.Where(m => !m.is_pinned && !m.name.ToLower().Contains("modpack") && !m.categories.Contains("Modpacks")).GroupBy(p => p.owner).Where(g => g.Count() >= 5)
                .ToDictionary(g => g.Key, g => g.ToList());

            // Calculate global median downloads
            List<int> allDownloads = packages.Where(m => !m.is_pinned && !m.categories.Contains("Modpacks") && !m.name.ToLower().Contains("modpack") && m.versions.All(v => v.dependencies.Count <= 4) && m.name != "r2modman" && m.name != "BepInExPack_Valheim").SelectMany(p => p.versions).Select(v => v.downloads).OrderBy(d => d).ToList();
            int globalMedianDownloads = CalculateMedian(allDownloads);
            int globalMedianMultiplied = globalMedianDownloads * packages.Count;

            Dictionary<string, AuthorStats> authorStats = new();

            foreach (KeyValuePair<string, List<PackageInfo>> authorGroup in authorGroups)
            {
                string? authorName = authorGroup.Key;
                List<PackageInfo> authorPackages = authorGroup.Value;

                int totalDownloads = authorPackages.Sum(p => p.versions?.Sum(v => v.downloads) ?? 0);
                int averageDownloads = totalDownloads / authorPackages.Count;

                List<int> downloadCounts = authorPackages.Select(p => p.versions?.Sum(v => v.downloads) ?? 0).ToList();
                int medianDownloads = CalculateMedian(downloadCounts);

                authorStats[authorName] = new AuthorStats
                {
                    mods_count = authorPackages.Count,
                    total_downloads = totalDownloads,
                    average_downloads = averageDownloads,
                    median_downloads = medianDownloads,
                    medianDownloadsMultiplied = medianDownloads * authorPackages.Count,
                    most_downloaded_mod = " [" + (authorPackages.MaxBy(p => p.versions?.Sum(v => v.downloads) ?? 0)?.name ?? "") + "](" + authorPackages.MaxBy(p => p.versions?.Sum(v => v.downloads) ?? 0)?.package_url + ")" ?? "",
                    global_median_downloads = globalMedianMultiplied
                };
            }

            return authorStats;
        }
        catch (Exception ex)
        {
            Console.WriteLine("Failed to fetch or parse package data: " + ex.Message);
            return new Dictionary<string, AuthorStats>();
        }
    }

    public static async Task<string?> GetLatestVersion(string author, string name)
    {
        HttpResponseMessage res = await client.GetAsync($"{BaseApiExpUrl}package/{author}/{name}/");
        if (!res.IsSuccessStatusCode) return null;
        string json = await res.Content.ReadAsStringAsync();
        ExperimentalPackageInfo? pkg = JsonConvert.DeserializeObject<ExperimentalPackageInfo>(json);
        return pkg?.latest?.version_number;
    }

    public static async Task<string?> GetReadmeMarkdown(string author, string name, string? versionOrNull = null)
    {
        string? version = string.IsNullOrWhiteSpace(versionOrNull) ? await GetLatestVersion(author, name) : versionOrNull;
        if (string.IsNullOrWhiteSpace(version)) return null;

        HttpResponseMessage response = await client.GetAsync($"{BaseApiExpUrl}package/{author}/{name}/{version}/readme/");
        if (!response.IsSuccessStatusCode) return null;

        string json = await response.Content.ReadAsStringAsync();
        MarkdownPackage? result = JsonConvert.DeserializeObject<MarkdownPackage>(json);
        return result?.markdown;
    }


    private static int CalculateMedian(List<int> downloads)
    {
        int numberCount = downloads.Count;
        int halfIndex = downloads.Count / 2;
        List<int> sortedNumbers = downloads.OrderBy(n => n).ToList();
        if ((numberCount % 2) == 0)
        {
            return (sortedNumbers[halfIndex] + sortedNumbers[halfIndex - 1]) / 2;
        }

        return sortedNumbers[halfIndex];
    }
}

public class Package
{
    public string Name { get; set; }
    public int Downloads { get; set; }
}

public class MarkdownPackage
{
    public string markdown { get; set; }
}

public class AuthorStats
{
    public int total_downloads { get; set; }
    public int average_downloads { get; set; }
    public int median_downloads { get; set; }

    public int medianDownloadsMultiplied { get; set; }
    public int mods_count { get; set; }

    public string most_downloaded_mod { get; set; }

    public List<PackageInfo> topModsSorted { get; set; } = [];

    public int global_median_downloads { get; set; }
}

public static class SubscriptionManager
{
    private static Dictionary<string, HashSet<ulong>> subscriptions = new();

    public static bool Subscribe(ulong userId, string packageName)
    {
        if (!subscriptions.ContainsKey(packageName))
        {
            subscriptions[packageName] = [];
        }

        return subscriptions[packageName].Add(userId);
    }

    public static bool Unsubscribe(ulong userId, string packageName)
    {
        if (subscriptions.ContainsKey(packageName))
        {
            return subscriptions[packageName].Remove(userId);
        }

        return false;
    }
}