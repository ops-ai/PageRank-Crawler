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
            var graphClient = new GraphClient(new Uri("http://localhost:7474/db/data/"), "neo4j", "neo4j");
            graphClient.ConnectAsync().ConfigureAwait(false).GetAwaiter().GetResult();

            var serviceProvider = new ServiceCollection()
                .AddLogging(config => config.AddConsole())
                .AddSingleton<IGraphClient>(graphClient)
                .BuildServiceProvider();

            MainAsync(serviceProvider).GetAwaiter().GetResult();
        }

        private static readonly Uri BaseUri = new Uri("https://wikipedia.org");
        private static readonly HashSet<string> linkBag = new();
        private static readonly ConcurrentQueue<string> links = new(new List<string> { BaseUri.ToString() });

        private static async Task MainAsync(ServiceProvider serviceProvider)
        {
            var logger = serviceProvider.GetRequiredService<ILoggerFactory>().CreateLogger<Program>();
            var graphClient = serviceProvider.GetRequiredService<IGraphClient>();

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

            while (links.TryDequeue(out var nextUrl))
            {
                try
                {
                    logger.LogInformation("Getting url {nextUrl}", nextUrl);
                    var pageNav = await page.GoToAsync(nextUrl);
                    //pageNav.Status

                    var metrics = await page.MetricsAsync();
                    var anchors = @"Array.from(document.querySelectorAll('a')).map(a => a.href.toLowerCase());";
                    var urls = await page.EvaluateExpressionAsync<string[]>(anchors);

                    foreach (string url in urls)
                    {
                        //TODO: Create relationships (and optionally target nodes) from {nextUrl} node to to all nodes in {urls}
                        var targetPage = new PageInfo { Url = url.TrimEnd(' ', '/') };
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
                                url = url.TrimEnd(' ', '/')
                            })
                            .ExecuteWithoutResultsAsync();

                        if (!string.IsNullOrWhiteSpace(url) && Uri.IsWellFormedUriString(url, UriKind.RelativeOrAbsolute) && BaseUri.IsBaseOf(new Uri(url)))
                            if (linkBag.Add(url.TrimEnd(' ', '/')))
                                links.Enqueue(url.TrimEnd(' ', '/'));
                    }
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Error navigating to url");
                }
            }
            logger.LogInformation("Done");
            Console.ReadKey();
        }
    }
}

