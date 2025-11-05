using System.Collections.Concurrent;
using Discord;

namespace ThunderstoreStats_DiscordBot;

// Expiring page cache so memory can’t grow forever.
public static class PagerStore
{
    private static readonly ConcurrentDictionary<string, (IReadOnlyList<Embed> Pages, DateTimeOffset Expires)> Pages = new();
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(20);

    public static string Put(IReadOnlyList<Embed> pages)
    {
        string key = Guid.NewGuid().ToString("N");
        Pages[key] = (pages, DateTimeOffset.UtcNow + Ttl);
        _ = Task.Run(SweepAsync); // opportunistic cleanup
        return key;
    }

    public static bool TryGet(string key, out IReadOnlyList<Embed> pages)
    {
        pages = [];
        if (!Pages.TryGetValue(key, out (IReadOnlyList<Embed> Pages, DateTimeOffset Expires) tuple)) return false;
        if (DateTimeOffset.UtcNow > tuple.Expires)
        {
            Pages.TryRemove(key, out _);
            return false;
        }

        pages = tuple.Pages;
        return true;
    }

    private static Task SweepAsync()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        foreach (KeyValuePair<string, (IReadOnlyList<Embed> Pages, DateTimeOffset Expires)> kv in Pages)
        {
            if (kv.Value.Expires <= now)
                Pages.TryRemove(kv.Key, out _);
        }

        return Task.CompletedTask;
    }
}

public static class PagerUi
{
    public static MessageComponent Build(string key, int index, int total)
    {
        bool prevDisabled = index <= 0;
        bool nextDisabled = index >= total - 1;

        ActionRowBuilder? row = new ActionRowBuilder()
            .WithButton("⟨ Prev", $"pager:{key}:{index}:prev", ButtonStyle.Secondary, disabled: prevDisabled)
            .WithButton($"{index + 1}/{total}", $"pager:{key}:{index}:noop", ButtonStyle.Secondary, disabled: true)
            .WithButton("Next ⟩", $"pager:{key}:{index}:next", ButtonStyle.Secondary, disabled: nextDisabled);

        return new ComponentBuilder().AddRow(row).Build();
    }
}