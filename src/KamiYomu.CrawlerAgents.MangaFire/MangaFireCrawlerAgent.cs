using HtmlAgilityPack;
using KamiYomu.CrawlerAgents.Core;
using KamiYomu.CrawlerAgents.Core.Catalog;
using KamiYomu.CrawlerAgents.Core.Catalog.Builders;
using KamiYomu.CrawlerAgents.Core.Catalog.Definitions;
using KamiYomu.CrawlerAgents.Core.Inputs;
using Microsoft.Extensions.Logging;
using PuppeteerSharp;
using System.ComponentModel;
using System.Text.RegularExpressions;
using System.Web;
using Page = KamiYomu.CrawlerAgents.Core.Catalog.Page;

namespace KamiYomu.CrawlerAgents.MangaFire;

[DisplayName("KamiYomu Crawler Agent – mangafire.to")]
[CrawlerSelect("Language", "Chapter Translation language, translated fields such as Titles and Descriptions", true, 1, [
  "en",
  "es",
  "es-la",
  "fr",
  "jp",
  "pt",
  "pt-br"
])]
[CrawlerText("PageLoadingTimeout", "Enter the delay, in milliseconds, to wait before fetching the next page while downloading.", true, "5000")]
public class MangaFireCrawlerAgent : AbstractCrawlerAgent, ICrawlerAgent, IAsyncDisposable
{
    private bool _disposed = false;
    private readonly Uri _baseUri;
    private Lazy<Task<IBrowser>> _browser;
    public Task<IBrowser> GetBrowserAsync() => _browser.Value;

    private async Task<IBrowser> CreateBrowserAsync()
    {
        var launchOptions = new LaunchOptions
        {
            Headless = true,
            Timeout = TimeoutMilliseconds,
            Args = [
                "--disable-blink-features=AutomationControlled",
                "--no-sandbox",
                "--disable-dev-shm-usage"
            ]
        };

        return await Puppeteer.LaunchAsync(launchOptions);
    }
    private readonly string _language;
    private readonly int _pageLoadingTimeoutValue;

    public MangaFireCrawlerAgent(IDictionary<string, object> options) : base(options)
    {
        _baseUri = new Uri("https://mangafire.to");
        _browser = new Lazy<Task<IBrowser>>(CreateBrowserAsync, true);
        if (Options.TryGetValue("Language", out var language) && language is string languageValue)
        {
            _language = languageValue;
        }
        else
        {
            _language = "en";
        }

        if (Options.TryGetValue("PageLoadingTimeout", out var pageLoadingTimeout) 
            && pageLoadingTimeout is string pageLoadingTimeoutValue 
            && int.TryParse(pageLoadingTimeoutValue, out var pageLoadingTimeoutValueInt))
        {
            _pageLoadingTimeoutValue = pageLoadingTimeoutValueInt;
        }
        else
        {
            _pageLoadingTimeoutValue = 5_000;
        }
    }



    public Task<Uri> GetFaviconAsync(CancellationToken cancellationToken)
    {
        return Task.FromResult(new Uri("https://s.mfcdn.cc/assets/sites/mangafire/favicon.png"));
    }

    public async Task<PagedResult<Manga>> SearchAsync(string titleName, PaginationOptions paginationOptions, CancellationToken cancellationToken)
    {
        var token = await GetTokenAsync();

        var browser = await GetBrowserAsync();
        using var page = await browser.NewPageAsync();
        await SetTimeZone(page);
        await page.SetUserAgentAsync(HttpClientDefaultUserAgent);
        var queryParams = new Dictionary<string, string>
        {
            ["keyword"] = titleName,
            ["vrf"] = token,
            ["language[]"] = _language,
        };
        var encodedQuery = new FormUrlEncodedContent(queryParams).ReadAsStringAsync(cancellationToken).Result;
        var builder = new UriBuilder(new Uri(new Uri(_baseUri.ToString()), "filter"))
        {
            Query = encodedQuery
        };


        var finalUrl = builder.Uri.ToString();

        var response = await page.GoToAsync(finalUrl, new NavigationOptions
        {
            WaitUntil = [WaitUntilNavigation.DOMContentLoaded, WaitUntilNavigation.Load],
            Timeout = TimeoutMilliseconds
        });

        var content = await page.GetContentAsync();
        var document = new HtmlDocument();
        document.LoadHtml(content);

        List<Manga> mangas = [];
        HtmlNodeCollection nodes = document.DocumentNode.SelectNodes("//div[contains(@class, 'unit') and contains(@class, 'item-')]");
        if (nodes != null)
        {
            foreach (var divNode in nodes)
            {
                Manga manga = ConvertToMangaFromList(divNode);
                mangas.Add(manga);
            }
        }

        return PagedResultBuilder<Manga>.Create()
            .WithData(mangas)
            .WithPaginationOptions(new PaginationOptions(mangas.Count(), mangas.Count(), mangas.Count()))
            .Build();
    }

