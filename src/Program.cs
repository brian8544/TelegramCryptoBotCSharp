using Telegram.Bot;
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using System.Text;
using System.Linq;

namespace CryptoTelegramBot
{
    class Program
    {
        private static string TELEGRAM_BOT_TOKEN;
        private static string COINMARKETCAP_API_KEY;
        private static string TELEGRAM_CHANNEL;

        private static bool AI_ENABLED;
        private static string AI_API_KEY;

        private static int UPDATE_INTERVAL_MS;
        private static int COUNTDOWN_INTERVAL_MS;

        private static DateTime _lastUpdateTime;

        // Request tracking
        private static DateTime _currentHourStart = DateTime.UtcNow;
        private static int _requestsThisHour = 1;

        // Price tracking
        private static readonly Dictionary<string, decimal> _lastPrices = new();
        private static readonly Queue<Dictionary<string, decimal>> _priceHistory = new();
        private const int HISTORY_POINTS = 12; // Keeps 6 hours of data with 30-minute intervals
        private static readonly string[] CRYPTO_SYMBOLS = { "BTC", "XRP", "ETH", "SOL" };

        // Time zone
        private static readonly TimeZoneInfo netherlandsTimeZone = TimeZoneInfo.FindSystemTimeZoneById("W. Europe Standard Time");

        // OpenRouter API
        private static readonly HttpClient openRouterClient = new HttpClient
        {
            BaseAddress = new Uri("https://openrouter.ai/api/v1/")
        };
        private class AiAnalysisRequest
        {
            public Message[] messages { get; set; }
            public string model { get; set; }
        }
        private class Message
        {
            public string role { get; set; }
            public string content { get; set; }
        }

        static async Task Main(string[] args)
        {
            // Load the config file
            var config = ConfigReader.ReadConfig("Config.conf");

            // Assign values from the config file
            TELEGRAM_BOT_TOKEN = config["TELEGRAM_BOT_TOKEN"];
            COINMARKETCAP_API_KEY = config["COINMARKETCAP_API_KEY"];
            TELEGRAM_CHANNEL = config["TELEGRAM_CHANNEL"];

            AI_ENABLED = bool.Parse(config["AI_ENABLED"]);
            AI_API_KEY = config["AI_API_KEY"];

            UPDATE_INTERVAL_MS = int.Parse(config["UPDATE_INTERVAL_MS"]);
            COUNTDOWN_INTERVAL_MS = int.Parse(config["COUNTDOWN_INTERVAL_MS"]);



            var botClient = new TelegramBotClient(TELEGRAM_BOT_TOKEN);
            var httpClient = new HttpClient();

            httpClient.DefaultRequestHeaders.Add("X-CMC_PRO_API_KEY", COINMARKETCAP_API_KEY);
            httpClient.DefaultRequestHeaders.Add("Accepts", "application/json");

            await SendStartupMessage(botClient);
            int updateCount = 0;
            DateTime lastSuccessfulUpdate = DateTime.UtcNow;
            _lastUpdateTime = DateTime.UtcNow;

            // Start the countdown timer
            _ = StartCountdownTimer();

            while (true)
            {
                try
                {
                    var prices = await FetchCryptoPrices(httpClient);
                    var message = await FormatUpdateMessage(prices, ++updateCount);
                    await SendTelegramMessage(botClient, message);

                    _requestsThisHour++;
                    lastSuccessfulUpdate = DateTime.UtcNow;
                    _lastUpdateTime = DateTime.UtcNow;
                    await Task.Delay(UPDATE_INTERVAL_MS);
                }
                catch (Exception ex)
                {
                    await HandleError(botClient, ex, lastSuccessfulUpdate);
                    await Task.Delay(5000);
                }
            }
        }

        private static async Task StartCountdownTimer()
        {
            while (true)
            {
                var elapsedTime = DateTime.UtcNow - _lastUpdateTime;
                var remainingTime = TimeSpan.FromMilliseconds(UPDATE_INTERVAL_MS) - elapsedTime;

                if (remainingTime.TotalSeconds > 0)
                {
                    Console.WriteLine($"Time remaining for next update: {remainingTime.Minutes} minutes and {remainingTime.Seconds} seconds.");
                }

                await Task.Delay(COUNTDOWN_INTERVAL_MS);
            }
        }

        private static double CalculateVolatility(List<decimal> prices)
        {
            if (prices.Count < 2) return 0;

            var returns = new List<double>();
            for (int i = 1; i < prices.Count; i++)
            {
                var returnValue = (double)((prices[i] - prices[i - 1]) / prices[i - 1] * 100);
                returns.Add(returnValue);
            }

            var mean = returns.Average();
            var sumSquares = returns.Sum(r => Math.Pow(r - mean, 2));
            var variance = sumSquares / (returns.Count - 1);
            return Math.Sqrt(variance);
        }

