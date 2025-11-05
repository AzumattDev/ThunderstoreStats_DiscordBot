using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ThunderstoreStats_DiscordBot.Profiles;

public sealed class ThunderstoreMetadataClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly string _community;

    public ThunderstoreMetadataClient(string community = "valheim", HttpMessageHandler? handler = null, TimeSpan? timeout = null)
    {
        _community = community;
        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _http.BaseAddress = new Uri(ThunderstoreAPI.BaseTsUrl);
        _http.Timeout = timeout ?? TimeSpan.FromSeconds(10);
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("AzumattBot", "1.0"));
    }

    private static readonly Dictionary<string, (DateTimeOffset when, TS_Package[] data)> Cache = new();

    public async Task<TS_Package[]> GetAllPackagesAsync(CancellationToken ct = default)
    {
        string key = _community.ToLowerInvariant();
        if (Cache.TryGetValue(key, out (DateTimeOffset when, TS_Package[] data) hit) && (DateTimeOffset.UtcNow - hit.when) < TimeSpan.FromMinutes(5))
            return hit.data;

        // e.g. https://thunderstore.io/c/valheim/api/v1/package/
        string url = $"/c/{Uri.EscapeDataString(_community)}/api/v1/package/";
        using HttpRequestMessage req = new(HttpMethod.Get, url);
        using HttpResponseMessage res = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        res.EnsureSuccessStatusCode();

        await using Stream s = await res.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        TS_Package[] data = await JsonSerializer.DeserializeAsync<TS_Package[]>(s, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        }, ct).ConfigureAwait(false) ?? [];

        Cache[key] = (DateTimeOffset.UtcNow, data);
        return data;
    }

    internal static IReadOnlyList<ModRef> ParseFromR2x(Stream r2x, out string? community, out string? profileName)
    {
        // r2x is tiny; read to string
        using StreamReader sr = new(r2x, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        string text = sr.ReadToEnd();

        // Very light YAML parsing (no external deps). Handles the typical structure:
        // profileName: Foo
        // mods:
        // - name: Author-Package-Can-Have-Dashes
        //   version: { major: 1, minor: 2, patch: 3 }
        //   enabled: true
        // community: valheim

        Match mProfile = Regex.Match(text, @"(?m)^\s*profileName\s*:\s*(?<p>.+?)\s*$");
        profileName = mProfile.Success ? mProfile.Groups["p"].Value.Trim() : null;

        community = null;

        // community: <word>
        Match mCommunity = Regex.Match(text, @"(?m)^\s*community\s*:\s*(?<c>[A-Za-z0-9_\-]+)\s*$");
        if (mCommunity.Success) community = mCommunity.Groups["c"].Value.Trim();

        // Iterate mods block line-by-line to keep this resilient to spacing.
        List<ModRef> mods = [];
        using StringReader reader = new(text);

        string? line;
        string? fullName = null;
        int? maj = null, min = null, pat = null;
        bool enabled = true;
        bool inMods = false, inEntry = false;

        while ((line = reader.ReadLine()) != null)
        {
            // Find start of mods:
            if (!inMods)
            {
                if (Regex.IsMatch(line, @"(?m)^\s*mods\s*:\s*$")) inMods = true;
                continue;
            }

            // Start of a new list item: "- name: ..."
            Match nameMatch = Regex.Match(line, @"^\s*-\s*name\s*:\s*(?<n>.+?)\s*$");
            if (nameMatch.Success)
            {
                // flush any previous
                if (inEntry && fullName is { Length: > 0 } && maj.HasValue && min.HasValue && pat.HasValue && enabled)
                {
                    string version = $"{maj.Value}.{min.Value}.{pat.Value}";
                    // Split Author-Name into author + name: split on first '-'
                    string fn = fullName.Trim();
                    int dash = fn.IndexOf('-');
                    if (dash > 0)
                    {
                        string author = fn.Substring(0, dash);
                        string pkg = fn.Substring(dash + 1);
                        mods.Add(new ModRef(author, pkg, version, $"{author}-{pkg}-{version}"));
                    }
                }

                // reset for new entry
                inEntry = true;
                fullName = nameMatch.Groups["n"].Value.Trim();
                maj = min = pat = null;
                enabled = true;
                continue;
            }

            if (!inEntry) continue;

            // version.major/minor/patch lines (either inline map or indented fields)
            Match majMatch = Regex.Match(line, @"^\s*major\s*:\s*(?<v>\d+)\s*$");
            if (majMatch.Success)
            {
                maj = int.Parse(majMatch.Groups["v"].Value);
                continue;
            }

            Match minMatch = Regex.Match(line, @"^\s*minor\s*:\s*(?<v>\d+)\s*$");
            if (minMatch.Success)
            {
                min = int.Parse(minMatch.Groups["v"].Value);
                continue;
            }

            Match patMatch = Regex.Match(line, @"^\s*patch\s*:\s*(?<v>\d+)\s*$");
            if (patMatch.Success)
            {
                pat = int.Parse(patMatch.Groups["v"].Value);
                continue;
            }

            Match enabledMatch = Regex.Match(line, @"^\s*enabled\s*:\s*(?<v>true|false)\s*$", RegexOptions.IgnoreCase);
            if (enabledMatch.Success)
            {
                enabled = bool.Parse(enabledMatch.Groups["v"].Value);
                continue;
            }
        }

        // flush last entry
        if (inEntry && fullName is { Length: > 0 } && maj.HasValue && min.HasValue && pat.HasValue && enabled)
        {
            string version = $"{maj.Value}.{min.Value}.{pat.Value}";
            string fn = fullName.Trim();
            int dash = fn.IndexOf('-');
            if (dash > 0)
            {
                string author = fn.Substring(0, dash);
                string pkg = fn.Substring(dash + 1);
                mods.Add(new ModRef(author, pkg, version, $"{author}-{pkg}-{version}"));
            }
        }

        return mods;
    }


    public void Dispose() => _http.Dispose();
}

public sealed class TS_Package
{
    public string? Namespace { get; set; } // author
    public string? Name { get; set; } // package name
    public string? Full_Name { get; set; } // "Author-Name"
    public TS_Version[] Versions { get; set; } = [];
}

public sealed class TS_Version
{
    public string? Version_Number { get; set; } // "1.2.3"
    public string? Icon { get; set; } // absolute URL
    public string? Description { get; set; }
    public string? Download_Url { get; set; }
    public string[] Dependencies { get; set; } = [];
}