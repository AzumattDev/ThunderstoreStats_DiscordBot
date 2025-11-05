using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ThunderstoreStats_DiscordBot.Profiles;

public sealed class ThunderstoreProfileClient : IDisposable
{
    private static readonly Uri BaseUri = new(ThunderstoreAPI.BaseTsUrl);
    private readonly HttpClient _http;

    public ThunderstoreProfileClient(HttpMessageHandler? handler = null, TimeSpan? timeout = null)
    {
        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _http.BaseAddress = BaseUri;
        _http.Timeout = timeout ?? TimeSpan.FromSeconds(15);
        _http.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("AzumattBot", "1.0")); // be a good citizen
    }

    /// <summary>
    /// Downloads and returns the raw r2modman payload text for a legacy profile (starts with "#r2modman" then base64 ZIP).
    /// Throws on 404/rate limit/other errors with clear messages.
    /// </summary>
    public async Task<string> GetLegacyProfileTextAsync(string? profileCode, CancellationToken ct = default)
    {
        profileCode = (profileCode ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(profileCode))
            throw new ArgumentException("Profile code is empty.", nameof(profileCode));

        // r2modman uses: /api/experimental/legacyprofile/get/{code}/
        Uri url = new($"/api/experimental/legacyprofile/get/{Uri.EscapeDataString(profileCode)}/", UriKind.Relative);

        // Simple retry for 429 / transient network errors
        const int maxAttempts = 5;
        TimeSpan delay = TimeSpan.FromMilliseconds(750);

        for (int attempt = 1; attempt <= maxAttempts; ++attempt)
        {
            using HttpRequestMessage req = new(HttpMethod.Get, url);
            using HttpResponseMessage res = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

            if (res.IsSuccessStatusCode)
            {
                string text = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                if (!text.StartsWith("#r2modman", StringComparison.Ordinal))
                    throw new InvalidDataException("Server response does not start with '#r2modman'. Is this a valid legacy profile code?");
                return text;
            }

            if (res.StatusCode == HttpStatusCode.NotFound)
                throw new KeyNotFoundException("Profile code not found (404). The code may be expired or mistyped.");

            if ((int)res.StatusCode == 429) // Too Many Requests
            {
                if (attempt == maxAttempts)
                    throw new HttpRequestException("Rate limited by Thunderstore (429). Try again later.");

                // Honor Retry-After if present
                TimeSpan sleep = delay;
                if (res.Headers.TryGetValues("Retry-After", out IEnumerable<string>? vals) &&
                    int.TryParse(vals.FirstOrDefault(), out int seconds) &&
                    seconds > 0)
                {
                    sleep = TimeSpan.FromSeconds(seconds);
                }

                await Task.Delay(sleep, ct).ConfigureAwait(false);
                delay = TimeSpan.FromMilliseconds(Math.Min(5000, delay.TotalMilliseconds * 1.8));
                continue;
            }

            // Transient?
            if ((int)res.StatusCode >= 500 && attempt < maxAttempts)
            {
                await Task.Delay(delay, ct).ConfigureAwait(false);
                delay = TimeSpan.FromMilliseconds(Math.Min(5000, delay.TotalMilliseconds * 1.8));
                continue;
            }

            string body = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            throw new HttpRequestException($"Thunderstore request failed ({(int)res.StatusCode} {res.StatusCode}). Body: {body}");
        }

        throw new HttpRequestException("Failed to fetch profile after retries.");
    }

    /// <summary>
    /// Decodes the "#r2modman" base64 content into a ZIP stream in memory.
    /// </summary>
    public static MemoryStream DecodeProfileZip(string legacyProfileText)
    {
        if (!legacyProfileText.StartsWith("#r2modman", StringComparison.Ordinal))
            throw new InvalidDataException("Invalid profile data format.");

        string b64 = legacyProfileText.Substring("#r2modman".Length).Trim();

        // Using Convert.FromBase64String for simplicity & speed
        byte[] zipBytes = Convert.FromBase64String(b64);
        return new MemoryStream(zipBytes, writable: false);
    }

    /// <summary>
    /// Reads the mod list (Author, Name, Version) from the ZIP.
    /// Primary source: manifest.json dependencies (Author-Mod-1.2.3).
    /// Has fallbacks for a couple of common export variants.
    /// </summary>
    public static (IReadOnlyList<ModRef> Mods, string? Community, string? ProfileName) ParseModsFromZip(Stream zipStream)
    {
        using ZipArchive zip = new(zipStream, ZipArchiveMode.Read, leaveOpen: true);

        // A) Try r2x first (most accurate & includes community)
        ZipArchiveEntry? r2x = zip.Entries.FirstOrDefault(e =>
            e.FullName.Equals("export.r2x", StringComparison.OrdinalIgnoreCase));
        if (r2x != null)
        {
            using Stream s = r2x.Open();
            IReadOnlyList<ModRef> mods = ThunderstoreMetadataClient.ParseFromR2x(s, out string? community, out string? profileName);
            if (mods.Count > 0)
                return (mods, community, profileName);
        }

        // B) Fallback to manifest.json (legacy)
        ZipArchiveEntry? manifestEntry = zip.Entries.FirstOrDefault(e =>
                                             e.FullName.Equals("manifest.json", StringComparison.OrdinalIgnoreCase))
                                         ?? zip.Entries.FirstOrDefault(e => e.FullName.EndsWith("/manifest.json", StringComparison.OrdinalIgnoreCase));

        if (manifestEntry != null)
        {
            using Stream s = manifestEntry.Open();
            IReadOnlyList<ModRef> mods = ParseFromManifest(s);
            if (mods.Count > 0)
                return (mods, null, null); // community not provided by old manifest
        }

        // C) Last resort heuristic from BepInEx/config
        IReadOnlyList<ModRef> guesses = GuessFromConfigs(zip);
        return (guesses, null, null);
    }


    private static IReadOnlyList<ModRef> ParseFromManifest(Stream manifestJson)
    {
        ModpackManifest? manifest = JsonSerializer.Deserialize<ModpackManifest>(manifestJson, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        });

        if (manifest?.Dependencies == null || manifest.Dependencies.Count == 0)
            return [];

        List<ModRef> mods = new(manifest.Dependencies.Count);
        foreach (string dep in manifest.Dependencies)
        {
            // Format: "Author-Name-1.2.3"
            if (TryParseDependency(dep, out ModRef mod))
                mods.Add(mod);
        }

        return mods;
    }

    private static bool TryParseDependency(string dependency, out ModRef mod)
    {
        // Safe split from the end so names with dashes still work:
        // We need ...-<version> as the last token; author is the first token; name is everything in between.
        mod = default;
        string[] parts = dependency.Split('-', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3) return false;

        string version = parts[^1].Trim();
        string author = parts[0].Trim();
        string name = string.Join('-', parts.Skip(1).Take(parts.Length - 2)).Trim();

        if (author.Length == 0 || name.Length == 0 || version.Length == 0)
            return false;

        mod = new ModRef(author, name, version, dependency);
        return true;
    }

    private static IReadOnlyList<ModRef> GuessFromConfigs(ZipArchive zip)
    {
        // Best-effort: look under "BepInEx/config/*.cfg" that commonly follow "org.bepinex.plugins.Author_ModName" or similar.
        // We'll try to map "Author.ModName" or "Author_ModName" back to Thunderstore "Author-Name".
        HashSet<ModRef> mods = [];

        foreach (ZipArchiveEntry e in zip.Entries)
        {
            if (!e.FullName.StartsWith("BepInEx/config/", StringComparison.OrdinalIgnoreCase)) continue;
            if (!e.FullName.EndsWith(".cfg", StringComparison.OrdinalIgnoreCase)) continue;

            // Pull something that looks like Author.Name or Author_Name
            string file = Path.GetFileNameWithoutExtension(e.FullName);
            if (string.IsNullOrWhiteSpace(file)) continue;

            // Common patterns:
            // - Azumatt.AzuExtendedPlayerInventory
            // - org.bepinex.valheim.displayinfo  (these are framework mods; skip)
            if (file.StartsWith("org.bepinex", StringComparison.OrdinalIgnoreCase)) continue;

            // Prefer a split on '.' first (Author.NameLikeThis)
            string? author = null, name = null;

            string[] dotParts = file.Split('.', StringSplitOptions.RemoveEmptyEntries);
            if (dotParts.Length >= 2)
            {
                author = dotParts[0];
                name = string.Join('.', dotParts.Skip(1));
            }
            else
            {
                // Fallback: split on '_' (Author_NameLikeThis)
                string[] under = file.Split('_', StringSplitOptions.RemoveEmptyEntries);
                if (under.Length >= 2)
                {
                    author = under[0];
                    name = string.Join('_', under.Skip(1));
                }
            }

            if (author is null || name is null) continue;

            // Unknown version — leave empty so you can still display the pair
            mods.Add(new ModRef(author, name, Version: "", OriginalDependencyString: $"{author}-{name}"));
        }

        return mods.ToList();
    }

    public void Dispose() => _http.Dispose();
}

public readonly record struct ModRef(string Author, string Name, string? Version = null, string? OriginalDependencyString = null)
{
    public override string ToString() => Version is { Length: > 0 } ? $"{Author}-{Name}-{Version}" : $"{Author}-{Name}";
}

// Minimal manifest for r2modman exports
internal sealed class ModpackManifest
{
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("version_number")] public string? Version { get; set; }
    [JsonPropertyName("website_url")] public string? WebsiteUrl { get; set; }
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("dependencies")] public List<string> Dependencies { get; set; } = [];
}

public static class ThunderstoreProfileReader
{
    /// <summary>
    /// High-level helper: given a profile code, fetch, decode and parse mods.
    /// </summary>
    public static async Task<(IReadOnlyList<ModRef> Mods, string? Community, string? ProfileName)>
        GetModsAsync(string? profileCode, CancellationToken ct = default)
    {
        using ThunderstoreProfileClient client = new();
        string text = await client.GetLegacyProfileTextAsync(profileCode, ct);
        using MemoryStream zip = ThunderstoreProfileClient.DecodeProfileZip(text);
        return ThunderstoreProfileClient.ParseModsFromZip(zip);
    }
}