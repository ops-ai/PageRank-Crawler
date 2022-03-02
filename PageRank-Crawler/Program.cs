using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Neo4jClient;
using Neo4jClient.Cypher;
using NLog.Extensions.Logging;
using OfficeOpenXml;
using OfficeOpenXml.Table;
using PageRank_Crawler;
using PuppeteerSharp;
using System.Collections.Concurrent;

namespace PageRankCrawler
{
    public class Program
    {
        public static void Main(string[] args)
        {
            var graphClient = new GraphClient(new Uri("http://localhost:7474/db/data/"), "neo4j", "P@ssw0rd1");
            graphClient.ConnectAsync().ConfigureAwait(false).GetAwaiter().GetResult();

            var serviceProvider = new ServiceCollection()
                .AddLogging(config => config.AddConsole().AddNLog())
                .AddSingleton<IGraphClient>(graphClient)
                .BuildServiceProvider();

            MainAsync(serviceProvider).ConfigureAwait(false).GetAwaiter().GetResult();
        }

        private static readonly Uri BaseUri = new("https://premierpups.com/");
        private static readonly HashSet<string> linkBag = new();
        private static AhoCorasick.Trie trie = new();
        private static readonly ConcurrentQueue<string> links = new(new List<string> { BaseUri.ToString() });

