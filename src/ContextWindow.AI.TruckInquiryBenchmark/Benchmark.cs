using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ContextWindow;

public sealed record Trial(string Provider, string CaseId, string Split, bool Warmup, bool Repeat,
    string Status, double Milliseconds, int Retries, int? InputTokens, int? OutputTokens, int? CachedTokens,
    Assessment? Assessment, PriorityResult? Priority)
{
    public string? Model
    {
        get; init;
    }
    public string? ComparisonMode
    {
        get; init;
    }
    public string RatingMode => Provider == "GPT-discrete" ? "practical-discrete" : "probability";
}

public sealed record CalibrationBucket(int Correct, int Total, double? Accuracy);

public sealed record DimensionMetrics(int Total, int Accurate, int MissingCorrect, int MissingTotal,
    int ContradictoryCorrect, int ContradictoryTotal, double? Accuracy, double? Brier,
    CalibrationBucket? HighProbability, CalibrationBucket? LowProbability);

public sealed record TokenMetrics([property: JsonPropertyName("input")] long Input,
    [property: JsonPropertyName("output")] long Output, [property: JsonPropertyName("cached")] long Cached);

public sealed record RankingVariation(int ComparedSets, int ChangedSets, double? Rate);

public sealed record ProviderMetrics
{
    [JsonPropertyName("sample")]
    public required string Sample
    {
        get; init;
    }

    [JsonPropertyName("ratingMode")]
    public required string RatingMode
    {
        get; init;
    }

    [JsonPropertyName("requests")]
    public int Requests
    {
        get; init;
    }

    [JsonPropertyName("errors")]
    public int Errors
    {
        get; init;
    }

    [JsonPropertyName("invalid")]
    public int Invalid
    {
        get; init;
    }

    [JsonPropertyName("timeouts")]
    public int Timeouts
    {
        get; init;
    }

    [JsonPropertyName("retries")]
    public int Retries
    {
        get; init;
    }

    [JsonPropertyName("latencyMedianMs")]
    public double? LatencyMedianMs
    {
        get; init;
    }

    [JsonPropertyName("latencyP95Ms")]
    public double? LatencyP95Ms
    {
        get; init;
    }

    [JsonPropertyName("batchDurationSeconds")]
    public double BatchDurationSeconds
    {
        get; init;
    }

    [JsonPropertyName("completedInquiriesPerSecond")]
    public double CompletedInquiriesPerSecond
    {
        get; init;
    }

    [JsonPropertyName("tokens")]
    public required TokenMetrics Tokens
    {
        get; init;
    }

    [JsonPropertyName("estimatedCostPer1000")]
    public decimal? EstimatedCostPer1000
    {
        get; init;
    }

    [JsonPropertyName("dimensions")]
    public required IReadOnlyDictionary<string, DimensionMetrics> Dimensions
    {
        get; init;
    }

    [JsonPropertyName("topAgreement")]
    public bool? TopAgreement
    {
        get; init;
    }

    [JsonPropertyName("providerTopInquiryId")]
    public string? ProviderTopInquiryId
    {
        get; init;
    }

    [JsonPropertyName("independentReferenceTopInquiryId")]
    public string? IndependentReferenceTopInquiryId
    {
        get; init;
    }

    [JsonPropertyName("providerTopAgreement")]
    public bool? ProviderTopAgreement
    {
        get; init;
    }

    [JsonPropertyName("top10Overlap")]
    public int? Top10Overlap
    {
        get; init;
    }

    [JsonPropertyName("highPriorityDownrank")]
    public bool? HighPriorityDownrank
    {
        get; init;
    }

    [JsonPropertyName("repeatability")]
    public double? Repeatability
    {
        get; init;
    }

    [JsonPropertyName("rankingVariation")]
    public required RankingVariation RankingVariation
    {
        get; init;
    }
}

public sealed record BenchmarkRun(IReadOnlyList<Trial> Trials, double MeasuredSeconds);

