using AITrade.Data;

namespace AITrade.Market;

public enum RecommendedAction
{
    Buy,
    Wait,
    BuyMore,
    Hold,
    Sell
}

public enum Confidence
{
    Low,
    Medium,
    High
}

public enum Strategy
{
    Trend,
    Momentum,
    Macd,
    Rsi,
    Bollinger,
    Forecast
}

/// <summary>A single strategy's vote: +1 bullish, -1 bearish, 0 neutral.</summary>
public sealed record StrategyVote(Strategy Kind, string Strategy, int Vote, string Reason);

public sealed record Recommendation(
    RecommendedAction Action,
    double Score,
    Confidence Confidence,
    IReadOnlyList<StrategyVote> Votes,
    DateOnly AsOf,
    bool IsStale)
{
    public string ActionLabel => Action switch
    {
        RecommendedAction.BuyMore => "Buy more",
        _ => Action.ToString()
    };
}

/// <summary>Tunable rules: the score needed to act, and which strategies vote.</summary>
public sealed record SignalSettings(double Threshold, IReadOnlySet<Strategy> Enabled)
{
    public static SignalSettings Default { get; } = new(SignalEngine.ActionThreshold, Enum.GetValues<Strategy>().ToHashSet());
}

/// <summary>
/// Indicators computed once over a full history. Every indicator is causal (the value at index i uses only
/// prices up to i), so evaluating at an earlier index sees exactly what was known on that day.
/// </summary>
public sealed class IndicatorSet
{
    public IndicatorSet(IReadOnlyList<DailyPrice> history)
    {
        History = history;
        Closes = history.Select(p => (double)p.Close).ToArray();
        Sma20 = Indicators.Sma(Closes, 20);
        Sma50 = Indicators.Sma(Closes, 50);
        Sma200 = Indicators.Sma(Closes, 200);
        Rsi = Indicators.Rsi(Closes, 14);
        Macd = Indicators.Macd(Closes);
        Bollinger = Indicators.Bollinger(Closes);
        ForecastChange = new double?[Closes.Length];
    }

    /// <summary>
    /// Fills <see cref="ForecastChange"/> for bars <paramref name="from"/>..<paramref name="to"/> (inclusive).
    /// Forecasts are trained per bar (walk-forward), so they are computed on demand rather than up front.
    /// </summary>
    public void ComputeForecasts(int from, int to)
    {
        for (var i = Math.Max(0, from); i <= to && i < Closes.Length; i++)
        {
            ForecastChange[i] = PriceForecaster.Forecast(Closes, i)?.ChangeFrom(Closes[i]);
        }
    }

    public IReadOnlyList<DailyPrice> History { get; }
    public double[] Closes { get; }
    public double?[] Sma20 { get; }
    public double?[] Sma50 { get; }
    public double?[] Sma200 { get; }
    public double?[] Rsi { get; }
    public MacdResult Macd { get; }
    public BollingerResult Bollinger { get; }

    /// <summary>ML.NET forecast change over the next few days at each bar; null where not computed.</summary>
    public double?[] ForecastChange { get; }
}

/// <summary>
/// Combines common technical strategies into a recommendation. Each enabled strategy votes +1/0/-1; the score is
/// the average vote. Trend, Momentum and MACD are trend-following; RSI and Bollinger are mean-reversion, so they
/// frequently disagree, which pulls the score toward neutral.
/// </summary>
public static class SignalEngine
{
    // Average vote needed to act. With all six strategies voting this takes three more bullish than bearish votes.
    public const double ActionThreshold = 0.4;
    public const double HighConfidenceThreshold = 0.8;

    // Daily data older than this (calendar days) is flagged, e.g. if prices haven't been refreshed.
    private const int StaleAfterDays = 5;

    // Guards threshold comparisons against floating-point noise in averaged votes.
    private const double Epsilon = 1e-9;

    // The forecast votes only when it predicts at least this move over its horizon (1%).
    public const double ForecastMinMove = 0.01;