        private static async Task MainAsync(ServiceProvider serviceProvider)
        {
            var loggerFactory = serviceProvider.GetRequiredService<ILoggerFactory>();
            var logger = loggerFactory.CreateLogger<Program>();
            var graphClient = serviceProvider.GetRequiredService<IGraphClient>();

            var existingPages = await graphClient.Cypher.Match("(p:Page)").Return<string>("p.Url").ResultsAsync.ConfigureAwait(false);
            if (existingPages.Any())
            {
                logger.LogError("Database not clean");
                return;
            }

            var uniqueKeywords = await CreateKeywordNodesAsync(loggerFactory, graphClient).ConfigureAwait(false);
            foreach (var keyword in uniqueKeywords)
                trie.Add(keyword);
            trie.Build();

            await graphClient.Cypher.Create("INDEX ON :Keyword(Keyword)").ExecuteWithoutResultsAsync().ConfigureAwait(false);
            await graphClient.Cypher.Create("INDEX ON :Page(Url)").ExecuteWithoutResultsAsync().ConfigureAwait(false);

            logger.LogInformation("Starting scrape!");

            var newPage = new PageInfo { Url = BaseUri.ToString() };
            await graphClient.Cypher
                .Create("(newPage:Page {newPage})")
                .WithParam("newPage", newPage)
                .MaxExecutionTime(30000)
                .ExecuteWithoutResultsAsync().ConfigureAwait(false);

            var tasks = new List<Task>();
            for (var i = 0; i < Environment.ProcessorCount; i++)
                tasks.Add(ProcessLinksAsync(serviceProvider, i));

            Task.WaitAll(tasks.ToArray());

            
            logger.LogInformation("Done scraping");

            //TODO: merge canonical pages?

            //TODO: merge paging


            //calculate Page Rank
            await graphClient.Cypher.Call(@"gds.graph.create('pagerank', 'Page', 'LINKS', { })").MaxExecutionTime(30000).ExecuteWithoutResultsAsync().ConfigureAwait(false);
            logger.LogInformation("Created named graph for page rank");

            await graphClient.Cypher.Call("gds.pageRank.write('pagerank', { maxIterations: 20, dampingFactor: 0.85, writeProperty: 'PageRank' })")
                        .Yield("nodePropertiesWritten, ranIterations").MaxExecutionTime(30000).ExecuteWithoutResultsAsync().ConfigureAwait(false);
            logger.LogInformation("Exported pagerank scores");
            try { await graphClient.Cypher.Call(@"gds.graph.drop('pagerank')").ExecuteWithoutResultsAsync().ConfigureAwait(false); } catch { }


            //await graphClient.Cypher.Call(@"gds.graph.create('keywordrank', ['Keyword','Page'], 'KEYUSED', { relationshipProperties: 'PageRank' })").ExecuteWithoutResultsAsync();
            //logger.LogInformation("Created named graph for keyword rank");

            //await graphClient.Cypher.Call("gds.pageRank.write('keywordrank', { maxIterations: 20, dampingFactor: 0.85, writeProperty: 'KeywordRank' })")
            //            .Yield("nodePropertiesWritten, ranIterations").ExecuteWithoutResultsAsync();
            //logger.LogInformation("Exported keyword rank scores");
            //try { await graphClient.Cypher.Call(@"gds.graph.drop('keywordrank')").ExecuteWithoutResultsAsync(); } catch { }


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
                var browser = await Puppeteer.LaunchAsync(options).ConfigureAwait(false);
                
                var page = await browser.NewPageAsync().ConfigureAwait(false);
                
                //await page.Tracing.StartAsync(new TracingOptions { Path = "" }).ConfigureAwait(false);

                //await page.Tracing.StopAsync().ConfigureAwait(false);
                //await page.CloseAsync().ConfigureAwait(false);

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
                            Response? pageNav = null;

                            var tries = 0;
                            do
                            {
                                try
                                {
                                    pageNav = await page.GoToAsync(nextUrl, 30000).ConfigureAwait(false);
                                }
                                catch (Exception ex)
                                {
                                    logger.LogError(ex, "Error navigating to page");
                                    tries++;
                                }
                            } while (pageNav == null || tries < 3);

                            if (pageNav == null)
                            {
                                logger.LogError("Failed to navigate to page {page}", nextUrl);
                                continue;
                            }

                            if (!pageNav.Ok)
                            {
                                await graphClient.Cypher
                                    .Match("(page:Page)")
                                    .Where((PageInfo page) => page.Url == nextUrl)
                                    .Set("page.StatusCode = {status}")
                                    .WithParam("status", (int)pageNav.Status)
                                    .MaxExecutionTime(30000)
                                    .ExecuteWithoutResultsAsync().ConfigureAwait(false);
                                continue;
                            }

                            var metrics = await page.MetricsAsync();

                            await graphClient.Cypher
                                .Match("(page:Page)")
                                .Where((PageInfo page) => page.Url == nextUrl)
                                
                                .Set("page.StatusCode = {status}, page.LayoutDuration = {LayoutDuration}, page.ScriptDuration = {ScriptDuration}, page.TaskDuration = {TaskDuration}, page.JSHeapUsedSize = {JSHeapUsedSize}, page.JSHeapTotalSize = {JSHeapTotalSize}, page.Nodes = {Nodes}, page.JSEventListeners = {JSEventListeners}")
                                .WithParams(new Dictionary<string, object> {
                                    { "status", (int)pageNav.Status },
                                    { "LayoutDuration", metrics["LayoutDuration"] },
                                    { "ScriptDuration", metrics["ScriptDuration"] },
                                    { "TaskDuration", metrics["TaskDuration"] },
                                    { "JSHeapUsedSize", metrics["JSHeapUsedSize"] },
                                    { "JSHeapTotalSize", metrics["JSHeapTotalSize"] },
                                    { "Nodes", metrics["Nodes"] },
                                    { "JSEventListeners", metrics["JSEventListeners"] }
                                })
                                .MaxExecutionTime(30000)
                                .ExecuteWithoutResultsAsync().ConfigureAwait(false);

                            var urls = await page.EvaluateExpressionAsync<string[]>(@"Array.from(document.querySelectorAll('a')).map(a => a.href);").ConfigureAwait(false);

                            //TODO: handle case where page has a canonical link that points to a page different than itself

                            foreach (var url in urls.Select(t => (t.Contains('#') ? t[..t.IndexOf('#')] : t).ToLower().TrimEnd(' ', '/', '?')).GroupBy(t => t))
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
                                    .MaxExecutionTime(30000)
                                    .ExecuteWithoutResultsAsync().ConfigureAwait(false);


                                await graphClient.Cypher
                                    .Match("(existingPage:Page)")
                                    .Where((PageInfo existingPage) => existingPage.Url == nextUrl)
                                    .Match("(targetPage:Page)")
                                    .Where((PageInfo targetPage) => targetPage.Url == cleanUrl)
                                    .Create("(existingPage)-[:LINKS {linkInfo}]->(targetPage)")
                                    .WithParams(new
                                    {
                                        linkInfo = new LinkInfo { }
                                    })
                                    .MaxExecutionTime(30000)
                                    .ExecuteWithoutResultsAsync().ConfigureAwait(false);

                            }

                            //Create relationships + score to keywords if found on page
                            //TODO: Affect score based on position on the page
                            var keywordSources = new List<(string tag, string[] data, int score)>
                            {
                                new("a", await page.EvaluateExpressionAsync<string[]>(@"Array.from(document.querySelectorAll('a')).map(a => a.textContent.replace( /[\r\n]+/gm, ' ').trim().toLowerCase()).filter(t => t != '');").ConfigureAwait(false), 4),
                                new("h", await page.EvaluateExpressionAsync<string[]>(@"Array.from(document.querySelectorAll('h1,h2,h3,h4,h5')).map(a => a.textContent.replace( /[\r\n]+/gm, ' ').trim().toLowerCase()).filter(t => t != '');").ConfigureAwait(false), 4),
                                new("meta", await page.EvaluateExpressionAsync<string[]>(@"Array.from(document.querySelectorAll('title,meta[name=Description]')).map(a => a.textContent.replace( /[\r\n]+/gm, ' ').trim().toLowerCase() || a.content.replace( /[\r\n]+/gm, ' ').trim().toLowerCase()).concat(window.location.href.replace(/[^a-zA-Z0-9]+/gm, ' ')).filter(t => t != '');").ConfigureAwait(false), 5),
                                new("img", await page.EvaluateExpressionAsync<string[]>(@"Array.from(document.querySelectorAll('img')).map(a => a.alt.replace( /[\r\n]+/gm, ' ').trim().toLowerCase()).filter(t => t != '');").ConfigureAwait(false), 3)
                            };