        private static async Task<string> GetAIAnalysis(Dictionary<string, (decimal Price, decimal Change, decimal PercentChange)> priceData)
        {
            if (!AI_ENABLED)
            {
                return "⚠️ AI Analysis is currently disabled.";
            }

            try
            {
                var context = new StringBuilder();
                context.AppendLine("Current Prices:");

                foreach (var (symbol, data) in priceData)
                {
                    var sign = data.Change >= 0 ? "+" : "";
                    context.AppendLine($"{symbol}: Current: €{data.Price:N2}, Change: {sign}{data.Change:N2} ({sign}{data.PercentChange:N2}%)");
                }

                var request = new AiAnalysisRequest
                {
                    model = "meta-llama/llama-3.1-70b-instruct:free",
                    messages = new[]
                    {
                new Message
                {
                    role = "user",
                    content = $@"
Analyze this cryptocurrency data and provide a simple trading summary:

{context}

Provide a brief overview:
1. Which coins are trending up/down?
2. Which coins have high volatility?
3. One key trading opportunity, if any.
4. Main risk to watch for.

Keep the analysis simple and actionable. Focus on the most important points only."
                }
            }
                };

                // Rest of the AI request code remains the same...
                openRouterClient.DefaultRequestHeaders.Clear();
                openRouterClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {AI_API_KEY}");
                openRouterClient.DefaultRequestHeaders.Add("HTTP-Referer", "https://brianoost.com/");
                openRouterClient.DefaultRequestHeaders.Add("X-Title", "Crypto Analysis Bot");

                var response = await openRouterClient.PostAsync(
                    "chat/completions",
                    new StringContent(JsonSerializer.Serialize(request), Encoding.UTF8, "application/json")
                );

                var responseContent = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    Console.WriteLine($"AI Analysis Error: {await response.Content.ReadAsStringAsync()}");
                    return "⚠️ AI Analysis unavailable at this time.";
                }

                using var document = JsonDocument.Parse(responseContent);
                var analysis = document.RootElement
                    .GetProperty("choices")[0]
                    .GetProperty("message")
                    .GetProperty("content")
                    .GetString();

                return $"\n\n📊 Quick Analysis:\n{analysis?.Trim()}";
            }
            catch (Exception ex)
            {
                Console.WriteLine($"AI Analysis Error: {ex.Message}");
                return "⚠️ AI Analysis unavailable at this time.";
            }
        }