    private Manga ConvertToMangaFromList(HtmlNode divNode)
    {
        var baseUri = new Uri(_baseUri.ToString());

        // Title node
        var titleNode = divNode.SelectSingleNode(".//div[@class='info']/a");
        var title = titleNode?.InnerText.Trim();

        // Website URL
        var websiteUrlRaw = titleNode?.GetAttributeValue("href", string.Empty);
        var websiteUrl = NormalizeUrl(websiteUrlRaw);

        // ID from last segment of URL
        string id = null;
        if (!string.IsNullOrEmpty(websiteUrl))
        {
            var uri = new Uri(websiteUrl);
            id = uri.Segments.Last().Trim('/');
        }

        // Cover image
        var coverNode = divNode.SelectSingleNode(".//img");
        var coverUrlRaw = coverNode?.GetAttributeValue("src", string.Empty);
        var coverUrlStr = NormalizeUrl(coverUrlRaw);
        Uri coverUrl = string.IsNullOrEmpty(coverUrlStr) ? null : new Uri(coverUrlStr);
        var coverFileName = coverUrl != null ? Path.GetFileName(coverUrl.LocalPath) : null;

        // Type (Manga, One_shot, etc.)
        var typeNode = divNode.SelectSingleNode(".//span[@class='type']");
        var type = typeNode?.InnerText.Trim();

        // Chapters
        var chapterNodes = divNode.SelectNodes(".//ul[@class='content' and @data-name='chap']/li/a");
        decimal latestChapter = 0;
        var links = new Dictionary<string, string>();
        if (chapterNodes != null)
        {
            foreach (var a in chapterNodes)
            {
                var text = a.InnerText.Trim();
                var href = NormalizeUrl(a.GetAttributeValue("href", string.Empty));
                links[text] = href;

                var match = Regex.Match(text, @"Chap\s+(\d+)");
                if (match.Success && decimal.TryParse(match.Groups[1].Value, out var chapNum))
                {
                    if (chapNum > latestChapter) latestChapter = chapNum;
                }
            }
        }

        // Volumes
        var volumeNodes = divNode.SelectNodes(".//ul[@class='content' and @data-name='vol']/li/a");
        decimal lastVolume = 0;
        if (volumeNodes != null)
        {
            foreach (var a in volumeNodes)
            {
                var text = a.InnerText.Trim();
                var href = NormalizeUrl(a.GetAttributeValue("href", string.Empty));
                links[text] = href;

                var match = Regex.Match(text, @"Vol\s+(\d+)");
                if (match.Success && decimal.TryParse(match.Groups[1].Value, out var volNum))
                {
                    if (volNum > lastVolume) lastVolume = volNum;
                }
            }
        }

        // Build Manga object
        var manga = MangaBuilder.Create()
            .WithId(id)
            .WithTitle(title)
            .WithDescription("No Description Available")
            .WithCoverUrl(coverUrl)
            .WithCoverFileName(coverFileName)
            .WithWebsiteUrl(websiteUrl)
            .WithTags(type)
            .WithLinks(links)
            .WithLatestChapterAvailable(latestChapter)
            .WithLastVolumeAvailable(lastVolume)
            .Build();

        return manga;
    }

