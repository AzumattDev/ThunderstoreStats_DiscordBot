// Somewhere shared (e.g., Chunking.cs or a new Utils.cs)
public static class ReadmeSearch
{
    public static List<(int LineNo, string Excerpt)> Find(string markdown, string query, int contextLines = 2, int maxMatches = 10, bool caseInsensitive = true)
    {
        var lines = markdown.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
        var results = new List<(int, string)>();
        if (string.IsNullOrWhiteSpace(query)) return results;

        var comp = caseInsensitive ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var hits = new List<int>();

        for (int i = 0; i < lines.Length && hits.Count < maxMatches; ++i)
            if (lines[i].IndexOf(query, comp) >= 0)
                hits.Add(i);

        foreach (var hit in hits)
        {
            int start = Math.Max(0, hit - contextLines);
            int end = Math.Min(lines.Length - 1, hit + contextLines);

            var excerptLines = new List<string>();
            for (int i = start; i <= end; ++i)
            {
                var line = lines[i];
                // highlight all occurrences of the query (simple, safe)
                excerptLines.Add(Highlight(line, query, comp));
            }

            var block = string.Join("\n", excerptLines);
            // Use a code block to keep formatting tidy in Discord
            var fenced = $"```md\n{block}\n```";
            results.Add((hit + 1, fenced)); // 1-based line number
        }

        return results;
    }

    private static string Highlight(string line, string query, StringComparison comp)
    {
        if (string.IsNullOrEmpty(query)) return line;

        // Non-regex, multi-hit replace with **bold** wrappers
        int idx = 0;
        var sb = new System.Text.StringBuilder();
        while (idx < line.Length)
        {
            int pos = line.IndexOf(query, idx, comp);
            if (pos < 0)
            {
                sb.Append(line, idx, line.Length - idx);
                break;
            }
            sb.Append(line, idx, pos - idx);
            var slice = line.Substring(pos, query.Length);
            sb.Append("**").Append(slice).Append("**");
            idx = pos + query.Length;
        }
        return sb.ToString();
    }
}
