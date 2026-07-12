using Downpatch.Web.Services;
using Markdig;
using Microsoft.Extensions.Caching.Memory;
using System.Text;

namespace Downpatch.Web.Services
{
    public sealed class MarkdownPageService
    {
        private readonly ContentIndex _index;
        private readonly IMemoryCache _cache;
        private readonly MarkdownPipeline _pipeline;

        public MarkdownPageService(ContentIndex index, IMemoryCache cache, MarkdownPipeline pipeline)
        {
            _index = index;
            _cache = cache;
            _pipeline = pipeline;
        }

        public void WarmAll()
        {
            foreach (var entry in _index.AllEntries)
            {
                _ = TryGetRendered(entry.Slug.StartsWith("guide/", StringComparison.OrdinalIgnoreCase)
                    ? entry.Slug["guide/".Length..]
                    : entry.Slug, out _);
            }
        }

        public bool TryGetRendered(string? slug, out RenderedPage page)
        {
            page = default;

            if (!_index.TryResolve(slug, out var entry))
                return false;

            var cacheKey = $"page::{entry.Slug}";

            if (_cache.TryGetValue(cacheKey, out RenderedPage cached) && cached.LastModifiedUtc == entry.LastModifiedUtc)
            {
                page = cached;
                return true;
            }

            var markdown = File.ReadAllText(entry.FilePath, Encoding.UTF8);
            var (fm, body) = ParseFrontMatter(markdown);

            var title = fm.TryGetValue("title", out var t) && !string.IsNullOrWhiteSpace(t) ? t : entry.Slug;
            var placeholders = new Dictionary<string, string>();

            body = RewriteStrategyBlocks(body, placeholders);

            var htmlBody = Markdown.ToHtml(body, _pipeline);

            foreach (var pair in placeholders)
            {
                htmlBody = htmlBody.Replace(pair.Key, pair.Value);
            }

            htmlBody = RewriteRelativeLinks(htmlBody, entry.Slug);
            htmlBody = RewriteYoutubeEmbeds(htmlBody);
            htmlBody = RewriteCallouts(htmlBody);

            page = new RenderedPage(
                Slug: entry.Slug,
                Title: title,
                HtmlBody: htmlBody,
                FrontMatter: fm,
                LastModifiedUtc: entry.LastModifiedUtc
            );

            var approxBytes = page.HtmlBody.Length * 2;

            _cache.Set(cacheKey, page, new MemoryCacheEntryOptions()
                .SetSize(approxBytes)
                .SetPriority(CacheItemPriority.Normal));

            return true;
        }

        private static (Dictionary<string, string> frontMatter, string body) ParseFrontMatter(string text)
        {
            var fm = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            if (!(text.StartsWith("---\n") || text.StartsWith("---\r\n")))
                return (fm, text);

            var end = text.IndexOf("\n---", 4, StringComparison.Ordinal);
            if (end < 0)
                return (fm, text);

            var closingLineEnd = text.IndexOf('\n', end + 4);
            if (closingLineEnd < 0) closingLineEnd = end + 4;

            var header = text.Substring(4, end - 4);
            var body = text.Substring(closingLineEnd + 1);

            foreach (var raw in header.Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal)) continue;

                var colon = line.IndexOf(':');
                if (colon <= 0) continue;

                var key = line[..colon].Trim();
                var val = line[(colon + 1)..].Trim();

                if (val.Length >= 2 && ((val[0] == '"' && val[^1] == '"') || (val[0] == '\'' && val[^1] == '\'')))
                    val = val[1..^1];

                fm[key] = val;
            }