                            foreach (var (tag, data, score) in keywordSources)
                            {
                                var alreadyLinked = new HashSet<string>();
                                try
                                {
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
                                                    .MaxExecutionTime(30000)
                                                    .ExecuteWithoutResultsAsync().ConfigureAwait(false);
                                        }
                                }
                                catch (Exception ex)
                                {
                                    logger.LogWarning(ex, "Error searching for keywords");
                                }
                            }

                            var alreadyLinkedContent = new HashSet<string>();
                            foreach (string word in trie.Find(await page.GetContentAsync().ContinueWith(t => t.Result.ToLower()).ConfigureAwait(false)))
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
                                        .MaxExecutionTime(30000)
                                        .ExecuteWithoutResultsAsync().ConfigureAwait(false);
                            }
                        }
                        catch (Exception ex)
                        {
                            logger.LogError(ex, "Error navigating to url");
                        }
                    }
                    currentTry++;
                    await Task.Delay(1000).ConfigureAwait(false);
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

            var keywords = await File.ReadAllLinesAsync("keywords.txt").ConfigureAwait(false);
            var uniqueKeywords = keywords.Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t.ToLower().Trim()).Distinct();

            await graphClient.Cypher
                .Unwind(uniqueKeywords, "keywords")
                .ForEach("(word IN keywords |")
                .Create("(keyword:Keyword {Keyword: word}) )")
                .MaxExecutionTime(30000)
                .ExecuteWithoutResultsAsync().ConfigureAwait(false);
                
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
                    var inboundLinksSheet = package.Workbook.Worksheets.Add("Inbound Links");
                    var inboundLinksResults = await graphClient.Cypher.Match("()-[l:LINKS]->(p:Page)")
                        .Return(() => new { Url = Return.As<string>("p.Url"), Links = Return.As<int>("COUNT(l)"), PageRank = Return.As<float>("p.PageRank") })
                        .OrderByDescending("COUNT(l)")
                        .ResultsAsync.ConfigureAwait(false);
                    inboundLinksSheet.Cells["A1"].LoadFromCollection(inboundLinksResults, true, TableStyles.Medium4).AutoFitColumns();


                    var outboundLinksSheet = package.Workbook.Worksheets.Add("Outbound Links");
                    var outboundLinksResults = await graphClient.Cypher.Match("(p:Page)-[l:LINKS]->()")
                        .Return(() => new { Url = Return.As<string>("p.Url"), Links = Return.As<int>("COUNT(l)"), PageRank = Return.As<float>("p.PageRank") })
                        .OrderByDescending("COUNT(l)").ResultsAsync.ConfigureAwait(false);
                    outboundLinksSheet.Cells["A1"].LoadFromCollection(outboundLinksResults, true, TableStyles.Medium4).AutoFitColumns();


                    var keywordsUsedSheet = package.Workbook.Worksheets.Add("Keywords Used");
                    var keywordsUsedResults = await graphClient.Cypher.Match("(p:Page)-[keyw:KEYUSED]->(k:Keyword)")
                        .Return(() => new { Url = Return.As<string>("k.Keyword"), Score = Return.As<int>("SUM(keyw.Score)"), Pages = Return.As<int>("COUNT(p)")/*, KeywordRank = Return.As<float>("p.KeywordRank")*/ })
                        .OrderByDescending("SUM(keyw.Score)").ResultsAsync.ConfigureAwait(false);
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
                            
                            .Return(() => new { Url = Return.As<string>("k.Keyword"), Score = Return.As<int>("SUM(keyw.Score)"), Pages = Return.As<int>("COUNT(p)")/*, KeywordRank = Return.As<float>("p.KeywordRank")*/ })
                            .OrderByDescending("SUM(keyw.Score)").ResultsAsync.ConfigureAwait(false);
                        nodeTypeSheet.Cells["A1"].LoadFromCollection(nodeTypeResults, true, TableStyles.Medium4).AutoFitColumns();
                    }


                    var pageStatsSheet = package.Workbook.Worksheets.Add("Page Stats");
                    var pageStatsResults = await graphClient.Cypher.Match("(p:Page)")
                        .Return<PageInfo>("p")
                        .OrderByDescending("p.PageRank").ResultsAsync.ConfigureAwait(false);
                    pageStatsSheet.Cells["A1"].LoadFromCollection(pageStatsResults, true, TableStyles.Medium4).AutoFitColumns();

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

