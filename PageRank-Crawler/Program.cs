using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Neo4jClient;
using PageRank_Crawler;
using PuppeteerSharp;
using System.Collections.Concurrent;
using System.Linq;

namespace PageRankCrawler
{
    public class Program
    {
        public static void Main(string[] args)
        {
            var graphClient = new GraphClient(new Uri("http://localhost:7474/db/data/"), "neo4j", "P@ssw0rd1");
            graphClient.ConnectAsync().ConfigureAwait(false).GetAwaiter().GetResult();

            var serviceProvider = new ServiceCollection()
                .AddLogging(config => config.AddConsole())
                .AddSingleton<IGraphClient>(graphClient)
                .BuildServiceProvider();

            MainAsync(serviceProvider).GetAwaiter().GetResult();
        }

        private static readonly Uri BaseUri = new("https://premierpups.com");
        private static readonly HashSet<string> linkBag = new();
        private static readonly HashSet<string> keywords = new();
        private static readonly ConcurrentQueue<string> links = new(new List<string> { BaseUri.ToString() });

        private static async Task MainAsync(ServiceProvider serviceProvider)
        {
            var loggerFactory = serviceProvider.GetRequiredService<ILoggerFactory>();
            var logger = loggerFactory.CreateLogger<Program>();
            var graphClient = serviceProvider.GetRequiredService<IGraphClient>();

            var uniqueKeywords = await CreateKeywordNodesAsync(loggerFactory, graphClient);
            foreach (var keyword in uniqueKeywords)
                keywords.Add(keyword);

            logger.LogInformation("Starting scrape!");

            var options = new LaunchOptions()
            {
                Headless = true,
                ExecutablePath = @"C:\Program Files\Google\Chrome\Application\chrome.exe",
                Product = Product.Chrome
            };
            var browser = await Puppeteer.LaunchAsync(options);
            var page = await browser.NewPageAsync();

            var newPage = new PageInfo { Url = BaseUri.ToString() };
            await graphClient.Cypher
                .Create("(newPage:Page {newPage})")
                .WithParam("newPage", newPage)
                .ExecuteWithoutResultsAsync();

            //TODO: mutlithreading
            while (links.TryDequeue(out var nextUrl))
            {
                try
                {
                    logger.LogInformation("Getting url {nextUrl}", nextUrl);
                    var pageNav = await page.GoToAsync(nextUrl);
                    //pageNav.Status

                    //TODO: record if we got a non-200 status code

                    var metrics = await page.MetricsAsync();
                    var urls = await page.EvaluateExpressionAsync<string[]>(@"Array.from(document.querySelectorAll('a')).map(a => a.href);");

                    //TODO: handle case where page has a canonical link that points to a page different than itself

                    foreach (string url in urls)
                    {
                        var cleanUrl = url.ToLower().TrimEnd(' ', '/');
                        int index = cleanUrl.IndexOf("#");
                        if (index >= 0)
                            cleanUrl = cleanUrl[..index];

                        //TODO: Create relationships (and optionally target nodes) from {nextUrl} node to to all nodes in {urls}
                        var targetPage = new PageInfo { Url = cleanUrl };
                        await graphClient.Cypher
                            .Match("(existingPage:Page)")
                            .Where((PageInfo existingPage) => existingPage.Url == nextUrl)

                            .Merge("(targetPage:Page { Url: {url} })")
                                .OnCreate()
                                .Set("targetPage = {targetPage}")

                            .Create("(existingPage)-[:LINKS {linkInfo}]->(targetPage)")
                            .WithParams(new
                            {
                                linkInfo = new LinkInfo { },
                                targetPage,
                                url = cleanUrl
                            })
                            .ExecuteWithoutResultsAsync();

                        //TODO: exclude loading images?
                        if (!string.IsNullOrWhiteSpace(cleanUrl) && Uri.IsWellFormedUriString(cleanUrl, UriKind.RelativeOrAbsolute) && BaseUri.IsBaseOf(new Uri(cleanUrl)))
                            if (linkBag.Add(cleanUrl))
                                links.Enqueue(cleanUrl);
                    }

                    //TODO: Create relationships + score to keywords if found on page
                    var anchorKeywords = await page.EvaluateExpressionAsync<string[]>(@"Array.from(document.querySelectorAll('a')).map(a => a.innerText);");
                    var headingKeywords = await page.EvaluateExpressionAsync<string[]>(@"Array.from(document.querySelectorAll('h1,h2,h3,h4,h5')).map(a => a.innerText);");
                    var metaKeywords = await page.EvaluateExpressionAsync<string[]>(@"Array.from(document.querySelectorAll('title,meta[name=Description]')).map(a => a.innerText || a.content);");
                    var imgKeywords = await page.EvaluateExpressionAsync<string[]>(@"Array.from(document.querySelectorAll('img')).map(a => a.alt);");
                    var pageContent = await page.GetContentAsync();
                    foreach (var word in keywords)
                    {
                        var score = 0;
                        score += anchorKeywords.Any(x => x.Contains(word, StringComparison.OrdinalIgnoreCase)) ? 4 : 0;
                        score += headingKeywords.Any(x => x.Contains(word, StringComparison.OrdinalIgnoreCase)) ? 4 : 0;
                        score += metaKeywords.Any(x => x.Contains(word, StringComparison.OrdinalIgnoreCase)) ? 5 : 0;
                        score += imgKeywords.Any(x => x.Contains(word, StringComparison.OrdinalIgnoreCase)) ? 3 : 0;
                        if (score == 0)
                            score = pageContent.Contains(word, StringComparison.OrdinalIgnoreCase) ? 1 : 0;

                        if (score > 0)
                        {
                            await graphClient.Cypher
                                .Match("(existingPage:Page)")
                                .Where((PageInfo existingPage) => existingPage.Url == nextUrl)
                                
                                .Match("(keyword:Keyword)")
                                .Where((KeywordInfo keyword) => keyword.Keyword == word)

                                .Create("(existingPage)-[:KEYUSED {linkInfo}]->(keyword)")
                                .WithParams(new
                                {
                                    linkInfo = new KeywordLinkInfo { Score = score }
                                })
                                .ExecuteWithoutResultsAsync();
                        }
                    }
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Error navigating to url");
                }
            }
            logger.LogInformation("Done scraping");

            //TODO: merge canonical pages?


            //TODO: calculate Page Rank
            //https://neo4j.com/docs/graph-data-science/current/algorithms/page-rank/


            Console.ReadKey();
        }

        private static async Task<IEnumerable<string>> CreateKeywordNodesAsync(ILoggerFactory loggerFactory, IGraphClient graphClient)
        {
            var logger = loggerFactory.CreateLogger<Program>();
            logger.LogInformation("Creating keyword nodes");

            var keywords = await File.ReadAllLinesAsync("keywords.txt");
            var uniqueKeywords = keywords.Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t.ToLower().Trim()).Distinct();
            foreach (var keyword in uniqueKeywords)
            {
                var keywordInfo = new KeywordInfo { Keyword = keyword };

                await graphClient.Cypher
                .Create("(keyword:Keyword {keyword})")
                .WithParam("keyword", keywordInfo)
                .ExecuteWithoutResultsAsync();
            }

            return uniqueKeywords;
        }
    }
}

