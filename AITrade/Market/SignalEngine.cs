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

/// <summary>A single strategy's vote: +1 bullish, -1 bearish, 0 neutral.</summary>
public sealed record StrategyVote(string Strategy, int Vote, string Reason);

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

/// <summary>
/// Combines common technical strategies into a recommendation. Each strategy votes +1/0/-1; the score is the
/// average vote. Trend, Momentum and MACD are trend-following; RSI and Bollinger are mean-reversion, so they
/// frequently disagree, which pulls the score toward neutral. Thresholds are untested until backtesting (step 5).
/// </summary>
public static class SignalEngine
{
    // Net two more bullish than bearish votes out of five.
    public const double ActionThreshold = 0.4;
    public const double HighConfidenceThreshold = 0.8;

    // Daily data older than this (calendar days) is flagged, e.g. if prices haven't been refreshed.
    private const int StaleAfterDays = 5;

    private static readonly TimeZoneInfo MarketTimeZone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

    /// <summary>Today's date in US market (Eastern) time, matching how daily bars are dated.</summary>
    public static DateOnly MarketToday() =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, MarketTimeZone));

    public static Recommendation? Evaluate(IReadOnlyList<DailyPrice> history, bool isOwned, DateOnly today)
    {
        if (history.Count < 2)
        {
            return null;
        }

        var closes = history.Select(p => (double)p.Close).ToArray();
        var i = closes.Length - 1;
        var close = closes[i];

        var sma20 = Indicators.Sma(closes, 20);
        var sma50 = Indicators.Sma(closes, 50);
        var sma200 = Indicators.Sma(closes, 200);
        var rsi = Indicators.Rsi(closes, 14);
        var macd = Indicators.Macd(closes);
        var bb = Indicators.Bollinger(closes);

        List<StrategyVote> votes =
        [
            Trend(close, sma50[i], sma200[i]),
            Momentum(sma20, sma50, i),
            Macd(macd, i),
            Rsi(rsi[i]),
            Bollinger(close, bb.Upper[i], bb.Lower[i]),
        ];

        var score = votes.Average(v => v.Vote);
        var action = isOwned
            ? score >= ActionThreshold ? RecommendedAction.BuyMore
                : score <= -ActionThreshold ? RecommendedAction.Sell
                : RecommendedAction.Hold
            : score >= ActionThreshold ? RecommendedAction.Buy : RecommendedAction.Wait;

        var magnitude = Math.Abs(score);
        var confidence = magnitude >= HighConfidenceThreshold ? Confidence.High
            : magnitude >= ActionThreshold ? Confidence.Medium
            : Confidence.Low;

        var asOf = history[i].Date;
        return new Recommendation(action, score, confidence, votes, asOf, today.DayNumber - asOf.DayNumber > StaleAfterDays);
    }

    private static StrategyVote Trend(double close, double? sma50, double? sma200)
    {
        const string name = "Trend (SMA 50 / 200)";
        if (sma50 is not double s50 || sma200 is not double s200)
        {
            return new(name, 0, "Not enough history for a 200-day average");
        }

        var priceAbove = close > s200;
        var averagesAbove = s50 > s200;
        return (priceAbove, averagesAbove) switch
        {
            (true, true) => new(name, 1, "Uptrend: price and 50-day average are above the 200-day average"),
            (false, false) => new(name, -1, "Downtrend: price and 50-day average are below the 200-day average"),
            _ => new(name, 0, $"Mixed: price is {(priceAbove ? "above" : "below")} the 200-day average but the 50-day is {(averagesAbove ? "above" : "below")} it"),
        };
    }

    private static StrategyVote Momentum(double?[] sma20, double?[] sma50, int i)
    {
        const string name = "Momentum (SMA 20 / 50)";
        if (sma20[i] is not double fast || sma50[i] is not double slow)
        {
            return new(name, 0, "Not enough history");
        }

        var crossedRecently = CrossedWithin(sma20, sma50, i, 5);
        return fast > slow
            ? new(name, 1, "20-day average is above the 50-day" + (crossedRecently ? " (crossed above in the last 5 days)" : ""))
            : new(name, -1, "20-day average is below the 50-day" + (crossedRecently ? " (crossed below in the last 5 days)" : ""));
    }

    private static StrategyVote Macd(MacdResult macd, int i)
    {
        const string name = "MACD (12, 26, 9)";
        if (macd.Macd[i] is not double m || macd.Signal[i] is not double s
            || macd.Histogram[i] is not double h || macd.Histogram[i - 1] is not double prevH)
        {
            return new(name, 0, "Not enough history");
        }

        var rising = h >= prevH;
        return (m > s, rising) switch
        {
            (true, true) => new(name, 1, "Above the signal line with strengthening momentum"),
            (false, false) => new(name, -1, "Below the signal line with weakening momentum"),
            (true, false) => new(name, 0, "Above the signal line but momentum is fading"),
            (false, true) => new(name, 0, "Below the signal line but momentum is improving"),
        };
    }

    private static StrategyVote Rsi(double? rsi)
    {
        const string name = "RSI (14)";
        return rsi switch
        {
            null => new(name, 0, "Not enough history"),
            < 30 => new(name, 1, $"Oversold at {rsi:0.0} (below 30); a rebound is more likely"),
            > 70 => new(name, -1, $"Overbought at {rsi:0.0} (above 70); a pullback is more likely"),
            _ => new(name, 0, $"Neutral at {rsi:0.0}"),
        };
    }

    private static StrategyVote Bollinger(double close, double? upper, double? lower)
    {
        const string name = "Bollinger Bands (20, 2)";
        if (upper is not double u || lower is not double l)
        {
            return new(name, 0, "Not enough history");
        }

        return close < l ? new(name, 1, "Closed below the lower band (stretched to the downside)")
            : close > u ? new(name, -1, "Closed above the upper band (stretched to the upside)")
            : new(name, 0, "Inside the bands");
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
