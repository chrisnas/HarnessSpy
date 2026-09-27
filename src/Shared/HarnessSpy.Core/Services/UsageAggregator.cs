using HarnessSpy.Core.Models;

namespace HarnessSpy.Core.Services;

// Aggregates provider-reported usage without adding cumulative checkpoints to
// per-request deltas. Callers supply the native turn/session bucket because a
// UsageMeasurement deliberately carries scope, not a provider-specific turn id.
public sealed class UsageAggregator
{
    public long? Aggregate(
        IEnumerable<UsageSample> samples,
        Func<string, bool> namePredicate)
    {
        UsageSample[] accepted = Deduplicate(samples)
            .Where(sample => namePredicate(sample.Measurement.Name))
            .ToArray();
        if (accepted.Length == 0)
        {
            return null;
        }

        UsageSample[] sessionSamples = accepted
            .Where(sample => sample.Measurement.Scope == UsageScope.Session)
            .ToArray();
        if (sessionSamples.Length > 0)
        {
            if (sessionSamples.Any(sample => !IsModelMetric(sample.Measurement.Name)))
            {
                sessionSamples = sessionSamples
                    .Where(sample => !IsModelMetric(sample.Measurement.Name))
                    .ToArray();
            }
            else
            {
                sessionSamples = RemoveMirroredAgentModelMetrics(sessionSamples);
            }

            return AggregateBuckets(sessionSamples);
        }

        UsageSample[] turnSamples = accepted
            .Where(sample => sample.Measurement.Scope == UsageScope.Turn)
            .ToArray();
        if (turnSamples.Length > 0)
        {
            return AggregateBuckets(turnSamples);
        }

        return AggregateBuckets(accepted);
    }

