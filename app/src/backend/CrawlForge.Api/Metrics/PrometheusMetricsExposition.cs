using System.Globalization;
using System.Text;
using CrawlForge.Api.Hubs;
using CrawlForge.Contracts.Api;

namespace CrawlForge.Api.Metrics;

/// <summary>
/// Pull-only local Prometheus-class exposition (locked
/// <see cref="IMetricsExposition"/>; FS-CRAWL-001 §10.5, FS-CRAWL-002 §7.3).
/// Rendered on demand at GET /metrics over loopback; nothing is ever pushed,
/// exported, or scraped by any remote service (criterion #20). Families
/// ending in <c>_total</c> accumulate as counters; all others keep the
/// latest observation as gauges. Runtime memory/GC/WAL families refresh
/// from the local health sampler at render time. Label keys are validated
/// against the privacy invariant: identity, machine, and account dimensions
/// are rejected outright.
/// </summary>
public sealed class PrometheusMetricsExposition : IMetricsExposition
{
    private static readonly string[] ForbiddenLabelKeyFragments =
    [
        "user", "account", "identity", "machine", "license", "entitlement", "email", "profile", "credential",
    ];

    private readonly ISystemHealthSampler? _healthSampler;
    private readonly object _gate = new();
    private readonly Dictionary<string, FamilyState> _families = new(StringComparer.Ordinal);

    /// <summary>Creates the exposition.</summary>
    /// <param name="healthSampler">Optional runtime sampler refreshed at render time.</param>
    public PrometheusMetricsExposition(ISystemHealthSampler? healthSampler = null)
    {
        _healthSampler = healthSampler;
    }

    /// <inheritdoc />
    public void Record(string metricFamily, double value, IReadOnlyDictionary<string, string>? labels = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(metricFamily);
        ValidateLabels(labels);
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            throw new ArgumentOutOfRangeException(nameof(value), "Metric observations must be finite.");
        }

