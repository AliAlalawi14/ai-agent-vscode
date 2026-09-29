using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;

namespace Ai_Agent.Web
{
    /// <summary>
    /// A web page as readable Markdown for the model: the main content (main/article when present), headings,
    /// paragraphs, lists, links, code and simple tables; scripts, styles, navigation, forms and other page
    /// furniture dropped.
    /// </summary>
    public static class HtmlToMarkdown
    {
        private static readonly HashSet<string> Skipped = new(StringComparer.OrdinalIgnoreCase)
        {
            "script", "style", "noscript", "template", "svg", "canvas", "iframe", "object", "embed",
            "nav", "footer", "aside", "form", "button", "select", "input", "textarea", "dialog", "head"
        };

        private static readonly HashSet<string> Blocks = new(StringComparer.OrdinalIgnoreCase)
        {
            "p", "div", "section", "article", "main", "header", "figure", "figcaption", "blockquote", "address", "details", "summary", "dl", "dt", "dd"
        };

        public static (string Title, string Markdown) Convert(string html, Uri? baseUri = null)
        {
            var document = new HtmlParser().ParseDocument(html);
            var title = Collapse(document.Title ?? string.Empty);

            // The page's main content when it says where it is; otherwise the body
            var root = (IElement?)document.QuerySelector("main")
                       ?? document.QuerySelector("article")
                       ?? document.QuerySelector("[role=main]")
                       ?? document.Body;
            if (root == null) return (title, string.Empty);

            var sb = new StringBuilder();
            Walk(root, sb, baseUri, listDepth: 0);
            var markdown = Regex.Replace(sb.ToString(), @"[ \t]+\n", "\n");
            markdown = Regex.Replace(markdown, @"\n{3,}", "\n\n").Trim();
            return (title, markdown);
        }

        private static void Walk(INode node, StringBuilder sb, Uri? baseUri, int listDepth)
        {
            foreach (var child in node.ChildNodes)
            {
                if (child is IText text)
                {
                    sb.Append(Collapse(text.Data, keepEdges: true));
                    continue;
                }
                if (child is not IElement el) continue;

                var tag = el.LocalName;
                if (Skipped.Contains(tag) || el.HasAttribute("hidden") || el.GetAttribute("aria-hidden") == "true") continue;
                if (IsCookieOrBanner(el)) continue;

                switch (tag)
                {
                    case "h1": case "h2": case "h3": case "h4": case "h5": case "h6":
                        var level = tag[1] - '0';
                        NewBlock(sb);
                        sb.Append(new string('#', level)).Append(' ').Append(Collapse(el.TextContent)).Append("\n\n");
                        break;
                    case "br":
                        sb.Append('\n');
                        break;
                    case "hr":
                        NewBlock(sb);
                        sb.Append("---\n\n");
                        break;
                    case "pre":
                        NewBlock(sb);
                        var lang = el.QuerySelector("code")?.ClassList.FirstOrDefault(c => c.StartsWith("language-"))?["language-".Length..] ?? "";
                        sb.Append("```").Append(lang).Append('\n').Append(el.TextContent.TrimEnd()).Append("\n```\n\n");
                        break;
                    case "code":
                        sb.Append('`').Append(el.TextContent.Trim()).Append('`');
                        break;
                    case "a":
                        var label = Collapse(el.TextContent);
                        var href = el.GetAttribute("href");
                        if (string.IsNullOrEmpty(label)) break;
                        if (string.IsNullOrEmpty(href) || href.StartsWith("#") || href.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase))
                        {
                            sb.Append(label);
                            break;
                        }
                        var absolute = baseUri != null && Uri.TryCreate(baseUri, href, out var resolved) ? resolved.ToString() : href;
                        sb.Append('[').Append(label).Append("](").Append(absolute).Append(')');
                        break;
                    case "img":
                        var alt = Collapse(el.GetAttribute("alt") ?? string.Empty);
                        if (alt.Length > 0) sb.Append("[image: ").Append(alt).Append(']');
                        break;
                    case "strong": case "b":
                        var bold = Collapse(el.TextContent);
                        if (bold.Length > 0) sb.Append("**").Append(bold).Append("**");
                        break;
                    case "em": case "i":
                        var italic = Collapse(el.TextContent);
                        if (italic.Length > 0) sb.Append('*').Append(italic).Append('*');
                        break;
                    case "ul": case "ol":
                        NewBlock(sb);
                        var n = 1;
                        foreach (var li in el.Children.Where(c => c.LocalName == "li"))
                        {
                            sb.Append(new string(' ', listDepth * 2)).Append(tag == "ol" ? $"{n++}. " : "- ");
                            var item = new StringBuilder();
                            Walk(li, item, baseUri, listDepth + 1);
                            sb.Append(item.ToString().Trim()).Append('\n');
                        }
                        sb.Append('\n');
                        break;
                    case "table":
                        NewBlock(sb);
                        AppendTable(el, sb);
                        break;
                    default:
                        if (Blocks.Contains(tag)) NewBlock(sb);
                        if (tag == "blockquote") sb.Append("> ");
                        Walk(el, sb, baseUri, listDepth);
                        if (Blocks.Contains(tag)) sb.Append("\n\n");
                        break;
                }
            }
        }

        private static void AppendTable(IElement table, StringBuilder sb)
        {
            var rows = table.QuerySelectorAll("tr").Take(60).ToList();
            if (rows.Count == 0) return;
            var width = rows.Max(r => r.Children.Count(c => c.LocalName is "td" or "th"));
            if (width == 0) return;
            for (var i = 0; i < rows.Count; i++)
            {
                var cells = rows[i].Children.Where(c => c.LocalName is "td" or "th")
                    .Select(c => Collapse(c.TextContent).Replace("|", "\\|")).ToList();
                while (cells.Count < width) cells.Add("");
                sb.Append("| ").Append(string.Join(" | ", cells)).Append(" |\n");
                if (i == 0) sb.Append('|').Append(string.Concat(Enumerable.Repeat(" --- |", width))).Append('\n');
            }
            sb.Append('\n');
        }

        private static bool IsCookieOrBanner(IElement el)
        {
            var marker = $"{el.Id} {el.ClassName}".ToLowerInvariant();
            return marker.Contains("cookie") || marker.Contains("consent") || marker.Contains("gdpr");
        }

        private static void NewBlock(StringBuilder sb)
        {
            if (sb.Length > 0 && sb[^1] != '\n') sb.Append("\n\n");
        }

        private static string Collapse(string text, bool keepEdges = false)
        {
            var collapsed = Regex.Replace(text, @"\s+", " ");
            return keepEdges ? collapsed : collapsed.Trim();
        }
    }
}