    public IReadOnlyList<AggregatedUsageValue> AggregateByName(
        IEnumerable<UsageSample> samples)
    {
        UsageSample[] materialized = Deduplicate(samples).ToArray();
        return materialized
            .GroupBy(
                sample => new UsageMetricKey(
                    sample.Measurement.Name,
                    sample.Measurement.Unit),
                UsageMetricKeyComparer.Instance)
            .Select(group => new AggregatedUsageValue(
                group.Key.Name,
                Aggregate(group, static _ => true) ?? 0,
                group.Key.Unit))
            .Where(value => value.Value != 0)
            .OrderBy(value => value.Unit, StringComparer.OrdinalIgnoreCase)
            .ThenBy(value => value.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IEnumerable<UsageSample> Deduplicate(IEnumerable<UsageSample> samples)
    {
        return samples.DistinctBy(
            sample => new UsageIdentity(
                sample.Measurement.SourceRecordId,
                sample.Measurement.Name,
                sample.Measurement.Value,
                sample.Measurement.Unit,
                sample.Measurement.Scope,
                sample.Measurement.Behavior,
                sample.BucketId),
            UsageIdentityComparer.Instance);
    }

    private static long AggregateBuckets(IEnumerable<UsageSample> samples)
    {
        return samples
            .GroupBy(
                sample => MetricIdentity(sample.Measurement.Name),
                StringComparer.OrdinalIgnoreCase)
            .Sum(metric => metric
                .GroupBy(BucketKey, StringComparer.Ordinal)
                .Sum(AggregateBucket));
    }

    private static string BucketKey(UsageSample sample)
    {
        return sample.Measurement.Scope switch
        {
            UsageScope.Session => $"session:{sample.BucketId}",
            UsageScope.Turn => $"turn:{sample.BucketId}",
            _ => $"request:{sample.Measurement.SourceRecordId}"
        };
    }

    private static long AggregateBucket(IEnumerable<UsageSample> samples)
    {
        UsageSample[] bucket = samples
            .DistinctBy(sample => new
            {
                sample.Measurement.SourceRecordId,
                sample.Measurement.Value,
                sample.Measurement.Scope,
                sample.Measurement.Behavior,
                sample.BucketId,
                sample.IsAuthoritative
            })
            .ToArray();
        if (bucket.Any(static sample => sample.IsAuthoritative))
        {
            bucket = bucket
                .Where(static sample => sample.IsAuthoritative)
                .ToArray();
        }

        UsageSample? final = bucket
            .Where(sample => sample.Measurement.Behavior == UsageBehavior.FinalSnapshot)
            .OrderBy(sample => sample.Timestamp)
            .ThenBy(sample => SnapshotPreference(sample.Measurement.Name))
            .LastOrDefault();
        if (final is not null)
        {
            return final.Measurement.Value;
        }

        long? cumulative = bucket
            .Where(sample => sample.Measurement.Behavior == UsageBehavior.CumulativeSnapshot)
            .Select(sample => (long?)sample.Measurement.Value)
            .Max();
        if (cumulative is not null)
        {
            return cumulative.Value;
        }

        return bucket
            .Where(sample => sample.Measurement.Behavior is
                UsageBehavior.Delta or UsageBehavior.Unknown)
            .Sum(sample => sample.Measurement.Value);
    }

    private static string MetricIdentity(string name)
    {
        string family = MetricFamily(name);
        string[] parts = name.Split(
            '.',
            StringSplitOptions.RemoveEmptyEntries |
            StringSplitOptions.TrimEntries);
        if (!IsModelMetric(name))
        {
            return family;
        }

        return $"{ModelMetricOwner(parts)}:{family}";
    }

    private static string MetricFamily(string name)
    {
        string[] parts = name.Split(
            '.',
            StringSplitOptions.RemoveEmptyEntries |
            StringSplitOptions.TrimEntries);
        string leaf = parts.Length == 0 ? name : parts[^1];
        string family = new(
            leaf.Where(char.IsLetterOrDigit)
                .Select(char.ToLowerInvariant)
                .ToArray());
        family = family switch
        {
            "input" or "inputtoken" or "inputtokens" => "inputtokens",
            "output" or "outputtoken" or "outputtokens" => "outputtokens",
            "cacheread" or "cachereadtoken" or "cachereadtokens" or
                "cachereadinputtokens" => "cachereadtokens",
            "cachewrite" or "cachewritetoken" or "cachewritetokens" or
                "cachecreationinputtokens" => "cachewritetokens",
            "reasoning" or "reasoningtoken" or "reasoningtokens" or
                "thinkingtoken" or "thinkingtokens" => "reasoningtokens",
            _ => family
        };
        return family;
    }

    private static bool IsModelMetric(string name)
    {
        string[] parts = name.Split(
            '.',
            StringSplitOptions.RemoveEmptyEntries |
            StringSplitOptions.TrimEntries);
        if (parts.Length < 2)
        {
            return false;
        }

        return parts.Any(static part =>
                   part.Equals(
                       "modelMetrics",
                       StringComparison.OrdinalIgnoreCase)) ||
            parts[0].Equals("agentMetrics", StringComparison.OrdinalIgnoreCase) ||
            (!parts[0].Equals("usage", StringComparison.OrdinalIgnoreCase) &&
             !parts[0].Equals("tokenDetails", StringComparison.OrdinalIgnoreCase));
    }

    private static string ModelMetricOwner(IReadOnlyList<string> parts)
    {
        for (int index = 0; index + 1 < parts.Count; index++)
        {
            if (parts[index].Equals(
                    "modelMetrics",
                    StringComparison.OrdinalIgnoreCase))
            {
                if (index >= 2 &&
                    parts[0].Equals(
                        "agentMetrics",
                        StringComparison.OrdinalIgnoreCase))
                {
                    return $"agent:{parts[1]}:model:{parts[index + 1]}";
                }

                return $"model:{parts[index + 1]}";
            }
        }

        if (parts.Count >= 2 &&
            parts[0].Equals(
                "agentMetrics",
                StringComparison.OrdinalIgnoreCase))
        {
            return $"agent:{parts[1]}";
        }

        return $"model:{parts[0]}";
    }

    private static UsageSample[] RemoveMirroredAgentModelMetrics(
        IReadOnlyList<UsageSample> samples)
    {
        HashSet<string> topLevelMetrics = samples
            .Select(static sample => sample.Measurement.Name)
            .Where(static name => name.StartsWith(
                "modelMetrics.",
                StringComparison.OrdinalIgnoreCase))
            .Select(MirroredModelMetricIdentity)
            .Where(static identity => identity is not null)
            .Cast<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (topLevelMetrics.Count == 0)
        {
            return samples.ToArray();
        }

        return samples
            .Where(sample =>
            {
                string name = sample.Measurement.Name;
                if (!name.StartsWith(
                        "agentMetrics.",
                        StringComparison.OrdinalIgnoreCase) ||
                    !name.Contains(
                        ".modelMetrics.",
                        StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                string? identity = MirroredModelMetricIdentity(name);
                return identity is null || !topLevelMetrics.Contains(identity);
            })
            .ToArray();
    }

    private static string? MirroredModelMetricIdentity(string name)
    {
        string[] parts = name.Split(
            '.',
            StringSplitOptions.RemoveEmptyEntries |
            StringSplitOptions.TrimEntries);
        for (int index = 0; index + 1 < parts.Length; index++)
        {
            if (parts[index].Equals(
                    "modelMetrics",
                    StringComparison.OrdinalIgnoreCase))
            {
                return $"{parts[index + 1]}:{MetricFamily(name)}";
            }
        }

        return null;
    }

    private static int SnapshotPreference(string name)
    {
        if (name.StartsWith(
                "modelMetrics.",
                StringComparison.OrdinalIgnoreCase))
        {
            return 2;
        }

        return name.Contains(
            ".modelMetrics.",
            StringComparison.OrdinalIgnoreCase)
                ? 1
                : 0;
    }

    private sealed record UsageMetricKey(string Name, string Unit);

    private sealed class UsageMetricKeyComparer : IEqualityComparer<UsageMetricKey>
    {
        public static UsageMetricKeyComparer Instance { get; } = new();

        public bool Equals(UsageMetricKey? x, UsageMetricKey? y) =>
            x is not null &&
            y is not null &&
            StringComparer.OrdinalIgnoreCase.Equals(x.Name, y.Name) &&
            StringComparer.OrdinalIgnoreCase.Equals(x.Unit, y.Unit);

        public int GetHashCode(UsageMetricKey value) =>
            HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(value.Name),
                StringComparer.OrdinalIgnoreCase.GetHashCode(value.Unit));
    }

    private sealed record UsageIdentity(
        string SourceRecordId,
        string Name,
        long Value,
        string Unit,
        UsageScope Scope,
        UsageBehavior Behavior,
        string BucketId);

    private sealed class UsageIdentityComparer : IEqualityComparer<UsageIdentity>
    {
        public static UsageIdentityComparer Instance { get; } = new();

        public bool Equals(UsageIdentity? x, UsageIdentity? y) =>
            x is not null &&
            y is not null &&
            x.Value == y.Value &&
            x.Scope == y.Scope &&
            x.Behavior == y.Behavior &&
            StringComparer.Ordinal.Equals(x.SourceRecordId, y.SourceRecordId) &&
            StringComparer.OrdinalIgnoreCase.Equals(x.Name, y.Name) &&
            StringComparer.OrdinalIgnoreCase.Equals(x.Unit, y.Unit) &&
            StringComparer.Ordinal.Equals(x.BucketId, y.BucketId);

        public int GetHashCode(UsageIdentity value) =>
            HashCode.Combine(
                StringComparer.Ordinal.GetHashCode(value.SourceRecordId),
                StringComparer.OrdinalIgnoreCase.GetHashCode(value.Name),
                value.Value,
                StringComparer.OrdinalIgnoreCase.GetHashCode(value.Unit),
                value.Scope,
                value.Behavior,
                StringComparer.Ordinal.GetHashCode(value.BucketId));
    }
}

public sealed record UsageSample(
    UsageMeasurement Measurement,
    string BucketId,
    DateTimeOffset Timestamp,
    bool IsAuthoritative = false);

public sealed record AggregatedUsageValue(
    string Name,
    long Value,
    string Unit);

public sealed class UsageNameClassifier
{
    public bool IsInputToken(string name)
    {
        string normalized = Normalize(name);
        return normalized.Contains("input", StringComparison.Ordinal) &&
            !normalized.Contains("cache", StringComparison.Ordinal);
    }

    public bool IsOutputToken(string name)
    {
        string normalized = Normalize(name);
        return normalized.Contains("output", StringComparison.Ordinal) &&
            !normalized.Contains("reason", StringComparison.Ordinal) &&
            !normalized.Contains("thinking", StringComparison.Ordinal);
    }

    public bool IsCacheReadToken(string name)
    {
        string normalized = Normalize(name);
        return normalized.Contains("cache", StringComparison.Ordinal) &&
            (normalized.Contains("read", StringComparison.Ordinal) ||
             normalized.Contains("cached", StringComparison.Ordinal));
    }

    public bool IsCacheWriteToken(string name)
    {
        string normalized = Normalize(name);
        return normalized.Contains("cache", StringComparison.Ordinal) &&
            (normalized.Contains("write", StringComparison.Ordinal) ||
             normalized.Contains("creation", StringComparison.Ordinal));
    }

    public bool IsReasoningToken(string name)
    {
        string normalized = Normalize(name);
        return normalized.Contains("reason", StringComparison.Ordinal) ||
            normalized.Contains("thinking", StringComparison.Ordinal);
    }

    public bool IsAnyToken(string name) =>
        IsInputToken(name) ||
        IsOutputToken(name) ||
        IsCacheReadToken(name) ||
        IsCacheWriteToken(name) ||
        IsReasoningToken(name) ||
        Normalize(name).Contains("total_token", StringComparison.Ordinal);

    private static string Normalize(string name) =>
        name.Replace("-", "_", StringComparison.Ordinal).ToLowerInvariant();
}