        var key = LabelKey(labels);
        lock (_gate)
        {
            var family = GetOrCreateFamily(metricFamily);
            if (family.IsCounter)
            {
                family.Samples.TryGetValue(key, out var existing);
                family.Samples[key] = new Sample(labels, (existing?.Value ?? 0) + value);
            }
            else
            {
                family.Samples[key] = new Sample(labels, value);
            }
        }
    }

    /// <inheritdoc />
    public string Render()
    {
        RefreshRuntimeFamilies();
        var builder = new StringBuilder();
        lock (_gate)
        {
            foreach (var familyName in _families.Keys.OrderBy(name => name, StringComparer.Ordinal))
            {
                var family = _families[familyName];
                if (family.Samples.Count == 0)
                {
                    continue;
                }

                builder.Append("# HELP ").Append(familyName).Append(' ').Append(family.Help).Append('\n');
                builder.Append("# TYPE ").Append(familyName).Append(' ').Append(family.IsCounter ? "counter" : "gauge").Append('\n');
                foreach (var sampleKey in family.Samples.Keys.OrderBy(key => key, StringComparer.Ordinal))
                {
                    var sample = family.Samples[sampleKey];
                    builder.Append(familyName);
                    AppendLabels(builder, sample.Labels);
                    builder.Append(' ').Append(FormatValue(sample.Value)).Append('\n');
                }
            }
        }

        return builder.ToString();
    }

    private void RefreshRuntimeFamilies()
    {
        if (_healthSampler is null)
        {
            return;
        }

        var payload = _healthSampler.CapturePayload();
        SetRuntime(MetricFamilyCatalog.ProcessWorkingSetBytes, payload.BackendWorkingSetBytes, null);
        SetRuntime(MetricFamilyCatalog.ProcessManagedHeapBytes, payload.ManagedHeapBytes, null);
        SetRuntime(MetricFamilyCatalog.GcCollectionsTotal, payload.GcGen0Count,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["generation"] = "0" });
        SetRuntime(MetricFamilyCatalog.GcCollectionsTotal, payload.GcGen1Count,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["generation"] = "1" });
        SetRuntime(MetricFamilyCatalog.GcCollectionsTotal, payload.GcGen2Count,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["generation"] = "2" });

        lock (_gate)
        {
            if (payload.SqliteWalBytes is { } walBytes)
            {
                var family = GetOrCreateFamily(MetricFamilyCatalog.SqliteWalBytes);
                family.Samples[string.Empty] = new Sample(null, walBytes);
            }
            else if (_families.TryGetValue(MetricFamilyCatalog.SqliteWalBytes, out var walFamily))
            {
                // External provider active: the WAL family is absent.
                walFamily.Samples.Clear();
            }
        }
    }

    private void SetRuntime(string familyName, double value, IReadOnlyDictionary<string, string>? labels)
    {
        var key = LabelKey(labels);
        lock (_gate)
        {
            var family = GetOrCreateFamily(familyName);
            family.Samples[key] = new Sample(labels, value);
        }
    }

    private FamilyState GetOrCreateFamily(string metricFamily)
    {
        if (!_families.TryGetValue(metricFamily, out var family))
        {
            family = new FamilyState(
                MetricFamilyCatalog.HelpFor(metricFamily),
                metricFamily.EndsWith("_total", StringComparison.Ordinal));
            _families[metricFamily] = family;
        }

        return family;
    }

    private static void ValidateLabels(IReadOnlyDictionary<string, string>? labels)
    {
        if (labels is null)
        {
            return;
        }

        foreach (var key in labels.Keys)
        {
            if (string.IsNullOrWhiteSpace(key)
                || !(char.IsAsciiLetter(key[0]) || key[0] == '_')
                || !key.All(character => char.IsAsciiLetterOrDigit(character) || character == '_'))
            {
                throw new ArgumentException($"Invalid Prometheus label name '{key}'.", nameof(labels));
            }

            foreach (var fragment in ForbiddenLabelKeyFragments)
            {
                if (key.Contains(fragment, StringComparison.OrdinalIgnoreCase))
                {
                    throw new ArgumentException(
                        $"Label '{key}' violates the privacy invariant: no identity, machine, or account dimensions.",
                        nameof(labels));
                }
            }
        }
    }

    private static string LabelKey(IReadOnlyDictionary<string, string>? labels)
    {
        if (labels is null || labels.Count == 0)
        {
            return string.Empty;
        }

        return string.Join("|", labels.Keys
            .OrderBy(key => key, StringComparer.Ordinal)
            .Select(key => string.Create(CultureInfo.InvariantCulture, $"{key}={labels[key]}")));
    }

    private static void AppendLabels(StringBuilder builder, IReadOnlyDictionary<string, string>? labels)
    {
        if (labels is null || labels.Count == 0)
        {
            return;
        }

        builder.Append('{');
        var first = true;
        foreach (var key in labels.Keys.OrderBy(key => key, StringComparer.Ordinal))
        {
            if (!first)
            {
                builder.Append(',');
            }

            first = false;
            builder.Append(key).Append("=\"").Append(EscapeLabelValue(labels[key])).Append('"');
        }

        builder.Append('}');
    }

    private static string EscapeLabelValue(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal);

    private static string FormatValue(double value)
    {
        if (double.IsPositiveInfinity(value))
        {
            return "+Inf";
        }

        if (double.IsNegativeInfinity(value))
        {
            return "-Inf";
        }

        return value.ToString("G17", CultureInfo.InvariantCulture);
    }

    private sealed record Sample(IReadOnlyDictionary<string, string>? Labels, double Value);

    private sealed class FamilyState(string help, bool isCounter)
    {
        public string Help { get; } = help;

        public bool IsCounter { get; } = isCounter;

        public Dictionary<string, Sample> Samples { get; } = new(StringComparer.Ordinal);
    }
}