public static class Benchmark
{
    public static async Task<BenchmarkRun> RunAsync(IReadOnlyList<Inquiry> cases,
        IReadOnlyList<IInquiryProvider> providers, CancellationToken cancellationToken, bool includeWarmup = true)
    {
        List<Trial> results = new();

        if (includeWarmup)
        {
            foreach (IInquiryProvider provider in providers)
            {
                foreach (Inquiry warmup in Fixtures.CreateTuning())
                {
                    results.Add(await MeasureAsync(provider, warmup, true, false, cancellationToken));
                }
            }
        }

        SemaphoreSlim[] limits = providers.Select(_ => new SemaphoreSlim(2)).ToArray();
        List<Task<Trial>> pending = new();
        Stopwatch measured = Stopwatch.StartNew();

        try
        {
            Inquiry[] evaluation = cases.Where(item => item.Split == "evaluation").ToArray();

            for (int index = 0; index < evaluation.Length; index++)
            {
                Inquiry inquiry = evaluation[index];

                for (int offset = 0; offset < providers.Count; offset++)
                {
                    int providerIndex = (index + offset) % providers.Count;
                    pending.Add(ScopedAsync(providers[providerIndex], inquiry, limits[providerIndex], cancellationToken));
                }
            }

            results.AddRange(await Task.WhenAll(pending));
            measured.Stop();
        }
        finally
        {
            foreach (SemaphoreSlim limit in limits)
            {
                limit.Dispose();
            }
        }

        foreach (Inquiry inquiry in cases.Where(item => item.Split == "evaluation").Take(5))
        {
            foreach (IInquiryProvider provider in providers)
            {
                for (int repeat = 0; repeat < 2; repeat++)
                {
                    results.Add(await MeasureAsync(provider, inquiry, false, true, cancellationToken));
                }
            }
        }

        return new(results, measured.Elapsed.TotalSeconds);
    }

    private static async Task<Trial> ScopedAsync(IInquiryProvider provider, Inquiry inquiry,
        SemaphoreSlim limit, CancellationToken cancellationToken)
    {
        await limit.WaitAsync(cancellationToken);

        try
        {
            return await MeasureAsync(provider, inquiry, false, false, cancellationToken);
        }
        finally
        {
            limit.Release();
        }
    }

    private static async Task<Trial> MeasureAsync(IInquiryProvider provider, Inquiry inquiry,
        bool warmup, bool repeat, CancellationToken cancellationToken)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        int retries = 0;

