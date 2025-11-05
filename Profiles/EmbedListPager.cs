using System.Text;
using Discord;

namespace ThunderstoreStats_DiscordBot.Profiles;

public static class EmbedListPager
{
    /// <summary>
    /// Builds paged embeds with a single-line entry per mod in the description.
    /// </summary>
    public static IReadOnlyList<Embed> BuildModListEmbeds(
        IEnumerable<EnrichedMod> mods,
        string title,
        string community,
        Color color,
        int softPageBudget = 3500,
        string? header = null,
        string? footer = null)
    {
        List<EnrichedMod> list = mods.ToList();
        if (list.Count == 0) return [];

        List<Embed> pages = [];
        StringBuilder sb = new(softPageBudget + 256);

        if (!string.IsNullOrWhiteSpace(header))
        {
            sb.AppendLine(header!.TrimEnd());
            sb.AppendLine();
        }

        int startIndexForPage = 0;
        for (int i = 0; i < list.Count; ++i)
        {
            string line = LineOf(list[i], community);

            // If adding this line would exceed the budget, flush a page
            if (sb.Length + line.Length + 1 > softPageBudget && sb.Length > 0)
            {
                pages.Add(MakeEmbed(sb.ToString(), title, color, pages.Count + 1, 0, list.Count, startIndexForPage, i - 1, footer));
                sb.Clear();
                startIndexForPage = i;

                if (!string.IsNullOrWhiteSpace(header))
                {
                    sb.AppendLine(header!.TrimEnd());
                    sb.AppendLine();
                }
            }

            sb.AppendLine(line);
        }

        if (sb.Length > 0)
            pages.Add(MakeEmbed(sb.ToString(), title, color, pages.Count + 1, 0, list.Count, startIndexForPage, list.Count - 1, footer));

        // fix the page count in footers now that we know N
        if (pages.Count > 1)
        {
            for (int i = 0; i < pages.Count; ++i)
            {
                EmbedBuilder? eb = pages[i].ToEmbedBuilder();
                string baseFooter = footer ?? "";
                string pg = $"Page {i + 1}/{pages.Count}";
                eb.WithFooter(string.IsNullOrWhiteSpace(baseFooter) ? pg : $"{baseFooter} • {pg}");
                pages[i] = eb.Build();
            }
        }

        return pages;

        // Each line: "• [Author-Name](url)_`version`"
        // No code block so links stay clickable.
        static string LineOf(EnrichedMod m, string community)
        {
            // e.g. https://thunderstore.io/c/valheim/p/Azumatt/AzuCraftyBoxes/
            string url = $"{ThunderstoreAPI.BaseTsUrl}c/{community}/p/{Uri.EscapeDataString(m.Author)}/{Uri.EscapeDataString(m.Name)}/";
            string ver = string.IsNullOrWhiteSpace(m.Version) ? "" : $" `{m.Version}`";
            return $"• [{m.Author}-{m.Name}]({url}) {ver}";
        }

        static Embed MakeEmbed(string desc, string title, Color color, int pageOneBased, int totalPages, int totalItems, int startIdx, int endIdx, string? footer)
        {
            EmbedBuilder? eb = new EmbedBuilder().WithTitle(title).WithDescription(desc).WithColor(color);

            // “shown X–Y of Z • Page i/N”
            string shown = totalItems <= 0 ? "" : $"Shown {startIdx + 1}–{endIdx + 1} of {totalItems}";
            string pageBit = totalPages <= 0 ? "" : $"Page {pageOneBased}/{Math.Max(1, totalPages)}";

            List<string> footerParts = [];
            if (!string.IsNullOrWhiteSpace(footer)) footerParts.Add(footer!);
            if (!string.IsNullOrWhiteSpace(shown)) footerParts.Add(shown);
            if (!string.IsNullOrWhiteSpace(pageBit)) footerParts.Add(pageBit);

            if (footerParts.Count > 0)
                eb.WithFooter(string.Join(" • ", footerParts));

            return eb.Build();
        }
    }
}