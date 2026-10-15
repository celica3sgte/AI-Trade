namespace AITrade.Market;

public sealed record MacdResult(double?[] Macd, double?[] Signal, double?[] Histogram);

public sealed record BollingerResult(double?[] Middle, double?[] Upper, double?[] Lower);

/// <summary>
/// Standard technical indicators over a series ordered oldest to newest. Each result array is aligned with the
/// input (same length); entries are null until enough data exists for the indicator's lookback period.
/// Definitions follow StockCharts ChartSchool (SMA/EMA, MACD 12/26/9, Bollinger 20/2) and Wilder's RSI.
/// </summary>
public static class Indicators
{
    public static double?[] Sma(IReadOnlyList<double> values, int period)
    {
        var result = new double?[values.Count];
        double sum = 0;
        for (var i = 0; i < values.Count; i++)
        {
            sum += values[i];
            if (i >= period)
            {
                sum -= values[i - period];
            }
            if (i >= period - 1)
            {
                result[i] = sum / period;
            }
        }
        return result;
    }

    /// <summary>EMA seeded with the SMA of the first <paramref name="period"/> values, smoothing 2 / (period + 1).</summary>
    public static double?[] Ema(IReadOnlyList<double> values, int period) => Ema(values.Select(v => (double?)v).ToArray(), period);

    /// <summary>EMA over a series that may start with nulls (e.g. another indicator's output).</summary>
    public static double?[] Ema(IReadOnlyList<double?> values, int period)
    {
        var result = new double?[values.Count];
        var k = 2.0 / (period + 1);
        var start = FirstNonNull(values);
        if (start < 0 || values.Count - start < period)
        {
            return result;
        }

        double seed = 0;
        for (var i = start; i < start + period; i++)
        {
            seed += values[i]!.Value;
        }

        var ema = seed / period;
        result[start + period - 1] = ema;
        for (var i = start + period; i < values.Count; i++)
        {
            ema = (values[i]!.Value - ema) * k + ema;
            result[i] = ema;
        }
        return result;
    }

    /// <summary>
    /// Wilder's RSI: the first average gain/loss is a simple average over <paramref name="period"/> changes,
    /// then avg = (prevAvg * (period - 1) + current) / period.
    /// </summary>
    public static double?[] Rsi(IReadOnlyList<double> closes, int period = 14)
    {
        var result = new double?[closes.Count];
        if (closes.Count <= period)
        {
            return result;
        }

        double avgGain = 0, avgLoss = 0;
        for (var i = 1; i <= period; i++)
        {
            var change = closes[i] - closes[i - 1];
            avgGain += Math.Max(change, 0);
            avgLoss += Math.Max(-change, 0);
        }
        avgGain /= period;
        avgLoss /= period;
        result[period] = ToRsi(avgGain, avgLoss);

        for (var i = period + 1; i < closes.Count; i++)
        {
            var change = closes[i] - closes[i - 1];
            avgGain = (avgGain * (period - 1) + Math.Max(change, 0)) / period;
            avgLoss = (avgLoss * (period - 1) + Math.Max(-change, 0)) / period;
            result[i] = ToRsi(avgGain, avgLoss);
        }
        return result;
    }

    /// <summary>MACD line = EMA(fast) - EMA(slow); signal = EMA(signalPeriod) of MACD; histogram = MACD - signal.</summary>
    public static MacdResult Macd(IReadOnlyList<double> closes, int fast = 12, int slow = 26, int signalPeriod = 9)
    {
        var fastEma = Ema(closes, fast);
        var slowEma = Ema(closes, slow);
        var macd = new double?[closes.Count];
        for (var i = 0; i < closes.Count; i++)
        {
            if (fastEma[i] is double f && slowEma[i] is double s)
            {
                macd[i] = f - s;
            }
        }

        var signal = Ema(macd, signalPeriod);
        var histogram = new double?[closes.Count];
        for (var i = 0; i < closes.Count; i++)
        {
            if (macd[i] is double m && signal[i] is double sig)
            {
                histogram[i] = m - sig;
            }
        }
        return new MacdResult(macd, signal, histogram);
    }

    /// <summary>
    /// Middle = SMA(period); upper/lower = middle ± multiplier × standard deviation of the same window.
    /// Uses the population standard deviation, as most charting packages do.
    /// </summary>
    public static BollingerResult Bollinger(IReadOnlyList<double> closes, int period = 20, double multiplier = 2)
    {
        var middle = Sma(closes, period);
        var upper = new double?[closes.Count];
        var lower = new double?[closes.Count];
        for (var i = period - 1; i < closes.Count; i++)
        {
            var mean = middle[i]!.Value;
            double sumSq = 0;
            for (var j = i - period + 1; j <= i; j++)
            {
                var d = closes[j] - mean;
                sumSq += d * d;
            }
            var sd = Math.Sqrt(sumSq / period);
            upper[i] = mean + multiplier * sd;
            lower[i] = mean - multiplier * sd;
        }
        return new BollingerResult(middle, upper, lower);
    }

    private static double ToRsi(double avgGain, double avgLoss) =>
        avgLoss == 0 ? (avgGain == 0 ? 50 : 100) : 100 - 100 / (1 + avgGain / avgLoss);

    private static int FirstNonNull(IReadOnlyList<double?> values)
    {
        for (var i = 0; i < values.Count; i++)
        {
            if (values[i].HasValue)
            {
                return i;
            }
        }
        return -1;
    }
}