        private static async Task<Dictionary<string, (decimal Eur, decimal Usd)>> FetchCryptoPrices(HttpClient httpClient)
        {
            Console.WriteLine("Fetching cryptocurrency prices...");
            var symbols = string.Join(",", CRYPTO_SYMBOLS);
            var prices = new Dictionary<string, (decimal Eur, decimal Usd)>();

            try
            {
                // Add retry logic with exponential backoff
                int maxRetries = 3;
                int currentRetry = 0;

                while (currentRetry < maxRetries)
                {
                    try
                    {
                        // Fetch EUR prices
                        var responseEur = await httpClient.GetStringAsync(
                            $"https://pro-api.coinmarketcap.com/v1/cryptocurrency/quotes/latest?symbol={symbols}&convert=EUR"
                        );

                        // Fetch USD prices
                        var responseUsd = await httpClient.GetStringAsync(
                            $"https://pro-api.coinmarketcap.com/v1/cryptocurrency/quotes/latest?symbol={symbols}&convert=USD"
                        );

                        // Parse EUR prices
                        using var documentEur = JsonDocument.Parse(responseEur);
                        var dataEur = documentEur.RootElement.GetProperty("data");

                        // Parse USD prices
                        using var documentUsd = JsonDocument.Parse(responseUsd);
                        var dataUsd = documentUsd.RootElement.GetProperty("data");

                        foreach (var symbol in CRYPTO_SYMBOLS)
                        {
                            // Verify the symbol exists in the response before accessing
                            if (!dataEur.TryGetProperty(symbol, out var eurSymbolData) ||
                                !dataUsd.TryGetProperty(symbol, out var usdSymbolData))
                            {
                                Console.WriteLine($"Warning: Symbol {symbol} not found in API response");
                                continue;
                            }

                            try
                            {
                                var eurPrice = eurSymbolData
                                    .GetProperty("quote")
                                    .GetProperty("EUR")
                                    .GetProperty("price")
                                    .GetDecimal();

                                var usdPrice = usdSymbolData
                                    .GetProperty("quote")
                                    .GetProperty("USD")
                                    .GetProperty("price")
                                    .GetDecimal();

                                prices[symbol] = (eurPrice, usdPrice);
                                Console.WriteLine($"Fetched {symbol} prices: {eurPrice:N2} EUR, {usdPrice:N2} USD");
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"Error processing {symbol}: {ex.Message}");
                                continue;
                            }
                        }

                        // If we got here, break the retry loop
                        break;
                    }
                    catch (HttpRequestException ex)
                    {
                        currentRetry++;
                        if (currentRetry == maxRetries)
                        {
                            throw new Exception($"Failed to fetch prices after {maxRetries} attempts: {ex.Message}");
                        }

                        // Exponential backoff
                        var delay = Math.Pow(2, currentRetry) * 1000; // 2, 4, 8 seconds
                        Console.WriteLine($"Request failed, attempting retry {currentRetry} of {maxRetries} in {delay / 1000} seconds...");
                        await Task.Delay((int)delay);
                    }
                }

                // Verify we got at least some prices
                if (prices.Count == 0)
                {
                    throw new Exception("No cryptocurrency prices were successfully fetched");
                }

                return prices;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Critical error in FetchCryptoPrices: {ex.Message}");
                throw; // Rethrow to be handled by the main error handler
            }
        }


        private static async Task<string> FormatUpdateMessage(Dictionary<string, (decimal Eur, decimal Usd)> currentPrices, int updateCount)
        {
            // Calculate price changes first
            var priceChanges = new Dictionary<string, (decimal Price, decimal Change, decimal PercentChange)>();
            foreach (var (symbol, prices) in currentPrices)
            {
                var eurPrice = prices.Eur;
                var lastPrice = _lastPrices.GetValueOrDefault(symbol);
                var change = lastPrice > 0 ? eurPrice - lastPrice : 0;
                var percentChange = lastPrice > 0 ? (change / lastPrice) * 100 : 0;
                priceChanges[symbol] = (eurPrice, change, percentChange);
            }

            // Get AI analysis with the calculated changes
            var aiAnalysis = await GetAIAnalysis(priceChanges);

            // Format message
            var currentTime = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, netherlandsTimeZone);
            var message = new StringBuilder();

            message.AppendLine($"🔄 Crypto Update • {currentTime:HH:mm dd-MM-yyyy}");
            var nextUpdateTime = currentTime.AddMinutes(UPDATE_INTERVAL_MS / 60000);
            message.AppendLine($"⏳ Next update at: {nextUpdateTime:HH:mm}\n");

            // Price updates
            foreach (var (symbol, prices) in currentPrices)
            {
                var emoji = GetCryptoEmoji(symbol);
                var (eurPrice, change, percentChange) = priceChanges[symbol];
                var usdPrice = prices.Usd;

                message.Append($"{emoji} {symbol}: €{eurPrice:N2} | ${usdPrice:N2}");

                if (_lastPrices.ContainsKey(symbol))
                {
                    var changeEmoji = change >= 0 ? "📈" : "📉";
                    var changeSign = change >= 0 ? "+" : "";
                    message.AppendLine($" {changeEmoji} {changeSign}{percentChange:N2}%");
                }
                else
                {
                    message.AppendLine();
                }
            }

            // Update last prices after using them for calculations
            foreach (var (symbol, prices) in currentPrices)
            {
                _lastPrices[symbol] = prices.Eur;
            }

            // Add AI analysis
            message.Append(aiAnalysis);

            return message.ToString();
        }


        private static string GetCryptoEmoji(string symbol) => symbol switch
        {
            "BTC" => "₿",
            "ETH" => "⟠",
            "SOL" => "◎",
            "XRP" => "✖",
            _ => "🪙"
        };

        private static async Task SendStartupMessage(TelegramBotClient botClient)
        {
            var startTime = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, netherlandsTimeZone);
            var message = $"🤖 Bot Started\n" +
                         $"✅ Status: Online\n" +
                         $"🪙 Tracking: {string.Join(", ", CRYPTO_SYMBOLS)}.\n" +
                         $"⏱️ Update Interval: {UPDATE_INTERVAL_MS / 60000} minutes.\n" + /* Divide by 60000 to convert milliseconds to minutes. */
                         $"🕒 Start Time: {startTime:dd-MM-yyyy HH:mm:ss}.\n" +
                         "\nMade with ❤️ by: @brian8544.";

            #if DEBUG
            Console.WriteLine("=== STARTUP MESSAGE ===");
            Console.WriteLine(message);
            Console.WriteLine("=====================");

            await SendTelegramMessage(botClient, message);
            #endif
        }

        private static async Task SendTelegramMessage(TelegramBotClient botClient, string message)
        {
            try
            {
                await botClient.SendTextMessageAsync(
                    chatId: TELEGRAM_CHANNEL,
                    text: message
                );
                #if DEBUG
                Console.WriteLine("\n=== SENT TO TELEGRAM ===");
                Console.WriteLine(message);
                Console.WriteLine("======================\n");
                #endif
            }
            catch (Exception ex)
            {
                Console.WriteLine($"\nERROR SENDING TELEGRAM MESSAGE: \"{ex.Message}\"");
            }
        }

        private static async Task HandleError(TelegramBotClient botClient, Exception ex, DateTime lastSuccessfulUpdate)
        {
            Console.WriteLine($"\n=== ERROR OCCURRED ===");
            Console.WriteLine($"Error: {ex.Message}");
            Console.WriteLine($"Stack trace: {ex.StackTrace}");
            Console.WriteLine("===================\n");

            var errorMessage = $"⚠️ Bot Error Occurred\n" +
                             $"❌ Error: {ex.Message}\n" +
                             $"🔄 Last Successful Update: {lastSuccessfulUpdate:yyyy-MM-dd HH:mm:ss} UTC\n" +
                             $"🕒 Error Time: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC\n" +
                             $"♻️ Attempting to reconnect...";

            await SendTelegramMessage(botClient, errorMessage);
        }
    }
}