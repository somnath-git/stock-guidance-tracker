using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using HtmlAgilityPack;
using Newtonsoft.Json;
using System.Text.RegularExpressions;
using System.Linq;
using System.Text;

namespace StockAnalysisAgent
{
    class Program
    {
        private static readonly HttpClient httpClient = new HttpClient();
        private static readonly string outputDirectory = @"C:\Users\somnathghosh\Music\investment";
        private static readonly string geminiApiKey = "AIzaSyCvaqXrKO2PfzcH6lbxTmcrLJuFjrXaifE"; // Replace with your actual API key
        private static readonly string processedStocksFile = Path.Combine(outputDirectory, "processed-stocks.txt");

        static async Task Main(string[] args)
        {
            Console.WriteLine("Stock Analysis Agent Started...");

            // Create output directory if it doesn't exist
            Directory.CreateDirectory(outputDirectory);

            // Configure HttpClient
            httpClient.DefaultRequestHeaders.Add("User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");

            // Process pages 2 to 10
            for (int page = 1; page <= 10; page++)
            {
                Console.WriteLine($"Processing page {page}...");
                await ProcessPage(page);

                // Add delay to avoid overwhelming the server
                await Task.Delay(2000);
            }

            Console.WriteLine("Stock Analysis Agent Completed!");
            Console.ReadKey();
        }