    private static readonly TimeZoneInfo MarketTimeZone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

    /// <summary>Today's date in US market (Eastern) time, matching how daily bars are dated.</summary>
    public static DateOnly MarketToday() =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, MarketTimeZone));

    /// <summary>Recommendation as of the latest bar in the history.</summary>
    public static Recommendation? Evaluate(IReadOnlyList<DailyPrice> history, bool isOwned, DateOnly today, SignalSettings? settings = null)
    {
        if (history.Count < 2)
        {
            return null;
        }

        settings ??= SignalSettings.Default;
        var ind = new IndicatorSet(history);
        var last = history.Count - 1;
        if (settings.Enabled.Contains(Strategy.Forecast))
        {
            ind.ComputeForecasts(last, last);
        }

        var recommendation = EvaluateAt(ind, last, isOwned, settings);
        return recommendation with { IsStale = today.DayNumber - recommendation.AsOf.DayNumber > StaleAfterDays };
    }

    /// <summary>Recommendation as of bar <paramref name="i"/>, using only data up to that bar. Requires i >= 1.</summary>
    public static Recommendation EvaluateAt(IndicatorSet ind, int i, bool isOwned, SignalSettings settings)
    {
        var close = ind.Closes[i];
        var votes = new List<StrategyVote>(5);
        foreach (var strategy in Enum.GetValues<Strategy>())
        {
            if (!settings.Enabled.Contains(strategy))
            {
                continue;
            }

            votes.Add(strategy switch
            {
                Strategy.Trend => Trend(close, ind.Sma50[i], ind.Sma200[i]),
                Strategy.Momentum => Momentum(ind.Sma20, ind.Sma50, i),
                Strategy.Macd => Macd(ind.Macd, i),
                Strategy.Rsi => Rsi(ind.Rsi[i]),
                Strategy.Bollinger => Bollinger(close, ind.Bollinger.Upper[i], ind.Bollinger.Lower[i]),
                Strategy.Forecast => Forecast(ind.ForecastChange[i]),
                _ => throw new ArgumentOutOfRangeException(nameof(settings)),
            });
        }

        var score = votes.Count == 0 ? 0 : votes.Average(v => v.Vote);
        var bullish = score >= settings.Threshold - Epsilon;
        var bearish = score <= -settings.Threshold + Epsilon;
        var action = isOwned
            ? bullish ? RecommendedAction.BuyMore : bearish ? RecommendedAction.Sell : RecommendedAction.Hold
            : bullish ? RecommendedAction.Buy : RecommendedAction.Wait;

        var magnitude = Math.Abs(score);
        var confidence = magnitude >= HighConfidenceThreshold - Epsilon ? Confidence.High
            : magnitude >= settings.Threshold - Epsilon ? Confidence.Medium
            : Confidence.Low;

        return new Recommendation(action, score, confidence, votes, ind.History[i].Date, IsStale: false);
    }

    public static string DisplayName(Strategy strategy) => strategy switch
    {
        Strategy.Trend => "Trend (SMA 50 / 200)",
        Strategy.Momentum => "Momentum (SMA 20 / 50)",
        Strategy.Macd => "MACD (12, 26, 9)",
        Strategy.Rsi => "RSI (14)",
        Strategy.Bollinger => "Bollinger Bands (20, 2)",
        Strategy.Forecast => $"ML.NET forecast ({PriceForecaster.Horizon}-day)",
        _ => strategy.ToString()
    };

    private static StrategyVote Vote(Strategy kind, int vote, string reason) => new(kind, DisplayName(kind), vote, reason);

    private static StrategyVote Trend(double close, double? sma50, double? sma200)
    {
        const Strategy k = Strategy.Trend;
        if (sma50 is not double s50 || sma200 is not double s200)
        {
            return Vote(k, 0, "Not enough history for a 200-day average");
        }

        var priceAbove = close > s200;
        var averagesAbove = s50 > s200;
        return (priceAbove, averagesAbove) switch
        {
            (true, true) => Vote(k, 1, "Uptrend: price and 50-day average are above the 200-day average"),
            (false, false) => Vote(k, -1, "Downtrend: price and 50-day average are below the 200-day average"),
            _ => Vote(k, 0, $"Mixed: price is {(priceAbove ? "above" : "below")} the 200-day average but the 50-day is {(averagesAbove ? "above" : "below")} it"),
        };
    }

    private static StrategyVote Momentum(double?[] sma20, double?[] sma50, int i)
    {
        const Strategy k = Strategy.Momentum;
        if (sma20[i] is not double fast || sma50[i] is not double slow)
        {
            return Vote(k, 0, "Not enough history");
        }

        var crossedRecently = CrossedWithin(sma20, sma50, i, 5);
        return fast > slow
            ? Vote(k, 1, "20-day average is above the 50-day" + (crossedRecently ? " (crossed above in the last 5 days)" : ""))
            : Vote(k, -1, "20-day average is below the 50-day" + (crossedRecently ? " (crossed below in the last 5 days)" : ""));
    }

    private static StrategyVote Macd(MacdResult macd, int i)
    {
        const Strategy k = Strategy.Macd;
        if (macd.Macd[i] is not double m || macd.Signal[i] is not double s
            || macd.Histogram[i] is not double h || macd.Histogram[i - 1] is not double prevH)
        {
            return Vote(k, 0, "Not enough history");
        }

        var rising = h >= prevH;
        return (m > s, rising) switch
        {
            (true, true) => Vote(k, 1, "Above the signal line with strengthening momentum"),
            (false, false) => Vote(k, -1, "Below the signal line with weakening momentum"),
            (true, false) => Vote(k, 0, "Above the signal line but momentum is fading"),
            (false, true) => Vote(k, 0, "Below the signal line but momentum is improving"),
        };
    }

    private static StrategyVote Rsi(double? rsi)
    {
        const Strategy k = Strategy.Rsi;
        return rsi switch
        {
            null => Vote(k, 0, "Not enough history"),
            < 30 => Vote(k, 1, $"Oversold at {rsi:0.0} (below 30); a rebound is more likely"),
            > 70 => Vote(k, -1, $"Overbought at {rsi:0.0} (above 70); a pullback is more likely"),
            _ => Vote(k, 0, $"Neutral at {rsi:0.0}"),
        };
    }

    private static StrategyVote Bollinger(double close, double? upper, double? lower)
    {
        const Strategy k = Strategy.Bollinger;
        if (upper is not double u || lower is not double l)
        {
            return Vote(k, 0, "Not enough history");
        }

        return close < l ? Vote(k, 1, "Closed below the lower band (stretched to the downside)")
            : close > u ? Vote(k, -1, "Closed above the upper band (stretched to the upside)")
            : Vote(k, 0, "Inside the bands");
    }

    private static StrategyVote Forecast(double? change)
    {
        const Strategy k = Strategy.Forecast;
        return change switch
        {
            null => Vote(k, 0, "Not enough history to forecast"),
            >= ForecastMinMove => Vote(k, 1, $"Forecasts {change:+0.0%} over the next {PriceForecaster.Horizon} trading days"),
            <= -ForecastMinMove => Vote(k, -1, $"Forecasts {change:+0.0%;-0.0%} over the next {PriceForecaster.Horizon} trading days"),
            _ => Vote(k, 0, $"Forecasts little change ({change:+0.0%;-0.0%;0.0%}) over the next {PriceForecaster.Horizon} trading days"),
        };
    }

    // True if the fast series crossed the slow series at any point in the last `days` bars.
    private static bool CrossedWithin(double?[] fast, double?[] slow, int i, int days)
    {
        for (var j = i; j > i - days && j > 0; j--)
        {
            if (fast[j] is double f && slow[j] is double s && fast[j - 1] is double pf && slow[j - 1] is double ps
                && (f > s) != (pf > ps))
            {
                return true;
            }
        }
        return false;
    }
}
