using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PuppeteerSharp;
using System.Collections.Concurrent;

namespace PageRankCrawler
{
    public class Program
    {
        public static void Main(string[] args)
        {
            var serviceProvider = new ServiceCollection()
                .AddLogging(config => config.AddConsole())
                .BuildServiceProvider();

            MainAsync(serviceProvider).GetAwaiter().GetResult();
        }

        private static readonly ConcurrentQueue<string> links = new(new List<string> { "https://wikipedia.org" });

        private static async Task MainAsync(ServiceProvider serviceProvider)
        {
            var logger = serviceProvider.GetRequiredService<ILoggerFactory>().CreateLogger<Program>();
            logger.LogInformation("Starting scrape!");

            var options = new LaunchOptions()
            {
                Headless = true,
                ExecutablePath = @"C:\Program Files\Google\Chrome\Application\chrome.exe",
                Product = Product.Chrome
            };
            var browser = await Puppeteer.LaunchAsync(options);
            var page = await browser.NewPageAsync();

            while (links.TryDequeue(out var nextUrl))
            {
                try
                {
                    logger.LogInformation("Getting url {nextUrl}", nextUrl);
                    await page.GoToAsync(nextUrl);
                    var anchors = @"Array.from(document.querySelectorAll('a')).map(a => a.href);";
                    var urls = await page.EvaluateExpressionAsync<string[]>(anchors);

                    foreach (string url in urls)
                    {
                        //TODO: Create relationships (and optionally target nodes) from {nextUrl} node to to all nodes in {urls}

                        if (!string.IsNullOrWhiteSpace(url) && Uri.IsWellFormedUriString(url, UriKind.RelativeOrAbsolute))
                            links.Enqueue(url);
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