    public async Task<Manga> GetByIdAsync(string id, CancellationToken cancellationToken)
    {
        var browser = await GetBrowserAsync();
        using var page = await browser.NewPageAsync();
        await SetTimeZone(page);
        await page.SetUserAgentAsync(HttpClientDefaultUserAgent);

        var finalUrl = new Uri(_baseUri, $"manga/{id}").ToString();
        var response = await page.GoToAsync(finalUrl, new NavigationOptions
        {
            WaitUntil = [WaitUntilNavigation.DOMContentLoaded, WaitUntilNavigation.Load],
            Timeout = TimeoutMilliseconds
        });

        var content = await response.TextAsync();
        var document = new HtmlDocument();
        document.LoadHtml(content);
        var rootNode = document.DocumentNode.SelectSingleNode("//*[@id='manga-page']");
        Manga manga = ConvertToMangaFromSingleBook(rootNode, id);

        return manga;
    }

    public async Task<PagedResult<Chapter>> GetChaptersAsync(Manga manga, PaginationOptions paginationOptions, CancellationToken cancellationToken)
    {
        var browser = await GetBrowserAsync();
        using var page = await browser.NewPageAsync();
        await SetTimeZone(page);
        await page.SetUserAgentAsync(HttpClientDefaultUserAgent);

        var finalUrl = new Uri(_baseUri, $"manga/{manga.Id}").ToString();
        var response = await page.GoToAsync(finalUrl, new NavigationOptions
        {
            WaitUntil = [WaitUntilNavigation.DOMContentLoaded, WaitUntilNavigation.Load],
            Timeout = TimeoutMilliseconds
        });

        var content = await response.TextAsync();
        var document = new HtmlDocument();
        document.LoadHtml(content);
        var rootNode = document.DocumentNode.SelectSingleNode("//*[@id='manga-page']");
        IEnumerable<Chapter> chapters = ConvertChaptersFromSingleBook(manga, rootNode);

        return PagedResultBuilder<Chapter>.Create()
                                          .WithPaginationOptions(new PaginationOptions(chapters.Count(), chapters.Count(), chapters.Count()))
                                          .WithData(chapters)
                                          .Build();
    }

