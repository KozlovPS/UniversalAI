using System.Text;
using System.Text.Encodings.Web;
using System.Text.RegularExpressions;

namespace UniversalAI.Web.Components;

/// <summary>Small, HTML-free report renderer for the Markdown emitted by the benchmarks.</summary>
public static class SafeReportMarkdown
{
    private static string Inline(string text)
    {
        string safe = HtmlEncoder.Default.Encode(text);
        safe = Regex.Replace(safe, @"`([^`]+)`", "<code>$1</code>");
        return Regex.Replace(safe, @"\*\*(.+?)\*\*", "<strong>$1</strong>");
    }
    private static string[] Cells(string line)
    {
        string row = line.Trim();
        if (row.StartsWith('|')) row = row[1..];
        if (row.EndsWith('|') && (row.Length < 2 || row[^2] != '\\')) row = row[..^1];
        return Regex.Split(row, @"(?<!\\)\|").Select(c => c.Trim().Replace(@"\|", "|")).ToArray();
    }
    private static bool Separator(string line) => line.Contains('|') && Cells(line).All(c => Regex.IsMatch(c, @"^:?-{3,}:?$"));
    public static string Render(string markdown)
    {
        var lines = markdown.Replace("\r\n", "\n").Split('\n');
        var html = new StringBuilder();
        int i = 0;
        while (i < lines.Length)
        {
            string line = lines[i].Trim();
            if (line.Length == 0) { i++; continue; }
            if (line.StartsWith("```", StringComparison.Ordinal))
            {
                i++; var code = new StringBuilder();
                while (i < lines.Length && !lines[i].TrimStart().StartsWith("```", StringComparison.Ordinal)) code.AppendLine(lines[i++]);
                if (i < lines.Length) i++;
                html.Append("<pre><code>").Append(HtmlEncoder.Default.Encode(code.ToString())).Append("</code></pre>");
            }
            else if (i + 1 < lines.Length && line.Contains('|') && Separator(lines[i + 1]))
            {
                string[] headers = Cells(line);
                html.Append("<div class=report-table-scroll><table><thead><tr>");
                foreach (string cell in headers) html.Append("<th scope=col>").Append(Inline(cell)).Append("</th>");
                html.Append("</tr></thead><tbody>"); i += 2;
                while (i < lines.Length && lines[i].Trim().Length > 0 && lines[i].Contains('|'))
                {
                    string[] cells = Cells(lines[i++]); html.Append("<tr>");
                    for (int c = 0; c < headers.Length; c++) html.Append("<td>").Append(Inline(c < cells.Length ? cells[c] : "")).Append("</td>");
                    html.Append("</tr>");
                }
                html.Append("</tbody></table></div>");
            }
            else if (Regex.Match(line, @"^(#{1,6})\s+(.+)$") is { Success: true } heading)
            {
                int level = Math.Min(4, heading.Groups[1].Length + 1);
                html.Append($"<h{level}>").Append(Inline(heading.Groups[2].Value)).Append($"</h{level}>"); i++;
            }
            else if (Regex.IsMatch(line, @"^(?:[-*]|👉)\s+|^\d+\.\s+"))
            {
                bool ordered = Regex.IsMatch(line, @"^\d+\.\s+");
                string tag = ordered ? "ol" : "ul";
                html.Append(ordered ? $"<ol start=\"{Regex.Match(line, @"^\d+").Value}\">" : "<ul>");
                string pattern = ordered ? @"^\d+\.\s+" : @"^(?:[-*]|👉)\s+";
                while (i < lines.Length && Regex.IsMatch(lines[i].Trim(), pattern))
                    html.Append("<li>").Append(Inline(Regex.Replace(lines[i++].Trim(), pattern, ""))).Append("</li>");
                html.Append($"</{tag}>");
            }
            else if (line.StartsWith("> ", StringComparison.Ordinal))
            { html.Append("<blockquote>").Append(Inline(line[2..])).Append("</blockquote>"); i++; }
            else { html.Append("<p>").Append(Inline(lines[i++])).Append("</p>"); }
        }
        return html.ToString();
    }
}
