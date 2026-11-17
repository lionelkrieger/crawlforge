using Microsoft.Data.Sqlite;

namespace CrawlForge.Data.Sqlite.Extensions;

/// <summary>
/// Extended math/statistics capability family (FS-CRAWL-001 §2.4). Registers the
/// aggregates <c>median</c>, <c>variance</c>/<c>var_samp</c>, <c>var_pop</c>,
/// <c>stddev</c>/<c>stddev_samp</c>, <c>stddev_pop</c>, and <c>percentile</c>
/// (continuous, linear interpolation). The bundled native engine additionally supplies
/// the standard scalar math functions (sqrt, ln, log, exp, pow, trigonometry, …),
/// verified by the capability probe. SQL NULL inputs are ignored by every aggregate.
/// Accumulators are value-typed with lazily created state so concurrent aggregate
/// invocations inside one query never share state.
/// </summary>
internal static class MathStatisticsFunctions
{
    /// <summary>Registers the math/statistics family on an open connection.</summary>
    /// <param name="connection">The connection to extend.</param>
    public static void Register(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        connection.CreateAggregate(
            "median",
            default(NumericAccumulator),
            static (NumericAccumulator acc, double? value) => Accumulate(acc, value),
            static (NumericAccumulator acc) => Median(acc.Values),
            isDeterministic: true);

        connection.CreateAggregate(
            "variance",
            default(NumericAccumulator),
            static (NumericAccumulator acc, double? value) => Accumulate(acc, value),
            static (NumericAccumulator acc) => Variance(acc.Values, sample: true),
            isDeterministic: true);

        connection.CreateAggregate(
            "var_samp",
            default(NumericAccumulator),
            static (NumericAccumulator acc, double? value) => Accumulate(acc, value),
            static (NumericAccumulator acc) => Variance(acc.Values, sample: true),
            isDeterministic: true);

        connection.CreateAggregate(
            "var_pop",
            default(NumericAccumulator),
            static (NumericAccumulator acc, double? value) => Accumulate(acc, value),
            static (NumericAccumulator acc) => Variance(acc.Values, sample: false),
            isDeterministic: true);

        connection.CreateAggregate(
            "stddev",
            default(NumericAccumulator),
            static (NumericAccumulator acc, double? value) => Accumulate(acc, value),
            static (NumericAccumulator acc) => SquareRoot(Variance(acc.Values, sample: true)),
            isDeterministic: true);

        connection.CreateAggregate(
            "stddev_samp",
            default(NumericAccumulator),
            static (NumericAccumulator acc, double? value) => Accumulate(acc, value),
            static (NumericAccumulator acc) => SquareRoot(Variance(acc.Values, sample: true)),
            isDeterministic: true);

        connection.CreateAggregate(
            "stddev_pop",
            default(NumericAccumulator),
            static (NumericAccumulator acc, double? value) => Accumulate(acc, value),
            static (NumericAccumulator acc) => SquareRoot(Variance(acc.Values, sample: false)),
            isDeterministic: true);

        connection.CreateAggregate(
            "percentile",
            default(PercentileAccumulator),
            static (PercentileAccumulator acc, double? value, double? fraction) =>
            {
                if (fraction is { } p)
                {
                    if (p < 0.0 || p > 1.0 || double.IsNaN(p))
                    {
                        throw new ArgumentOutOfRangeException(nameof(fraction), p, "The percentile fraction must be between 0 and 1.");
                    }

                    if (acc.Fraction is { } existing && Math.Abs(existing - p) > double.Epsilon)
                    {
                        throw new ArgumentException("The percentile fraction must be constant across all rows.", nameof(fraction));
                    }

                    acc.Fraction = p;
                }

                if (value is { } v && !double.IsNaN(v))
                {
                    acc.Values ??= [];
                    acc.Values.Add(v);
                }

                return acc;
            },
            static (PercentileAccumulator acc) => Percentile(acc.Values, acc.Fraction),
            isDeterministic: true);
    }

    private static NumericAccumulator Accumulate(NumericAccumulator acc, double? value)
    {
        if (value is { } v && !double.IsNaN(v))
        {
            acc.Values ??= [];
            acc.Values.Add(v);
        }

        return acc;
    }

    private static double? Median(List<double>? values)
    {
        if (values is null || values.Count == 0)
        {
            return null;
        }

        var sorted = SortCopy(values);
        var middle = sorted.Count / 2;
        return sorted.Count % 2 == 1
            ? sorted[middle]
            : (sorted[middle - 1] + sorted[middle]) / 2.0;
    }

    private static double? Variance(List<double>? values, bool sample)
    {
        if (values is null)
        {
            return null;
        }

        var minimum = sample ? 2 : 1;
        if (values.Count < minimum)
        {
            return null;
        }

        var mean = 0.0;
        foreach (var value in values)
        {
            mean += value;
        }

        mean /= values.Count;

        var sumSquared = 0.0;
        foreach (var value in values)
        {
            var deviation = value - mean;
            sumSquared += deviation * deviation;
        }

        return sumSquared / (values.Count - (sample ? 1 : 0));
    }

    private static double? SquareRoot(double? value) =>
        value is { } v ? Math.Sqrt(v) : null;

    private static double? Percentile(List<double>? values, double? fraction)
    {
        if (values is null || values.Count == 0 || fraction is not { } p)
        {
            return null;
        }

        var sorted = SortCopy(values);
        if (sorted.Count == 1)
        {
            return sorted[0];
        }

        var rank = p * (sorted.Count - 1);
        var lower = (int)Math.Floor(rank);
        var upper = Math.Min(lower + 1, sorted.Count - 1);
        var weight = rank - lower;
        return sorted[lower] + (weight * (sorted[upper] - sorted[lower]));
    }

    private static List<double> SortCopy(List<double> values)
    {
        var copy = new List<double>(values);
        copy.Sort();
        return copy;
    }

    private struct NumericAccumulator
    {
        public List<double>? Values;
    }

    private struct PercentileAccumulator
    {
        public List<double>? Values;

        public double? Fraction;
    }
}