            return (fm, body);
        }

        private static string RewriteRelativeLinks(string html, string slug)
        {
            // slug example: guide/halo/index
            // base path should be /guide/halo/
            var lastSlash = slug.LastIndexOf('/');
            if (lastSlash < 0)
                return html;

            var basePath = "/" + slug[..lastSlash] + "/";

            return System.Text.RegularExpressions.Regex.Replace(
                html,
                "<a\\s+([^>]*?)href=\"(.*?)\"",
                match =>
                {
                    var before = match.Groups[1].Value;
                    var href = match.Groups[2].Value;

                    // skip absolute + root links
                    if (href.StartsWith("/") ||
                        href.StartsWith("http", StringComparison.OrdinalIgnoreCase) ||
                        href.StartsWith("#") ||
                        href.StartsWith("mailto:"))
                        return match.Value;

                    // remove .md if present
                    if (href.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
                        href = href[..^3];

                    var newHref = basePath + href.TrimStart('/');

                    return $"<a {before}href=\"{newHref}\"";
                },
                System.Text.RegularExpressions.RegexOptions.IgnoreCase
            );
        }

        private static string RewriteYoutubeEmbeds(string html)
        {
            return System.Text.RegularExpressions.Regex.Replace(
                html,
                @"<youtube\s+([^>]*)></youtube>",
                match =>
                {
                    var attrs = match.Groups[1].Value;

                    string? video = null;
                    string title = "YouTube video";

                    // Optional title
                    var titleMatch = System.Text.RegularExpressions.Regex.Match(
                        attrs,
                        @"title=""([^""]+)"""
                    );

                    if (titleMatch.Success)
                    {
                        title = System.Net.WebUtility.HtmlEncode(titleMatch.Groups[1].Value);
                    }

                    // id="..."
                    var idMatch = System.Text.RegularExpressions.Regex.Match(
                        attrs,
                        @"id=""([^""]+)"""
                    );

                    if (idMatch.Success)
                    {
                        video = $"https://www.youtube.com/embed/{idMatch.Groups[1].Value}";
                    }
                    else
                    {
                        // url="..."
                        var urlMatch = System.Text.RegularExpressions.Regex.Match(
                            attrs,
                            @"url=""([^""]+)"""
                        );

                        if (urlMatch.Success)
                        {
                            video = ExtractYoutubeEmbedUrl(urlMatch.Groups[1].Value);
                        }
                    }

                    if (string.IsNullOrWhiteSpace(video))
                        return match.Value;

                    return $"""
        <div class="video-embed">
            <iframe
                src="{video}"
                title="{title}"
                loading="lazy"
                allowfullscreen>
            </iframe>
        </div>
        """;
                },
                System.Text.RegularExpressions.RegexOptions.IgnoreCase
            );
        }
        private string RewriteStrategyBlocks(
            string markdown,
            Dictionary<string, string> placeholders)
        {
            return System.Text.RegularExpressions.Regex.Replace(
                markdown,
                @":::strategy\s*(.*?)\n\n(.*?):::",
                match =>
                {
                    var header = match.Groups[1].Value;
                    var body = match.Groups[2].Value.Trim();

                    var data = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                    foreach (var line in header.Split('\n'))
                    {
                        var trimmed = line.Trim();

                        if (string.IsNullOrWhiteSpace(trimmed))
                            continue;

                        var colon = trimmed.IndexOf(':');

                        if (colon <= 0)
                            continue;

                        data[trimmed[..colon].Trim()] =
                            trimmed[(colon + 1)..].Trim();
                    }

                    string Get(string key) =>
                        data.TryGetValue(key, out var value)
                            ? System.Net.WebUtility.HtmlEncode(value)
                            : "";

                    var renderedBody = Markdown.ToHtml(body, _pipeline);

                    var key = $"%%STRATEGY_{placeholders.Count}%%";

                    placeholders[key] = $"""
                    <div class="strategy-card">

                        <div class="strategy-card-header">
                            <h3>{Get("title")}</h3>
                        </div>

                        <div class="strategy-card-meta">

                            <div class="strategy-card-label">Difficulty</div>
                            <div class="strategy-card-value">
                                <span class="strategy-card-difficulty {DifficultyClass(Get("difficulty"))}">
                                    {Get("difficulty")}
                                </span>
                            </div>

                            <div class="strategy-card-label">Time Save</div>
                            <div class="strategy-card-value strategy-time-save">
                                <span class="strategy-time-value {TimeSaveClass(Get("time-save"))}">
                                    {Get("time-save")}
                                </span>

                                {(string.IsNullOrWhiteSpace(Get("compared-to"))
                                    ? ""
                                    : $"<div class=\"strategy-time-subtext\">over {Get("compared-to")}</div>")}
                            </div>

                            <div class="strategy-card-label">Platform</div>
                            <div class="strategy-card-value">
                                {Get("platform")}
                            </div>

                            <div class="strategy-card-label">Input</div>
                            <div class="strategy-card-value">
                                {Get("input")}
                            </div>

                            <div class="strategy-card-label">Recommended</div>
                            <div class="strategy-card-value">
                                {Get("recommended")}
                            </div>

                            <div class="strategy-card-label">Consistency</div>
                            <div class="strategy-card-value strategy-consistency {ConsistencyClass(Get("consistency"))}">
                                {Get("consistency")}
                            </div>

                        </div>

                        <div class="strategy-card-content">
                            {renderedBody}
                        </div>

                    </div>
                    """;

                    return key;
                },
                System.Text.RegularExpressions.RegexOptions.Singleline |
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        }

        private static string DifficultyClass(string difficulty)
        {
            return difficulty.Trim().ToLowerInvariant() switch
            {
                "beginner" => "beginner",
                "intermediate" => "intermediate",
                "advanced" => "advanced",
                "il only" => "il-only",
                "experimental" => "experimental",
                _ => ""
            };
        }

        private static string TimeSaveClass(string value)
        {
            var match = System.Text.RegularExpressions.Regex.Match(value, @"\d+");

            if (!match.Success)
                return "";

            var seconds = int.Parse(match.Value);

            if (seconds >= 45)
                return "legendary";

            if (seconds >= 30)
                return "major";

            if (seconds >= 15)
                return "great";

            if (seconds >= 5)
                return "good";

            return "minor";
        }

        private static string ConsistencyClass(string consistency)
        {
            var digits = new string(consistency.Where(char.IsDigit).ToArray());

            if (!int.TryParse(digits, out var value))
                return "";

            if (value >= 80)
                return "high";

            if (value >= 50)
                return "medium";

            return "low";
        }
        
        private static string RewriteCallouts(string html)
        {
            return System.Text.RegularExpressions.Regex.Replace(
                html,
                @"<blockquote>\s*<p>\[!(NOTE|TIP|IMPORTANT|WARNING|CAUTION)\]\s*(.*?)</p>(.*?)</blockquote>",
                match =>
                {
                    var type = match.Groups[1].Value.ToLowerInvariant();
                    var first = match.Groups[2].Value.Trim();
                    var rest = match.Groups[3].Value;

                    var title = type switch
                    {
                        "note" => "Note",
                        "tip" => "Tip",
                        "important" => "Important",
                        "warning" => "Warning",
                        "caution" => "Caution",
                        _ => "Note"
                    };

                    return $"""
        <div class="callout callout-{type}">
            <div class="callout-title">{title}</div>
            <div class="callout-body">
                <p>{first}</p>
                {rest}
            </div>
        </div>
        """;
                },
                System.Text.RegularExpressions.RegexOptions.Singleline |
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        }

        public readonly record struct RenderedPage(
            string Slug,
            string Title,
            string HtmlBody,
            IReadOnlyDictionary<string, string> FrontMatter,
            DateTime LastModifiedUtc
        );

        private static string? ExtractYoutubeEmbedUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
                return null;

            var uri = new Uri(url);

            string? videoId = null;

            if (uri.Host.Contains("youtu.be"))
            {
                videoId = uri.AbsolutePath.Trim('/');
            }
            else
            {
                var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
                videoId = query["v"];
            }

            if (string.IsNullOrWhiteSpace(videoId))
                return null;

            var queryParams = System.Web.HttpUtility.ParseQueryString(uri.Query);

            var t =
                queryParams["t"] ??
                queryParams["start"];

            if (string.IsNullOrWhiteSpace(t))
                return $"https://www.youtube.com/embed/{videoId}";

            t = t.TrimEnd('s');

            return $"https://www.youtube.com/embed/{videoId}?start={t}";
        }
    }
}
