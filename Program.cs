using Azure;
using Azure.AI.DocumentIntelligence;
using Azure.Core;
using Azure.Core.Diagnostics;
using Microsoft.Extensions.Configuration;
using System.Diagnostics.Tracing;
using System.Text.Json;

namespace InvoiceDataExtractor
{
    class Program
    {
        static async Task Main(string[] args)
        {
            // 1. Setup logging for diagnostics (optional but recommended)
            using var listener = new AzureEventSourceListener(
                (e, level) => Console.WriteLine($"{e.EventSource.Name} [{level}]: {e.Message}"),
                EventLevel.Informational);

            // 2. Load configuration
            var config = new ConfigurationBuilder()
                .SetBasePath(AppContext.BaseDirectory)
                .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
                .AddJsonFile("appsettings.Development.json", optional: true, reloadOnChange: true)
                .Build();

            var endpoint = config["DocumentIntelligence:Endpoint"];
            var key = config["DocumentIntelligence:Key"];
            if (string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(key))
            {
                Console.Error.WriteLine("Document Intelligence endpoint or key is missing in configuration.");
                return;
            }
            var credential = new AzureKeyCredential(key);

            // 3. Create client with retry policy
            var client = new DocumentIntelligenceClient(
                new Uri(endpoint),
                credential,
                new DocumentIntelligenceClientOptions()
                {
                    Retry =
                    {
                        Mode = RetryMode.Exponential,
                        Delay = TimeSpan.FromSeconds(1),
                        MaxDelay = TimeSpan.FromSeconds(10),
                        MaxRetries = 4
                    }
                });

            try
            {
                // 4. Read invoice PDF
                string filePath = args.Length > 0 ? args[0] : "C:\\Dev\\documents\\coyote-1line.pdf";
                await using var stream = File.OpenRead(filePath);

                // 5. Analyze with prebuilt invoice model
                var content = BinaryData.FromStream(stream);

                Operation<AnalyzeResult> operation =
                    await client.AnalyzeDocumentAsync(WaitUntil.Completed, "prebuilt-invoice", content);

                AnalyzeResult result = operation.Value;

                string json = JsonSerializer.Serialize(
                    result.Documents.First().Fields,
                    new JsonSerializerOptions { WriteIndented = true, DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull }
                );
                File.WriteAllText($@"invoice_analysis_result_{DateTime.Now.Ticks}.json", json);
                Console.WriteLine("Analysis Result JSON:");
                Console.WriteLine(json);

                // 6. Process results
                Console.WriteLine($"Invoice analysis completed. Model ID: {result.ModelId}");
                foreach (var page in result.Pages)
                {
                    Console.WriteLine($"Page {page.PageNumber}: {page.Width}×{page.Height} {page.Unit}");
                }

                foreach (var kv in result.KeyValuePairs)
                {
                    Console.WriteLine($"  Field '{kv.Key.Content}' = '{kv.Value?.Content}'");
                }
            }
            catch (RequestFailedException ex) when (ex.Status >= 500)
            {
                Console.Error.WriteLine($"Service error (will retry): {ex.Message}");
            }
            catch (RequestFailedException ex)
            {
                Console.Error.WriteLine($"Request failed: {ex.Message}");
            }
            catch (IOException ex)
            {
                Console.Error.WriteLine($"File error: {ex.Message}");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Unexpected error: {ex}");
            }
        }
    }
}