        while (true)
        {
            try
            {
                ProviderReply reply = await provider.AssessAsync(inquiry, cancellationToken);
                PriorityResult priority = Priority.Rank(inquiry, reply.Assessment, new PriorityWeights());
                stopwatch.Stop();
                return new(provider.Name, inquiry.Id, inquiry.Split, warmup, repeat, "ok",
                    stopwatch.Elapsed.TotalMilliseconds, retries, reply.InputTokens, reply.OutputTokens,
                    reply.CachedTokens, reply.Assessment, priority)
                {
                    Model = reply.Model
                };
            }
            catch (ProviderFailureException exception) when (exception.Kind is "retryable_http" or "timeout" && retries < 2)
            {
                retries++;
                await Task.Delay(TimeSpan.FromMilliseconds(250 * retries), cancellationToken);
            }
            catch (ProviderFailureException exception)
            {
                stopwatch.Stop();
                return new(provider.Name, inquiry.Id, inquiry.Split, warmup, repeat,
                    exception.Kind == "retryable_http" ? "api_error" : exception.Kind,
                    stopwatch.Elapsed.TotalMilliseconds, retries, null, null, null, null, null);
            }
        }
    }

    public static IReadOnlyDictionary<string, ProviderMetrics> Summarize(IReadOnlyList<Trial> trials, IReadOnlyList<Inquiry> cases,
        IReadOnlyDictionary<string, decimal> prices, double measuredSeconds, bool mock, string? comparisonMode = null)
    {
        Dictionary<string, ProviderMetrics> summaries = new();

        foreach (IGrouping<string, Trial> group in trials.Where(trial => !trial.Warmup && !trial.Repeat)
            .GroupBy(trial => trial.Provider))
        {
            Trial[] measured = group.Where(trial => trial.Split == "evaluation").ToArray();
            Trial[] evaluationCalls = measured;
            Trial[] evaluated = measured.Where(trial => trial.Status == "ok").ToArray();
            Dictionary<string, DimensionMetrics> dimensions = new();

            foreach (Dimension dimension in Enum.GetValues<Dimension>())
            {
                var paired = evaluated.Select(trial => (
                    Prediction: trial.Assessment![dimension],
                    Truth: cases.Single(item => item.Id == trial.CaseId).Reference[dimension])).ToArray();
                int accurate = paired.Count(pair => IsAccurate(pair.Prediction, pair.Truth));
                double[] briers = paired.Where(pair => pair.Truth.Level is not null
                    && ValidDistribution(pair.Prediction.Probabilities))
                    .Select(pair => pair.Prediction.Probabilities!.Select((probability, level) =>
                        Math.Pow(probability - (level == pair.Truth.Level ? 1 : 0), 2)).Sum() / 3).ToArray();
                var calibrated = paired.Where(pair => pair.Truth.Evidence == Sufficiency.Sufficient
                    && pair.Truth.Level is not null && pair.Prediction.Evidence == Sufficiency.Sufficient
                    && ValidDistribution(pair.Prediction.Probabilities)).ToArray();
                var high = calibrated.Where(pair => pair.Prediction.Probabilities!.Max() >= 0.8).ToArray();
                var low = calibrated.Where(pair => pair.Prediction.Probabilities!.Max() < 0.8).ToArray();
                dimensions[dimension.ToString()] = new(evaluationCalls.Length, accurate,
                    paired.Count(pair => pair.Truth.Evidence == Sufficiency.Missing && pair.Prediction.Evidence == Sufficiency.Missing),
                    evaluationCalls.Count(trial => cases.Single(item => item.Id == trial.CaseId).Reference[dimension].Evidence == Sufficiency.Missing),
                    paired.Count(pair => pair.Truth.Evidence == Sufficiency.Contradictory && pair.Prediction.Evidence == Sufficiency.Contradictory),
                    evaluationCalls.Count(trial => cases.Single(item => item.Id == trial.CaseId).Reference[dimension].Evidence == Sufficiency.Contradictory),
                    evaluationCalls.Length == 0 ? null : (double)accurate / evaluationCalls.Length,
                    briers.Length == 0 ? null : briers.Average(),
                    measured[0].RatingMode == "probability" ? Bucket(high) : null,
                    measured[0].RatingMode == "probability" ? Bucket(low) : null);
            }

            var independent = cases.Where(item => item.Split == "evaluation" && item.IndependentReference).ToArray();
            string[] referenceTop = independent.Select(item => Priority.Rank(item, item.Reference, new PriorityWeights()))
                .Where(result => result.Score is not null).OrderByDescending(result => result.Score)
                .ThenBy(result => result.InquiryId, StringComparer.Ordinal).Take(10).Select(result => result.InquiryId).ToArray();
            string[] predictedTop = evaluated.Where(trial => independent.Any(item => item.Id == trial.CaseId))
                .Where(trial => trial.Priority?.Score is not null).OrderByDescending(trial => trial.Priority!.Score)
                .ThenBy(trial => trial.CaseId, StringComparer.Ordinal).Take(10).Select(trial => trial.CaseId).ToArray();
            string? providerTop = RankOrder(evaluated).FirstOrDefault();
            Trial[] repeated = trials.Where(trial => trial.Provider == group.Key && trial.Repeat && trial.Status == "ok").ToArray();
            int repeatPairs = repeated.GroupBy(trial => trial.CaseId).Count(bucket => bucket.Count() == 2);
            int matchingPairs = repeated.GroupBy(trial => trial.CaseId).Count(bucket => bucket.Count() == 2
                && measured.Any(baseTrial => baseTrial.CaseId == bucket.Key && baseTrial.Status == "ok"
                    && bucket.All(repeat => JsonSerializer.Serialize(repeat.Assessment)
                        == JsonSerializer.Serialize(baseTrial.Assessment))));
            string[] repeatedIds = cases.Where(item => item.Split == "evaluation").Take(5)
                .Select(item => item.Id).ToArray();
            Trial[] baseSet = measured.Where(trial => repeatedIds.Contains(trial.CaseId)).ToArray();
            var repeatSets = repeatedIds.Select(id => repeated.Where(trial => trial.CaseId == id).ToArray()).ToArray();
            int comparedSets = 0;
            int changedSets = 0;

            if (repeatedIds.Length == 5 && baseSet.Length == 5 && baseSet.All(trial => trial.Status == "ok")
                && repeatSets.All(set => set.Length == 2) && repeatSets.SelectMany(set => set)
                    .All(trial => trial.Status == "ok") && baseSet.Any(trial => trial.Priority?.Score is not null))
            {
                string[] baseOrder = RankOrder(baseSet);

                for (int index = 0; index < 2; index++)
                {
                    string[] repeatOrder = RankOrder(repeatSets.Select(set => set[index]));
                    comparedSets++;
                    if (!baseOrder.SequenceEqual(repeatOrder))
                    {
                        changedSets++;
                    }
                }
            }

            double[] latencies = measured.Select(trial => trial.Milliseconds).Order().ToArray();
            long input = measured.Sum(trial => trial.InputTokens ?? 0);
            long output = measured.Sum(trial => trial.OutputTokens ?? 0);
            long cached = measured.Sum(trial => trial.CachedTokens ?? 0);
            decimal? cost = mock ? null : group.Key == "TypeSafe"
                ? prices["typesafe"] >= 0 && measured.All(trial => trial.InputTokens is not null)
                    ? input * prices["typesafe"] / 1_000_000m
                    : null
                : prices["input"] >= 0 && prices["cached"] >= 0 && prices["output"] >= 0
                    && measured.All(trial => trial.InputTokens is not null && trial.OutputTokens is not null)
                    ? ((input - cached) * prices["input"] + cached * prices["cached"] + output * prices["output"]) / 1_000_000m
                    : null;

            summaries[group.Key] = new ProviderMetrics
            {
                Sample = mock ? "MOCK - no model comparison" : "LIVE",
                RatingMode = group.Key == "TypeSafe" && comparisonMode is not null
                    ? $"{comparisonMode}-native" : measured[0].RatingMode,
                Requests = measured.Length,
                Errors = measured.Count(trial => trial.Status == "api_error"),
                Invalid = measured.Count(trial => trial.Status == "invalid"),
                Timeouts = measured.Count(trial => trial.Status == "timeout"),
                Retries = measured.Sum(trial => trial.Retries),
                LatencyMedianMs = Percentile(latencies, 0.50),
                LatencyP95Ms = Percentile(latencies, 0.95),
                BatchDurationSeconds = measuredSeconds,
                CompletedInquiriesPerSecond = measuredSeconds > 0
                    ? evaluated.Length / measuredSeconds : 0,
                Tokens = new(input, output, cached),
                EstimatedCostPer1000 = cost is null ? null : cost * 1000 / measured.Length,
                Dimensions = dimensions,
                ProviderTopInquiryId = providerTop,
                IndependentReferenceTopInquiryId = referenceTop.FirstOrDefault(),
                TopAgreement = referenceTop.Length == 0 || predictedTop.Length == 0
                    ? (bool?)null : predictedTop[0] == referenceTop[0],
                Top10Overlap = referenceTop.Length < 10 || predictedTop.Length < 10
                    ? (int?)null : referenceTop.Intersect(predictedTop).Count(),
                HighPriorityDownrank = referenceTop.Length == 0 ? (bool?)null
                    : !predictedTop.Take(10).Contains(referenceTop[0]),
                Repeatability = repeatPairs == 0 ? (double?)null : (double)matchingPairs / repeatPairs,
                RankingVariation = new(comparedSets, changedSets,
                    comparedSets == 0 ? null : (double)changedSets / comparedSets)
            };
        }

        string[] providerTops = summaries.Values.Select(summary => summary.ProviderTopInquiryId)
            .OfType<string>().ToArray();
        if (summaries.Count >= 2 && providerTops.Length == summaries.Count)
        {
            bool agreement = providerTops.Distinct(StringComparer.Ordinal).Count() == 1;
            foreach (string provider in summaries.Keys.ToArray())
            {
                summaries[provider] = summaries[provider] with
                {
                    ProviderTopAgreement = agreement
                };
            }
        }

        return summaries;
    }

    private static double? Percentile(double[] sorted, double fraction)
    {
        return sorted.Length == 0 ? null : sorted[(int)Math.Ceiling(fraction * sorted.Length) - 1];
    }

    private static string[] RankOrder(IEnumerable<Trial> trials)
    {
        return trials.Where(trial => trial.Priority?.Score is not null)
            .OrderByDescending(trial => trial.Priority!.Score)
            .ThenBy(trial => trial.CaseId, StringComparer.Ordinal)
            .Select(trial => trial.CaseId).ToArray();
    }

    private static bool IsAccurate(Rating prediction, Rating truth)
    {
        if (prediction.Evidence != truth.Evidence)
        {
            return false;
        }

        if (truth.Evidence != Sufficiency.Sufficient || prediction.Level == truth.Level)
        {
            return true;
        }

        var probabilities = prediction.Probabilities;
        return probabilities is not null && Array.IndexOf(probabilities, probabilities.Max()) == truth.Level;
    }

    private static bool ValidDistribution(double[]? probabilities)
    {
        return probabilities is { Length: 3 }
            && probabilities.All(value => double.IsFinite(value) && value >= 0 && value <= 1)
            && Math.Abs(probabilities.Sum() - 1) <= 0.0001;
    }

    private static CalibrationBucket Bucket((Rating Prediction, Rating Truth)[] pairs)
    {
        int correct = pairs.Count(pair => Array.IndexOf(pair.Prediction.Probabilities!,
            pair.Prediction.Probabilities!.Max()) == pair.Truth.Level);
        return new(correct, pairs.Length, pairs.Length == 0 ? null : (double)correct / pairs.Length);
    }

    public static async Task WriteAsync(string directory, IReadOnlyList<Inquiry> cases, IReadOnlyList<Trial> trials,
        IReadOnlyDictionary<string, ProviderMetrics> summary,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(directory);
        JsonSerializerOptions options = new()
        {
            WriteIndented = true
        };
        options.Converters.Add(new JsonStringEnumConverter());
        var fixtures = cases.Select(inquiry => new
        {
            inquiry.Id,
            inquiry.Message,
            inquiry.Split,
            inquiry.Context,
            inquiry.Offers,
            inquiry.Blocked,
            inquiry.Review
        });
        var referenceLabels = cases.Where(inquiry => inquiry.IndependentReference).Select(inquiry =>
        {
            PriorityResult ranking = Priority.Rank(inquiry, inquiry.Reference, new PriorityWeights());
            return new
            {
                inquiry.Id,
                inquiry.Reference.Ratings,
                ReferenceRanking = new
                {
                    Eligible = ranking.Score is not null,
                    ranking.Status,
                    ranking.Truck,
                    ranking.Score
                }
            };
        });
        await File.WriteAllTextAsync(Path.Combine(directory, "fixtures.json"),
            JsonSerializer.Serialize(fixtures, options), cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(directory, "reference-labels.json"),
            JsonSerializer.Serialize(referenceLabels, options), cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(directory, "aggregate.json"),
            JsonSerializer.Serialize(summary, options), cancellationToken);
        StringBuilder aggregate = new();
        AppendCsvRow(aggregate, "provider", "sample", "ratingMode", "dimension", "total", "accurate", "accuracy",
            "missingCorrect", "missingTotal", "contradictoryCorrect", "contradictoryTotal", "brier",
            "highProbabilityCorrect", "highProbabilityTotal", "highProbabilityAccuracy",
            "lowProbabilityCorrect", "lowProbabilityTotal", "lowProbabilityAccuracy",
            "providerTopInquiryId", "independentReferenceTopInquiryId", "topAgreement", "providerTopAgreement",
            "top10Overlap", "highPriorityDownrank", "repeatability",
            "rankingComparedSets", "rankingChangedSets", "rankingVariationRate", "requests", "errors", "invalid",
            "retries", "timeouts", "latencyMedianMs", "latencyP95Ms", "batchDurationSeconds",
            "completedInquiriesPerSecond",
            "inputTokens", "outputTokens", "cachedTokens", "estimatedCostPer1000");

        foreach ((string provider, ProviderMetrics metrics) in summary)
        {
            foreach ((string dimension, DimensionMetrics values) in metrics.Dimensions)
            {
                AppendCsvRow(aggregate, provider, metrics.Sample, metrics.RatingMode, dimension,
                    Number(values.Total), Number(values.Accurate), Number(values.Accuracy),
                    Number(values.MissingCorrect), Number(values.MissingTotal),
                    Number(values.ContradictoryCorrect), Number(values.ContradictoryTotal), Number(values.Brier),
                    Number(values.HighProbability?.Correct), Number(values.HighProbability?.Total),
                    Number(values.HighProbability?.Accuracy), Number(values.LowProbability?.Correct),
                    Number(values.LowProbability?.Total), Number(values.LowProbability?.Accuracy),
                    metrics.ProviderTopInquiryId, metrics.IndependentReferenceTopInquiryId,
                    metrics.TopAgreement?.ToString(), metrics.ProviderTopAgreement?.ToString(),
                    Number(metrics.Top10Overlap),
                    metrics.HighPriorityDownrank?.ToString(), Number(metrics.Repeatability),
                    Number(metrics.RankingVariation.ComparedSets), Number(metrics.RankingVariation.ChangedSets),
                    Number(metrics.RankingVariation.Rate), Number(metrics.Requests), Number(metrics.Errors),
                    Number(metrics.Invalid), Number(metrics.Retries), Number(metrics.Timeouts),
                    Number(metrics.LatencyMedianMs), Number(metrics.LatencyP95Ms),
                    Number(metrics.BatchDurationSeconds), Number(metrics.CompletedInquiriesPerSecond),
                    Number(metrics.Tokens.Input),
                    Number(metrics.Tokens.Output), Number(metrics.Tokens.Cached), Number(metrics.EstimatedCostPer1000));
            }
        }

        await File.WriteAllTextAsync(Path.Combine(directory, "aggregate.csv"), aggregate.ToString(), cancellationToken);
        StringBuilder csv = new("provider,caseId,split,warmup,repeat,status,latencyMs,retries,inputTokens,outputTokens,cachedTokens,priorityStatus,truck,score\n");

        foreach (Trial trial in trials)
        {
            string[] fields = [trial.Provider, trial.CaseId, trial.Split, trial.Warmup.ToString(), trial.Repeat.ToString(),
                trial.Status, trial.Milliseconds.ToString("F2", CultureInfo.InvariantCulture),
                Number(trial.Retries), Number(trial.InputTokens), Number(trial.OutputTokens), Number(trial.CachedTokens),
                trial.Priority?.Status ?? "", trial.Priority?.Truck ?? "",
                Number(trial.Priority?.Score)];
            AppendCsvRow(csv, fields);
        }

        await File.WriteAllTextAsync(Path.Combine(directory, "requests.csv"), csv.ToString(), cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(directory, "requests.json"),
            JsonSerializer.Serialize(trials, options), cancellationToken);
    }

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);
    private static string Number(int? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "";
    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);
    private static string Number(double? value) => value?.ToString("R", CultureInfo.InvariantCulture) ?? "";
    private static string Number(decimal? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "";

    private static void AppendCsvRow(StringBuilder csv, params string?[] fields)
    {
        csv.AppendJoin(',', fields.Select(field =>
        {
            string value = field ?? "";
            return value.IndexOfAny([',', '"', '\r', '\n']) < 0 ? value : $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
        }));
        csv.Append('\n');
    }
}
