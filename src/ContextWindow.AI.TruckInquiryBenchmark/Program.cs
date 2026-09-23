using System.Globalization;
using System.Text.Json;

namespace ContextWindow;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        try
        {
            bool forceLive = args.Contains("--live", StringComparer.Ordinal);
            bool forceMock = args.Contains("--mock", StringComparer.Ordinal);
            if (forceLive && forceMock)
            {
                throw new ArgumentException("Choose --live or --mock, not both.");
            }

            string? sizeValue = Argument(args, "--size");
            int size = sizeValue is null ? 100 : int.Parse(sizeValue, CultureInfo.InvariantCulture);
            IReadOnlyList<Inquiry> cases = Fixtures.Create(size);
            string? limitValue = Argument(args, "--limit");
            if (limitValue is not null)
            {
                int limit = int.Parse(limitValue, CultureInfo.InvariantCulture);
                if (limit is < 3 || limit > size)
                {
                    throw new ArgumentException("--limit must be between 3 and the selected dataset size.");
                }

                cases = cases.Take(limit).ToArray();
                Console.WriteLine($"LIMITED PROBE: {limit}/{size} synthetic inquiries; not the full benchmark.");
            }
            string output = Argument(args, "--out") ?? "artifacts";
            string[] required = ["TYPESAFE_API_KEY", "TYPESAFE_ENDPOINT", "TYPESAFE_MODEL",
                "AZURE_OPENAI_API_KEY", "AZURE_OPENAI_ENDPOINT", "AZURE_OPENAI_DEPLOYMENT"];
            string[] missing = forceMock ? [] : required.Where(name => string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name))).ToArray();

            if (forceLive && missing.Length > 0)
            {
                throw new ArgumentException($"Missing live environment variables: {string.Join(", ", missing)}");
            }

            bool mock = forceMock || missing.Length > 0;
            Dictionary<string, decimal> prices = new()
            {
                ["typesafe"] = -1,
                ["input"] = -1,
                ["cached"] = -1,
                ["output"] = -1
            };
            using HttpClient client = new()
            {
                Timeout = TimeSpan.FromSeconds(30)
            };
            IInquiryProvider typeSafeProvider;
            IInquiryProvider discreteProvider;
            IInquiryProvider probabilityProvider;

            if (mock)
            {
                typeSafeProvider = new MockProvider("TypeSafe", true);
                discreteProvider = new MockProvider("GPT-discrete", false);
                probabilityProvider = new MockProvider("GPT-probability", true);
                Console.WriteLine(forceMock
                    ? "MOCK ONLY: --mock selected. No provider calls or empirical comparison."
                    : $"MOCK ONLY: missing live environment variables ({string.Join(", ", missing)}). No provider calls or empirical comparison.");
            }
            else
            {
                Uri typeSafe = CheckedEndpoint("TYPESAFE_ENDPOINT", "/v1/systemone");
                Uri azure = AzureResponsesEndpoint();
                prices["typesafe"] = OptionalPrice("TYPESAFE_INPUT_USD_PER_MILLION");
                prices["input"] = OptionalPrice("AZURE_OPENAI_INPUT_USD_PER_MILLION");
                prices["cached"] = OptionalPrice("AZURE_OPENAI_CACHED_USD_PER_MILLION");
                prices["output"] = OptionalPrice("AZURE_OPENAI_OUTPUT_USD_PER_MILLION");
                typeSafeProvider = new TypeSafeProvider(client, typeSafe, Environment.GetEnvironmentVariable("TYPESAFE_API_KEY")!,
                    Environment.GetEnvironmentVariable("TYPESAFE_MODEL")!);
                discreteProvider = new AzureResponsesProvider(client, azure, Environment.GetEnvironmentVariable("AZURE_OPENAI_API_KEY")!,
                    Environment.GetEnvironmentVariable("AZURE_OPENAI_DEPLOYMENT")!, false);
                probabilityProvider = new AzureResponsesProvider(client, azure, Environment.GetEnvironmentVariable("AZURE_OPENAI_API_KEY")!,
                    Environment.GetEnvironmentVariable("AZURE_OPENAI_DEPLOYMENT")!, true);
                Console.WriteLine("LIVE: separate practical and probability batches; no response cache in the client.");
            }

            var modes = new (string Name, IInquiryProvider[] Providers)[]
            {
                ("practical", [typeSafeProvider, discreteProvider]),
                ("probability", [typeSafeProvider, probabilityProvider])
            };

            foreach (var mode in modes)
            {
                Console.WriteLine($"{mode.Name} mode:");
                BenchmarkRun run = await Benchmark.RunAsync(cases, mode.Providers, CancellationToken.None,
                    includeWarmup: limitValue is null);
                Trial[] trials = run.Trials.Select(trial => trial with { ComparisonMode = mode.Name }).ToArray();
                IReadOnlyDictionary<string, ProviderMetrics> summary = Benchmark.Summarize(
                    trials, cases, prices, run.MeasuredSeconds, mock, mode.Name);
                string modeOutput = Path.Combine(output, mode.Name);
                await Benchmark.WriteAsync(modeOutput, cases.Concat(Fixtures.CreateTuning()).ToArray(),
                    trials, summary, CancellationToken.None);
                Console.WriteLine($"CW_BENCHMARK_V1 {JsonSerializer.Serialize(new
                {
                    mode = mode.Name,
                    references = cases.Where(item => item.Split == "evaluation").Select(item => new object?[]
                    {
                        item.Id,
                        Enum.GetValues<Dimension>().Select(dimension => EvidenceRating(item.Reference[dimension])).ToArray()
                    }).ToArray(),
                    calls = trials.Where(trial => !trial.Warmup && !trial.Repeat).Select(trial => new object?[]
                    {
                        trial.Provider,
                        trial.CaseId,
                        trial.Status,
                        trial.Milliseconds,
                        trial.Model,
                        trial.Assessment is null ? null : Enum.GetValues<Dimension>()
                            .Select(dimension => EvidenceRating(trial.Assessment[dimension])).ToArray()
                    }).ToArray(),
                    summary
                })}");

                foreach (IGrouping<string, Trial> group in trials.Where(trial => !trial.Warmup && !trial.Repeat)
                    .GroupBy(trial => trial.Provider))
                {
                    Console.WriteLine($"{group.Key} rankings (human review; no dispatch):");

                    foreach (Trial trial in group.Where(trial => trial.Priority?.Score is not null)
                        .OrderByDescending(trial => trial.Priority!.Score).Take(10))
                    {
                        Console.WriteLine($"  {trial.CaseId} | {trial.Priority!.Score:F2} | {trial.Priority.Explanation}");
                    }
                }

                Console.WriteLine($"Wrote fixtures.json, reference-labels.json, aggregate.json, aggregate.csv, requests.json, requests.csv to {modeOutput}.");
            }
            return 0;
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException or JsonException)
        {
            Console.Error.WriteLine(exception.Message);
            return 2;
        }
    }

    private static int?[] EvidenceRating(Rating rating)
    {
        int? level = rating.Level ?? (rating.Probabilities is { } probabilities
            ? Array.IndexOf(probabilities, probabilities.Max()) : null);
        return [(int)rating.Evidence, level];
    }

    private static string? Argument(string[] args, string name)
    {
        int index = Array.IndexOf(args, name);

        if (index < 0)
        {
            return null;
        }

        if (index + 1 >= args.Length)
        {
            throw new ArgumentException($"{name} requires a value.");
        }

        return args[index + 1];
    }

    private static Uri CheckedEndpoint(string name, string suffix)
    {
        string value = Environment.GetEnvironmentVariable(name)!;

        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) || uri.Scheme != Uri.UriSchemeHttps
            || uri.Query.Length != 0 || uri.Fragment.Length != 0 || !uri.AbsolutePath.EndsWith(suffix, StringComparison.Ordinal))
        {
            throw new ArgumentException($"{name} must be a complete HTTPS URL ending in {suffix}.");
        }

        return uri;
    }

    private static Uri AzureResponsesEndpoint()
    {
        const string name = "AZURE_OPENAI_ENDPOINT";
        string value = Environment.GetEnvironmentVariable(name)!;

        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) || uri.Scheme != Uri.UriSchemeHttps
            || uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.AbsolutePath != "/")
        {
            throw new ArgumentException($"{name} must be a base HTTPS URL without a path, query or fragment.");
        }

        return new Uri(uri, "/openai/v1/responses");
    }

    private static decimal OptionalPrice(string name)
    {
        return string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name)) ? -1 : Price(name);
    }

    private static decimal Price(string name)
    {
        if (!decimal.TryParse(Environment.GetEnvironmentVariable(name), NumberStyles.Number,
            CultureInfo.InvariantCulture, out decimal value) || value < 0)
        {
            throw new ArgumentException($"{name} must be a nonnegative USD amount using invariant decimal notation.");
        }

        return value;
    }
}

public sealed class MockProvider(string name, bool probabilityMode) : IInquiryProvider
{
    public string Name { get; } = name;

    public Task<ProviderReply> AssessAsync(Inquiry inquiry, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Assessment assessment = probabilityMode
            ? new(inquiry.Reference.Ratings.ToDictionary(pair => pair.Key, pair => pair.Value.Level is int level
                ? new Rating(null, pair.Value.Evidence, Enumerable.Range(0, 3)
                    .Select(index => index == level ? 1.0 : 0.0).ToArray()) : pair.Value))
            : inquiry.Reference;
        return Task.FromResult(new ProviderReply(assessment, null, null, null, null));
    }
}
