using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Neo4jClient;
using Neo4jClient.Cypher;
using OfficeOpenXml;
using OfficeOpenXml.Table;
using PageRank_Crawler;
using PuppeteerSharp;
using System.Collections.Concurrent;
using System.ComponentModel;
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

        private static readonly Uri BaseUri = new("https://norcalpups.com/");
        private static readonly HashSet<string> linkBag = new();
        private static AhoCorasick.Trie trie = new();
        private static readonly ConcurrentQueue<string> links = new(new List<string> { BaseUri.ToString() });

        private static async Task MainAsync(ServiceProvider serviceProvider)
        {
            var loggerFactory = serviceProvider.GetRequiredService<ILoggerFactory>();
            var logger = loggerFactory.CreateLogger<Program>();
            var graphClient = serviceProvider.GetRequiredService<IGraphClient>();

            var existingPages = await graphClient.Cypher.Match("(p:Page)").Return<string>("p.Url").ResultsAsync;
            if (existingPages.Any())
            {
                logger.LogError("Database not clean");
                return;
            }

            var uniqueKeywords = await CreateKeywordNodesAsync(loggerFactory, graphClient);
            foreach (var keyword in uniqueKeywords)
                trie.Add(keyword);
            trie.Build();

            await graphClient.Cypher.Create("INDEX ON :Keyword(Keyword)").ExecuteWithoutResultsAsync();
            await graphClient.Cypher.Create("INDEX ON :Page(Url)").ExecuteWithoutResultsAsync();

            logger.LogInformation("Starting scrape!");

            var newPage = new PageInfo { Url = BaseUri.ToString() };
            await graphClient.Cypher
                .Create("(newPage:Page {newPage})")
                .WithParam("newPage", newPage)
                .ExecuteWithoutResultsAsync();

            var tasks = new List<Task>();
            for (var i = 0; i < Environment.ProcessorCount; i++)
                tasks.Add(ProcessLinksAsync(serviceProvider, i));

            Task.WaitAll(tasks.ToArray());

            
            logger.LogInformation("Done scraping");

            //TODO: merge canonical pages?

            //TODO: merge paging


            //calculate Page Rank
            try { await graphClient.Cypher.Call(@"gds.graph.drop('pagerank')").ExecuteWithoutResultsAsync(); } catch { }
            await graphClient.Cypher.Call(@"gds.graph.create('pagerank', 'Page', 'LINKS', { })").ExecuteWithoutResultsAsync();
            logger.LogInformation("Created named graph for page rank");

            await GenerateSpreadsheet(loggerFactory, graphClient, BaseUri.Host);
        }

        private static async Task ProcessLinksAsync(ServiceProvider serviceProvider, int threadId)
        {
            var loggerFactory = serviceProvider.GetRequiredService<ILoggerFactory>();
            var logger = loggerFactory.CreateLogger<Program>();

            try
            {
                var graphClient = serviceProvider.GetRequiredService<IGraphClient>();

                logger.LogInformation("{thread} starting", threadId);

                var options = new LaunchOptions()
                {
                    Headless = true,
                    ExecutablePath = @"C:\Program Files\Google\Chrome\Application\chrome.exe",
                    Product = Product.Chrome
                };
                var browser = await Puppeteer.LaunchAsync(options);
                var page = await browser.NewPageAsync();

                var tryLimit = 30;
                var currentTry = 0;

                while (currentTry < tryLimit)
                {
                    while (links.TryDequeue(out var nextUrl))
                    {
                        currentTry = 0;
                        try
                        {
                            logger.LogInformation("{threadId} Getting url {nextUrl}", threadId, nextUrl);
                            var pageNav = await page.GoToAsync(nextUrl);
                            //pageNav.Status

                            //TODO: record if we got a non-200 status code

                            var metrics = await page.MetricsAsync();
                            var urls = await page.EvaluateExpressionAsync<string[]>(@"Array.from(document.querySelectorAll('a')).map(a => a.href);");

                            //TODO: handle case where page has a canonical link that points to a page different than itself

                            foreach (var url in urls.Select(t => (t.Contains('#') ? t[..t.IndexOf('#')] : t).ToLower().TrimEnd(' ', '/')).GroupBy(t => t))
                            {
                                var cleanUrl = url.First();
                                if (string.IsNullOrWhiteSpace(cleanUrl) || !Uri.IsWellFormedUriString(cleanUrl, UriKind.RelativeOrAbsolute))
                                    continue;

                                //TODO: exclude loading images?
                                if (!string.IsNullOrWhiteSpace(cleanUrl) && Uri.IsWellFormedUriString(cleanUrl, UriKind.RelativeOrAbsolute) && BaseUri.IsBaseOf(new Uri(cleanUrl)))
                                    if (linkBag.Add(cleanUrl))
                                        links.Enqueue(cleanUrl);

                                //TODO: Create relationships (and optionally target nodes) from {nextUrl} node to to all nodes in {urls}
                                var targetPage = new PageInfo { Url = cleanUrl };
                                await graphClient.Cypher
                                    .Merge("(targetPage:Page { Url: {url} })")
                                    .OnCreate()
                                    .Set("targetPage = {targetPage}")
                                    .WithParams(new
                                    {
                                        url = targetPage.Url,
                                        targetPage
                                    })
                                    .ExecuteWithoutResultsAsync();


                                await graphClient.Cypher
                                    .Match("(existingPage:Page)")
                                    .Where((PageInfo existingPage) => existingPage.Url == nextUrl)
                                    .Match("(targetPage:Page)")
                                    .Where((PageInfo targetPage) => targetPage.Url == cleanUrl)
                                    .Create("(existingPage)-[:LINKS {linkInfo}]->(targetPage)")
                                    .WithParams(new
                                    {
                                        linkInfo = new LinkInfo { }
                                    }).ExecuteWithoutResultsAsync();

                            }

                            //Create relationships + score to keywords if found on page
                            //TODO: Affect score based on position on the page
                            var keywordSources = new List<(string tag, string[] data, int score)>
                            {
                                new("a", await page.EvaluateExpressionAsync<string[]>(@"Array.from(document.querySelectorAll('a')).map(a => a.textContent.replace( /[\r\n]+/gm, ' ').toLowerCase());"), 4),
                                new("h", await page.EvaluateExpressionAsync<string[]>(@"Array.from(document.querySelectorAll('h1,h2,h3,h4,h5')).map(a => a.textContent.replace( /[\r\n]+/gm, ' ').toLowerCase());"), 4),
                                new("meta", await page.EvaluateExpressionAsync<string[]>(@"Array.from(document.querySelectorAll('title,meta[name=Description]')).map(a => a.textContent.replace( /[\r\n]+/gm, ' ').toLowerCase() || a.content.replace( /[\r\n]+/gm, ' ').toLowerCase());"), 5),
                                new("img", await page.EvaluateExpressionAsync<string[]>(@"Array.from(document.querySelectorAll('img')).map(a => a.alt.replace( /[\r\n]+/gm, ' ').toLowerCase());"), 3)
                            };

                            foreach (var (tag, data, score) in keywordSources)
                            {
                                var alreadyLinked = new HashSet<string>();
                                foreach (var element in data)
                                    foreach (string word in trie.Find(element))
                                    {
                                        if (alreadyLinked.Add(word))
                                            await graphClient.Cypher
                                                .Match("(existingPage:Page)")
                                                .Where((PageInfo existingPage) => existingPage.Url == nextUrl)

                                                .Match("(keyword:Keyword)")
                                                .Where((KeywordInfo keyword) => keyword.Keyword == word)

                                                .Create("(existingPage)-[:KEYUSED {linkInfo}]->(keyword)")
                                                .WithParams(new
                                                {
                                                    linkInfo = new KeywordLinkInfo { Type = tag, Score = score }
                                                })
                                                .ExecuteWithoutResultsAsync();
                                    }
                            }

                            var alreadyLinkedContent = new HashSet<string>();
                            foreach (string word in trie.Find(await page.GetContentAsync().ContinueWith(t => t.Result.ToLower())))
                            {
                                if (alreadyLinkedContent.Add(word))
                                    await graphClient.Cypher
                                        .Match("(existingPage:Page)")
                                        .Where((PageInfo existingPage) => existingPage.Url == nextUrl)

                                        .Match("(keyword:Keyword)")
                                        .Where((KeywordInfo keyword) => keyword.Keyword == word)

                                        .Create("(existingPage)-[:KEYUSED {linkInfo}]->(keyword)")
                                        .WithParams(new
                                        {
                                            linkInfo = new KeywordLinkInfo { Type = "content", Score = 1 }
                                        })
                                        .ExecuteWithoutResultsAsync();
                            }
                        }
                        catch (Exception ex)
                        {
                            logger.LogError(ex, "Error navigating to url");
                        }
                    }
                    currentTry++;
                    await Task.Delay(1000);
                    logger.LogInformation("{thread} pausing", threadId);
                }
                logger.LogInformation("{thread} ending", threadId);
            }
            catch (Exception e)
            {
                logger.LogError(e, "{thread} starting", threadId);
            }
        }

        private static async Task<IEnumerable<string>> CreateKeywordNodesAsync(ILoggerFactory loggerFactory, IGraphClient graphClient)
        {
            var logger = loggerFactory.CreateLogger<Program>();
            logger.LogInformation("Creating keyword nodes");

            var keywords = await File.ReadAllLinesAsync("keywords.txt");
            var uniqueKeywords = keywords.Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t.ToLower().Trim()).Distinct();

            await graphClient.Cypher
                .Unwind(uniqueKeywords, "keywords")
                .ForEach("(word IN keywords |")
                .Create("(keyword:Keyword {Keyword: word}) )")
                .ExecuteWithoutResultsAsync();
                
            return uniqueKeywords;
        }

        private static async Task GenerateSpreadsheet(ILoggerFactory loggerFactory, IGraphClient graphClient, string siteUrl)
        {
            try
            {
                var logger = loggerFactory.CreateLogger<Program>();
                logger.LogInformation("Generating analysis file");

                using (var analysisFile = new FileStream($"{siteUrl}.xlsx", FileMode.Create))
                using (var package = new ExcelPackage(analysisFile))
                {
                    var pageRankSheet = package.Workbook.Worksheets.Add("PageRank");
                    var pageRankResults = await graphClient.Cypher.Call("gds.pageRank.stream('pagerank')")
                        .Yield("nodeId, score")
                        .Return(() => new { Url = Return.As<string>("gds.util.asNode(nodeId).Url"), Score = Return.As<float>("score") })
                        .OrderByDescending("score").ResultsAsync;
                    pageRankSheet.Cells["A1"].LoadFromCollection(pageRankResults, true, TableStyles.Medium4).AutoFitColumns();
                    

                    var keywordsUsedSheet = package.Workbook.Worksheets.Add("Keywords Used");
                    var keywordsUsedResults = await graphClient.Cypher.Match("(p:Page)-[keyw:KEYUSED]->(k:Keyword)")
                        .Return(() => new { Url = Return.As<string>("k.Keyword"), Score = Return.As<int>("SUM(keyw.Score)"), Pages = Return.As<int>("COUNT(p)") })
                        .OrderByDescending("SUM(keyw.Score)").ResultsAsync;
                    keywordsUsedSheet.Cells["A1"].LoadFromCollection(keywordsUsedResults, true, TableStyles.Medium4).AutoFitColumns();


                    var nodeTypeWorkbooks = new List<(string tag, string workbookName)> 
                    {
                        new("a", "Keywords in links"),
                        new("h", "Keywords in headings"),
                        new("meta", "Keywords in meta tags"),
                        new("img", "Keywords in img alt"),
                        new("content", "Keywords in content"),
                    };
                    foreach (var type in nodeTypeWorkbooks)
                    {
                        var nodeTypeSheet = package.Workbook.Worksheets.Add(type.workbookName);
                        var nodeTypeResults = await graphClient.Cypher
                            
                            .Match(@$"(p:Page)-[keyw:KEYUSED{{Type:""{type.tag}""}}]->(k:Keyword)")
                            
                            .Return(() => new { Url = Return.As<string>("k.Keyword"), Score = Return.As<int>("SUM(keyw.Score)"), Pages = Return.As<int>("COUNT(p)") })
                            .OrderByDescending("SUM(keyw.Score)").ResultsAsync;
                        nodeTypeSheet.Cells["A1"].LoadFromCollection(nodeTypeResults, true, TableStyles.Medium4).AutoFitColumns();
                    }


                    var inboundLinksSheet = package.Workbook.Worksheets.Add("Inbound Links");
                    var inboundLinksResults = await graphClient.Cypher.Match("()-[l:LINKS]->(p:Page)")
                        .Return(() => new { Url = Return.As<string>("p.Url"), Links = Return.As<int>("COUNT(l)") })
                        .OrderByDescending("COUNT(l)").ResultsAsync;
                    inboundLinksSheet.Cells["A1"].LoadFromCollection(inboundLinksResults, true, TableStyles.Medium4).AutoFitColumns();


                    var outboundLinksSheet = package.Workbook.Worksheets.Add("Outbound Links");
                    var outboundLinksResults = await graphClient.Cypher.Match("(p:Page)-[l:LINKS]->()")
                        .Return(() => new { Url = Return.As<string>("p.Url"), Links = Return.As<int>("COUNT(l)") })
                        .OrderByDescending("COUNT(l)").ResultsAsync;
                    outboundLinksSheet.Cells["A1"].LoadFromCollection(outboundLinksResults, true, TableStyles.Medium4).AutoFitColumns();


                    package.Save();
                }
            }
            catch (Exception e)
            {
                throw;
            }
        }
    }
}