        private static async Task ProcessPage(int pageNumber)
        {
            try
            {
                string url = $"https://www.screener.in/company/NFMICRO250/?page={pageNumber}";
                string html = await httpClient.GetStringAsync(url);

                var doc = new HtmlDocument();
                doc.LoadHtml(html);

                var stockLinks = ExtractStockLinks(doc);
                var processedStocks = LoadProcessedStocks();

                foreach (var stockLink in stockLinks)
                {
                    if (processedStocks.Contains(stockLink))
                    {
                        Console.WriteLine($"Skipping already processed stock: {stockLink}");
                        continue;
                    }

                    await ProcessStock(stockLink);
                    SaveProcessedStock(stockLink).Wait(); // Save processed stock link
                    await Task.Delay(1000 * 60); // Delay between stock processing
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error processing page {pageNumber}: {ex.Message}");
            }
        }

        private static List<string> ExtractStockLinks(HtmlDocument doc)
        {
            var links = new List<string>();

            // Extract stock links from the constituents table or any stock links
            var stockNodes = doc.DocumentNode
                .SelectNodes("//a[@href and contains(@href, '/company/')]");

            if (stockNodes != null)
            {
                foreach (var node in stockNodes)
                {
                    var href = node.GetAttributeValue("href", "");
                    if (!string.IsNullOrEmpty(href) && href.StartsWith("/company/") && !href.Contains("SMALLCA250"))
                    {
                        var fullUrl = "https://www.screener.in" + (href + (href.Contains("/consolidated") ? "" : "/consolidated")).Replace("//", "/");
                        //var fullUrl = "https://www.screener.in/company/HSCL/consolidated/";
                        if (!links.Contains(fullUrl))
                        {
                            links.Add(fullUrl);
                        }
                    }
                }
            }

            return links;
        }

        private static async Task ProcessStock(string stockUrl)
        {
            try
            {
                Console.WriteLine($"Processing stock: {stockUrl}");

                string html = await httpClient.GetStringAsync(stockUrl);
                var doc = new HtmlDocument();
                doc.LoadHtml(html);

                var stockInfo = await ExtractStockInfoWithLLM(doc, stockUrl);

                // Skip if it's a bank or financial sector stock
                if (await IsBankOrFinancialSectorLLM(stockInfo))
                {
                    Console.WriteLine($"Skipping {stockInfo.CompanyName} - Bank/Financial sector");
                    return;
                }

                // Find guidance and calculate CAGR using LLM
                var guidanceInfo = await FindGuidanceWithLLM(stockInfo, doc);

                if (guidanceInfo != null && guidanceInfo.isValidGuidance != null && (bool)guidanceInfo.isValidGuidance)
                {
                    // Generate growth summary using Gemini AI
                    //var growthSummary = await GenerateGrowthSummary(stockInfo, guidanceInfo);

                    // Create and save the stock report
                    await CreateStockReport(stockInfo, guidanceInfo);
                    Console.ForegroundColor = ConsoleColor.Green;

                    Console.WriteLine($"✓ Created report for {stockInfo.CompanyName} (CAGR: {guidanceInfo.CAGR:F2}%)");
                    Console.ResetColor();
                }
                else
                {
                    Console.WriteLine($"✗ Skipped {stockInfo.CompanyName} - CAGR below 20% or no guidance found");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error processing stock {stockUrl}: {ex.Message}");
            }
        }

        private static async Task<StockInfo> ExtractStockInfoWithLLM(HtmlDocument doc, string stockUrl)
        {
            // Extract raw HTML content for LLM analysis
            var pageContent = ExtractRelevantContent(doc);

            var prompt = $@"

            
            Analyze the following stock page content and extract the following information in JSON format:
            
            {{
                ""companyName"": ""company name"",
                ""sector"": ""sector/industry"",
                ""currentRevenue"": revenue_in_crores_as_number,
                ""marketCap"": market_cap_in_crores_as_number,
                ""FY2025revenue"" : revenue_for_fy25_in_crores_as_number
            }}
            
            Page Content:
            {pageContent}

EXAMPLE FORMAT (DO NOT USE THIS DATA - THIS IS JUST TO SHOW THE HTML STRUCTURE):
<div class=""responsive-holder fill-card-width"" data-result-table>
  <table class=""data-table responsive-text-nowrap"">
    <thead>
      <tr>
        <th class=""text""></th>
        <th class="""">Mar 2023</th>
        <th class="""">Mar 2024</th>
        <th class="""">Mar 2025</th>
        <th class="""">TTM</th>
      </tr>
    </thead>
    <tbody>
      <tr class=""stripe"">
        <td class=""text"">
          <button class=""button-plain"">
            Sales&nbsp;<span class=""blue-icon"">+</span>
          </button>
        </td>
        <td class="""">539</td>
        <td class="""">2,587</td>
        <td class="""">4,578</td>
        <td class="""">4,377</td>
      </tr>
    </tbody>
  </table>
</div>
In this example, the FY2025 revenue would be 4,578 crores (Mar 2025 column under Sales row).
            
            Return only valid JSON without any additional text or explanation.
            ";

            var response = await CallGeminiAPI(prompt);

            try
            {
                var stockInfo = JsonConvert.DeserializeObject<StockInfo>(ExtractJsonFromCodeBlock(response));
                return stockInfo ?? new StockInfo { CompanyName = "Unknown" };
            }
            catch (Exception ex)
            {
                // Fallback to basic extraction
                return new StockInfo
                {
                    CompanyName = doc.DocumentNode.SelectSingleNode("//h1")?.InnerText?.Trim() ?? "Unknown"
                };
            }
        }

        private static string ExtractRelevantContent(HtmlDocument doc)
        {
            var content = new StringBuilder();

            // Extract key sections
            var titleNode = doc.DocumentNode.SelectSingleNode("//h1");
            if (titleNode != null)
                content.AppendLine($"Company: {titleNode.InnerText.Trim()}");

            // Extract ratios and financial data
            var ratioNodes = doc.DocumentNode.SelectNodes("//li[contains(@class, 'ratio') or .//span[contains(text(), 'Sales') or contains(text(), 'Revenue') or contains(text(), 'Market Cap') or contains(text(), 'Sector')]]");
            if (ratioNodes != null)
            {
                foreach (var node in ratioNodes)
                {
                    content.AppendLine(node.InnerText.Trim());
                }
            }

            var mcNumberNode = doc.DocumentNode.SelectSingleNode(
        "//li[span[@class='name' and contains(text(),'Market Cap')]]" +
        "//span[contains(@class,'number')]");

    if (mcNumberNode != null)
    {
        var mcClean = mcNumberNode.InnerText.Replace(",", "").Trim(); // "48864"
        content.AppendLine($"Market Cap: {mcClean} Cr");
    }

            var profitLossSection = doc.DocumentNode.SelectSingleNode("//section[@id='profit-loss']");
            if (profitLossSection != null)
            {
                content.AppendLine("=== PROFIT & LOSS SECTION ===");

                // Extract the data table from profit-loss section
                var profitLossTable = profitLossSection.SelectSingleNode(".//table[@class='data-table responsive-text-nowrap']");
                if (profitLossTable != null)
                {
                    // Extract table headers
                    var headers = profitLossTable.SelectNodes(".//thead//th");
                    if (headers != null)
                    {
                        var headerText = string.Join(" | ", headers.Select(h => h.InnerText.Trim()));
                        content.AppendLine($"Headers: {headerText}");
                    }

                    // Extract table rows
                    var rows = profitLossTable.SelectNodes(".//tbody//tr");
                    if (rows != null)
                    {
                        foreach (var row in rows)
                        {
                            var cells = row.SelectNodes(".//td");
                            if (cells != null)
                            {
                                var rowText = string.Join(" | ", cells.Select(c => c.InnerText.Trim()));
                                content.AppendLine(rowText);
                            }
                        }
                    }
                }
            }

            // Extract any sector/industry information
            var aboutSection = doc.DocumentNode.SelectSingleNode("//div[contains(@class, 'about') or .//div[text()='About']]");
            if (aboutSection != null)
            {
                content.AppendLine(aboutSection.InnerText.Trim());
            }

            return content.ToString();
        }

        private static async Task<bool> IsBankOrFinancialSectorLLM(StockInfo stockInfo)
        {
            var prompt = $@"
            Analyze if the following company belongs to banking or financial services sector:
            
            Company Name: {stockInfo.CompanyName}
            Sector: {stockInfo.Sector}
            
            Return only 'true' if it's a bank, NBFC, insurance company, mutual fund, or any other financial services company.
            Return only 'false' if it's not from financial sector.
            
            Response should be only 'true' or 'false' without any additional text.
            ";

            var response = await CallGeminiAPI(prompt);
            return response?.Trim().ToLower() == "true";
        }

        private static async Task<GuidanceInfo> FindGuidanceWithLLM(StockInfo stockInfo, HtmlDocument doc)
        {
            try
            {
                // First, search for guidance in company announcements and investor presentations
                var guidanceContent = await SearchCompanyGuidanceWithLLM(stockInfo);

                if (string.IsNullOrEmpty(guidanceContent))
                {
                    Console.WriteLine($"No guidance content found for {stockInfo.CompanyName}");
                    return null;
                }

                // Use LLM to extract and analyze guidance
                var guidanceAnalysis = await AnalyzeGuidanceWithLLM(stockInfo, guidanceContent);

                return guidanceAnalysis;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error finding guidance for {stockInfo.CompanyName}: {ex.Message}");
                return null;
            }
        }

        private static async Task<string> SearchCompanyGuidanceWithLLM(StockInfo stockInfo)
        {
            var searchResults = new StringBuilder();
            try
            {
                var searchContent = await SearchWebForGuidance(stockInfo.CompanyName);
                if (!string.IsNullOrEmpty(searchContent))
                {
                    searchResults.AppendLine(searchContent);
                }

                await Task.Delay(1000); // Delay between searches
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Search error: {ex.Message}");
            }

            return searchResults.ToString();
        }

        private static async Task<string> SearchWebForGuidance(string companyName)
        {
            // Use LLM to generate search queries and simulate finding guidance
            var prompt = $@"
           Revenue guidance of {companyName} by management. Keep it short. Mention either cagr growth or revenue amount guidance
           do web search and try to find some number
            ";

            var response = await CallGeminiAPI(prompt);
            return response ?? "";
        }

        private static async Task<GuidanceInfo> AnalyzeGuidanceWithLLM(StockInfo stockInfo, string guidanceContent)
        {
            var prompt = $@"
            Analyze the following guidance content for {stockInfo.CompanyName} and extract guidance information:
            FY2025 Revenue: ₹{stockInfo.FY2025revenue} crore
            Market cap : ₹{stockInfo.MarketCap}
            
            try to find only one of these below things, if not there then mark as invalid guidance
            1. CAGR growth mentioned in the text, try to find post 2025 CAGR growth, if not there check point 2
            2. Revenue guidance in a number format, e.g. '₹800 crore' or '800 crores' then try to find current revenue and calculate CAGR
            
            Guidance Content:
            {guidanceContent}
            
            Extract and return in JSON format:
            {{
                ""cagr"": if company has provided growth number, then set here, else calculate from current revenue and guidance amount, cagr_growth will be 0 if no guidance found,
                ""guidanceYear"": year_for_guidance,
                ""guidanceText"": ""brief_summary_of_guidance"",
                ""isValidGuidance"": true_or_false
            }}
            
            Rules:
            - Guidance amount should be in crores
            - Set isValidGuidance to false if no clear revenue guidance found
            - consider isValidGuidance false if market cap is more than 50,000 crores

            Example, refer it just o understand what i want 
            if the Guidance Content is 'Yatharth Hospital &Trauma Care Services Ltd's management has indicated a target of **30-35% revenue CAGR** over the medium term (typically 3-5 years).'
            {{
                ""cagr"": 35,
                ""guidanceYear"": null,
                ""guidanceText"": ""management has indicated a target of **30-35% revenue CAGR** over the medium term (typically 3-5 years)"",
                ""isValidGuidance"": true
            }}

            Return only valid JSON without additional text.
            ";

            var response = await CallGeminiAPI(prompt);

            try
            {
                dynamic guidanceData = JsonConvert.DeserializeObject(ExtractJsonFromCodeBlock(response));

                if (guidanceData?.isValidGuidance == true)
                {
                    bool? isValidGuidance = guidanceData.isValidGuidance;

                    return new GuidanceInfo
                    {
                        isValidGuidance = isValidGuidance,
                        GuidanceText = guidanceData.guidanceText,
                        CAGR = guidanceData.cagr,
                        GuidanceYear = guidanceData.guidanceYear
                    };
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error parsing guidance analysis: {ex.Message}");
            }

            return null;
        }

        static string ExtractJsonFromCodeBlock(string response)
        {
            if (string.IsNullOrEmpty(response))
            {
                return "";
            }
            var firstBrace = response.IndexOf('{');
            var lastBrace = response.LastIndexOf('}');
            if (firstBrace >= 0 && lastBrace > firstBrace)
                response = response.Substring(firstBrace, lastBrace - firstBrace + 1);

            return response;
        }

        private static double CalculateCAGR(double currentValue, double futureValue, int years)
        {
            if (currentValue <= 0 || futureValue <= 0 || years <= 0)
                return 0;

            return (Math.Pow(futureValue / currentValue, 1.0 / years) - 1) * 100;
        }

        private static async Task<string> CallGeminiAPI(string prompt)
        {
            Random rnd = new Random();
            int number = rnd.Next(10000, 20001);
            Task.Delay(number).Wait(); // Delay to avoid rate limits
            try
            {
                var requestBody = new
                {
                    contents = new[]
                    {
                        new
                        {
                            parts = new[]
                            {
                                new { text = prompt }
                            }
                        }
                    },
                    generationConfig = new
                    {
                        temperature = 0.7,
                        maxOutputTokens = 2048
                    }
                };

                var json = JsonConvert.SerializeObject(requestBody);
                var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
                var url = $"https://generativelanguage.googleapis.com/v1beta/models/gemini-2.5-flash:generateContent?key={geminiApiKey}";
                var response = await httpClient.PostAsync(
                   url,
                    content);

                if (response.IsSuccessStatusCode)
                {
                    var responseJson = await response.Content.ReadAsStringAsync();
                    dynamic result = JsonConvert.DeserializeObject(responseJson);
                    return result?.candidates?[0]?.content?.parts?[0]?.text?.ToString();
                }
                else
                {
                    Console.WriteLine($"Gemini API error: {response.StatusCode}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Gemini API error: {ex.Message}");
            }

            return null;
        }

        private static async Task CreateStockReport(StockInfo stockInfo, GuidanceInfo guidanceInfo)
        {
            var fileName = SanitizeFileName(stockInfo.CompanyName) + ".txt";
            var filePath = Path.Combine(outputDirectory, fileName);

            var report = $@"Company Name: {stockInfo.CompanyName}


Current Revenue (FY25): ₹{stockInfo.CurrentRevenue} crore
Expected CAGR: {guidanceInfo.CAGR:F2}%

Guidance Summary: {guidanceInfo.GuidanceText}

Growth Analysis:
{guidanceInfo.GuidanceText}

Generated on: {DateTime.Now:yyyy-MM-dd HH:mm:ss}
";

            await File.WriteAllTextAsync(filePath, report);
        }

        private static string SanitizeFileName(string fileName)
        {
            var invalidChars = Path.GetInvalidFileNameChars();
            return string.Join("_", fileName.Split(invalidChars, StringSplitOptions.RemoveEmptyEntries));
        }

        private static HashSet<string> LoadProcessedStocks()
        {
            var processedStocks = new HashSet<string>();
            if (File.Exists(processedStocksFile))
            {
                var lines = File.ReadAllLines(processedStocksFile);
                foreach (var line in lines)
                {
                    if (!string.IsNullOrWhiteSpace(line))
                        processedStocks.Add(line.Trim());
                }
            }
            return processedStocks;
        }
        private static async Task SaveProcessedStock(string stockUrl)
        {
            await File.AppendAllTextAsync(processedStocksFile, stockUrl + Environment.NewLine);
        }
    }

    public class StockInfo
    {
        public string CompanyName { get; set; } = "";
        public string Sector { get; set; } = "";
        public double? CurrentRevenue { get; set; }
        public double? MarketCap { get; set; }
        public double? FY2025revenue { get; set; }
    }

    public class GuidanceInfo
    {
        public bool? isValidGuidance { get; set; }
        public string GuidanceText { get; set; } = "";
        public double? CAGR { get; set; }
        public int? GuidanceYear { get; set; }
    }
}