    public async Task<IEnumerable<Core.Catalog.Page>> GetChapterPagesAsync(Chapter chapter, CancellationToken cancellationToken)
    {
        var browser = await GetBrowserAsync();
        using var page = await browser.NewPageAsync();

        await SetTimeZone(page);
        await page.SetUserAgentAsync(HttpClientDefaultUserAgent);

        await page.GoToAsync(chapter.Uri.ToString(), new NavigationOptions
        {
            WaitUntil = [WaitUntilNavigation.DOMContentLoaded, WaitUntilNavigation.Load],
            Timeout = TimeoutMilliseconds
        });

        // Get total number of pages from progress-bar
        int totalPages = await page.EvaluateFunctionAsync<int>(@"
            () => {
                const totalEl = document.querySelector('#progress-bar .total-page');
                if (totalEl) {
                    const val = parseInt(totalEl.textContent.trim(), 10);
                    return isNaN(val) ? 0 : val;
                }
                const lis = document.querySelectorAll('#progress-bar ul li[data-page]');
                return lis.length;
            }
        ");

        Logger?.LogInformation("Number of Pages Expected: {totalPages}", totalPages);

        // Click each progress-bar item one by one to trigger lazy loading
        for (int i = 1; i <= totalPages; i++)
        {
            await page.EvaluateExpressionAsync($@"
            (() => {{
                const li = document.querySelector(`#progress-bar ul li[data-page='{i}']`);
                if (li) li.click();
            }})();
        ");
            await Task.Delay(_pageLoadingTimeoutValue, cancellationToken); // wait for image to load
        }

        // Wait until at least one image has src
        await page.WaitForSelectorAsync("#page-wrapper .img.swiper-slide.loaded img[src], .page img[src]");

        // Get fully loaded HTML
        var content = await page.GetContentAsync();
        var document = new HtmlDocument();
        document.LoadHtml(content);

        // Collect all <img> nodes that now have src
        var imgNodes = document.DocumentNode.SelectNodes(
            "//div[contains(@class,'page')]//img | //div[@id='page-wrapper']//div[contains(@class,'img') and contains(@class,'swiper-slide') and contains(@class,'loaded')]/img"
        );

        Logger?.LogInformation("Number of Page found {totalPages}", imgNodes.Count);

        return ConvertToChapterPages(chapter, imgNodes);
    }

    private IEnumerable<Core.Catalog.Page> ConvertToChapterPages(Chapter chapter, HtmlNodeCollection imgNodes)
    {
        if (imgNodes == null || imgNodes.Count == 0)
            return Enumerable.Empty<Page>();

        var pages = new List<Page>();

        foreach (var imgNode in imgNodes)
        {
            var numberAttr = imgNode.GetAttributeValue("data-number", string.Empty);
            if (!decimal.TryParse(numberAttr, out decimal pageNumber))
                continue;

            var imageUrl = imgNode.GetAttributeValue("src", null);
            if (string.IsNullOrEmpty(imageUrl))
                continue;

            var normalizedUrl = NormalizeUrl(imageUrl);

            var page = PageBuilder.Create()
                                  .WithChapterId(chapter.Id)
                                  .WithId($"page-{pageNumber}")
                                  .WithPageNumber(pageNumber)
                                  .WithImageUrl(new Uri(normalizedUrl))
                                  .WithParentChapter(chapter)
                                  .Build();

            pages.Add(page);
        }

        return pages;
    }

    private Manga ConvertToMangaFromSingleBook(HtmlNode rootNode, string id)
    {
        var baseUri = new Uri(_baseUri.ToString());

        // Title
        var titleNode = rootNode.SelectSingleNode(".//h1[@itemprop='name']");
        var title = titleNode?.InnerText.Trim();

        // Alternative titles (split by semicolon)
        var altTitlesNode = rootNode.SelectSingleNode(".//h6");
        var altTitles = new Dictionary<string, string>();
        if (altTitlesNode != null)
        {
            var parts = altTitlesNode.InnerText.Split(';');
            int i = 1;
            foreach (var part in parts)
            {
                var alt = part.Trim();
                if (!string.IsNullOrEmpty(alt))
                    altTitles[$"Alt{i++}"] = alt;
            }
        }

        // Cover image
        var coverNode = rootNode.SelectSingleNode(".//aside[@class='content']//div[@class='poster']//img");
        var coverUrlRaw = coverNode?.GetAttributeValue("src", string.Empty);
        var coverUrlStr = NormalizeUrl(coverUrlRaw);
        Uri coverUrl = string.IsNullOrEmpty(coverUrlStr) ? null : new Uri(coverUrlStr);
        var coverFileName = coverUrl != null ? Path.GetFileName(coverUrl.LocalPath) : null;

        // Background image (optional)
        var bgNode = rootNode.SelectSingleNode(".//div[@class='detail-bg']/img");
        var bgUrlRaw = bgNode?.GetAttributeValue("src", string.Empty);
        var bgUrlStr = NormalizeUrl(bgUrlRaw);

        // Description
        var descNode = rootNode.SelectSingleNode(".//div[@class='description']");
        var description = HttpUtility.HtmlDecode(descNode?.InnerText.Trim());

        // Website URL (canonical)
        var shareNode = rootNode.SelectSingleNode(".//div[contains(@class,'sharethis-inline-share-buttons')]");
        var websiteUrl = shareNode?.GetAttributeValue("data-url", string.Empty);
        websiteUrl = NormalizeUrl(websiteUrl);

        // Release status
        var statusNode = rootNode.SelectSingleNode(".//aside[@class='content']//p");
        ReleaseStatus releaseStatus = ReleaseStatus.Continuing;
        if (statusNode != null)
        {
            var statusText = statusNode.InnerText.Trim().ToLowerInvariant();
            if (statusText.Contains("releasing")) releaseStatus = ReleaseStatus.Continuing;
            else if (statusText.Contains("completed")) releaseStatus = ReleaseStatus.Completed;
        }

        // Authors
        var authorNodes = rootNode.SelectNodes(".//div[@class='meta']//span[a[@itemprop='author']]/a");
        var authors = authorNodes?.Select(a => a.InnerText.Trim()).ToArray() ?? Array.Empty<string>();

        // Genres
        var genreNodes = rootNode.SelectNodes(".//div[@class='meta']//a[contains(@href,'/genre/')]");
        var genres = genreNodes?.Select(a => a.InnerText.Trim()).ToArray() ?? Array.Empty<string>();

        // Year (from Published)
        int? year = null;
        var publishedNode = rootNode.SelectSingleNode(".//div[@class='meta']//div[span[text()='Published:']]/span[2]");
        if (publishedNode != null)
        {
            var text = publishedNode.InnerText;
            var match = Regex.Match(text, @"\b(\d{4})\b");
            if (match.Success && int.TryParse(match.Groups[1].Value, out var y))
                year = y;
        }

        // Rating
        var ratingNode = rootNode.SelectSingleNode(".//span[@class='live-score']");
        decimal rating = 0;
        if (ratingNode != null && decimal.TryParse(ratingNode.InnerText.Trim(), out var r))
            rating = r;

        // Build Manga object
        var manga = MangaBuilder.Create()
            .WithId(id)
            .WithTitle(title)
            .WithAlternativeTitles(altTitles)
            .WithDescription(description)
            .WithAuthors(authors)
            .WithTags(genres)
            .WithCoverUrl(coverUrl)
            .WithCoverFileName(coverFileName)
            .WithWebsiteUrl(websiteUrl)
            .WithYear(year)
            .WithIsFamilySafe(!genres.Any(g => IsGenreNotFamilySafe(g)))
            .WithReleaseStatus(releaseStatus)
            .Build();

        return manga;
    }

    private List<Chapter> ConvertChaptersFromSingleBook(Manga manga, HtmlNode rootNode)
    {
        var baseUri = new Uri(_baseUri.ToString());
        var activeLangNode = rootNode.SelectSingleNode(".//div[@class='tab-content' and @data-name='chapter']//div[@class='dropdown-menu']/a[contains(@class,'active')]");
        string activeLangCode = activeLangNode?.GetAttributeValue("data-code", "");
        var chapters = new List<Chapter>();

        // Select all chapter list items
        var chapterNodes = rootNode.SelectNodes(".//div[@class='tab-content' and @data-name='chapter']//ul[@class='scroll-sm']/li[@class='item']");
        if (chapterNodes == null) return chapters;

        foreach (var li in chapterNodes)
        {
            // Chapter number from data-number
            var numberAttr = li.GetAttributeValue("data-number", string.Empty);
            decimal number = 0;
            if (!string.IsNullOrEmpty(numberAttr))
                decimal.TryParse(numberAttr, out number);

            // Anchor node
            var aNode = li.SelectSingleNode("./a");
            if (aNode == null) continue;

            // Uri
            var href = aNode.GetAttributeValue("href", string.Empty);
            var uri = NormalizeUrl(href.Replace($"/{activeLangCode.ToLower()}/", $"/{_language}/"));

            // Title
            var titleSpan = aNode.SelectSingleNode("./span[1]");
            var title = HttpUtility.HtmlDecode(titleSpan?.InnerText.Trim());

            // Chapter Id: last segment of URL
            string chapterId = null;
            if (!string.IsNullOrEmpty(uri))
            {
                var uriObj = new Uri(uri);
                chapterId = uriObj.Segments.Last().Trim('/');
            }

            // Volume: parse from title attribute if present
            var titleAttr = aNode.GetAttributeValue("title", string.Empty);
            decimal volume = 0;
            var volMatch = Regex.Match(titleAttr, @"Vol\s+(\d+)");
            if (volMatch.Success && decimal.TryParse(volMatch.Groups[1].Value, out var volNum))
                volume = volNum;

            // Build Chapter object
            var chapter = ChapterBuilder.Create()
                .WithId(chapterId)
                .WithTitle(title)
                .WithParentManga(manga)
                .WithVolume(volume)
                .WithNumber(number)
                .WithUri(new Uri(uri))
                .Build();

            chapters.Add(chapter);
        }

        return chapters;
    }

    private static bool IsGenreNotFamilySafe(string p)
    {
        if (string.IsNullOrWhiteSpace(p)) return false;
        return p.Contains("adult", StringComparison.OrdinalIgnoreCase)
            || p.Contains("harem", StringComparison.OrdinalIgnoreCase)
            || p.Contains("ecchi", StringComparison.OrdinalIgnoreCase)
            || p.Contains("shota", StringComparison.OrdinalIgnoreCase)
            || p.Contains("sexual", StringComparison.OrdinalIgnoreCase);
    }

    private string NormalizeUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return string.Empty;

        if (!url.StartsWith("/") && Uri.TryCreate(url, UriKind.Absolute, out var absolute))
            return absolute.ToString();

        var resolved = new Uri(_baseUri, url);
        return resolved.ToString();
    }

    private async Task<string> GetTokenAsync()
    {
        var browser = await GetBrowserAsync();

        using var page = await browser.NewPageAsync();
        await page.SetUserAgentAsync(HttpClientDefaultUserAgent);
        await SetTimeZone(page);


        // Inject overrides BEFORE any site scripts execute
        await page.EvaluateExpressionOnNewDocumentAsync(@"
        // Neutralize devtools detection
        const originalLog = console.log;
        console.log = function(...args) {
            if (args.length === 1 && args[0] === '[object HTMLDivElement]') {
                return; // skip detection trick
            }
            return originalLog.apply(console, args);
        };

        // Override reload to do nothing
        window.location.reload = () => console.log('Reload prevented');
    ");

        // Navigate to /home
        var targetUri = new Uri(new Uri(_baseUri.ToString()), "home");
        await page.GoToAsync(targetUri.ToString(), new NavigationOptions
        {
            WaitUntil = new[] { WaitUntilNavigation.DOMContentLoaded, WaitUntilNavigation.Load },
            Timeout = TimeoutMilliseconds
        });

        // Intercept AFTER initial load to block subsequent reloads
        await page.SetRequestInterceptionAsync(true);
        page.Request += async (sender, e) =>
        {
            if (e.Request.ResourceType == ResourceType.Document &&
                !e.Request.Url.Contains("/home"))
            {
                await e.Request.AbortAsync(); // block reloads
            }
            else
            {
                await e.Request.ContinueAsync();
            }
        };

        // Wait for the input safely
        await page.WaitForSelectorAsync("input[name='keyword']");

        // Focus the input (often triggers VRF script)
        await page.FocusAsync("input[name='keyword']");

        // Optionally type something
        await page.Keyboard.TypeAsync("One Piece");

        // Capture the full HTML content too
        var vrfElement = await page.QuerySelectorAsync("input[name='vrf']");
        var vrfValue = await page.EvaluateFunctionAsync<string>("el => el.value", vrfElement);


        return vrfValue;
    }

    private static async Task SetTimeZone(IPage page)
    {
        var localTimeZone = TimeZoneInfo.Local.Id;

        await page.EmulateTimezoneAsync(localTimeZone);

        await page.EvaluateExpressionOnNewDocumentAsync(@"
            // Freeze time to a specific date
            const fixedDate = new Date('2025-11-21T12:00:00Z');
            Date = class extends Date {
                constructor(...args) {
                    if (args.length === 0) {
                        return fixedDate;
                    }
                    return super(...args);
                }
                static now() {
                    return fixedDate.getTime();
                }
            };
        ");
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (_disposed)
            return;

        if (disposing)
        {
            if (_browser.IsValueCreated)
            {
                var browserTask = _browser.Value;
                if (browserTask.IsCompletedSuccessfully)
                {
                    browserTask.Result.Dispose();
                }
            }
        }

        _disposed = true;
    }
    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        if (_browser.IsValueCreated)
        {
            try
            {
                var browser = await _browser.Value;
                await browser.CloseAsync();
                await browser.DisposeAsync();
            }
            catch (Exception ex)
            {
                Logger?.LogError("{crawler}, Error disposing browser: {Message}", nameof(MangaFireCrawlerAgent), ex.Message);
            }
        }

        _disposed = true;
    }

    ~MangaFireCrawlerAgent()
    {
        Dispose(false);
    }

}
