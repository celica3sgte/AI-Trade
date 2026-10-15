using AITrade.Data;

namespace AITrade.Market;

public sealed record BacktestOptions(decimal StartingCash = 10_000m, decimal CostBps = 5m);

public enum TradeSide
{
    Buy,
    Sell
}

public sealed record BacktestTrade(DateOnly SignalDate, DateOnly Date, TradeSide Side, decimal Price, decimal Shares, double Score);

public sealed record EquityPoint(DateOnly Date, decimal Strategy, decimal BuyAndHold, bool InMarket);

/// <summary>
/// How the walk-forward ML.NET forecasts compared with what actually happened over the forecast horizon.
/// Errors are percentages of the price on the forecast day. "Naive" assumes no change, a common baseline.
/// </summary>
public sealed record ForecastAccuracy(int Samples, double DirectionHitRate, double MeanAbsError, double NaiveMeanAbsError);

public sealed record BacktestResult(
    string Symbol,
    DateOnly StartDate,
    DateOnly EndDate,
    decimal StartingCash,
    decimal FinalValue,
    decimal BuyAndHoldFinalValue,
    double MaxDrawdown,
    double BuyAndHoldMaxDrawdown,
    double Exposure,
    int RoundTrips,
    int WinningRoundTrips,
    bool EndsInMarket,
    IReadOnlyList<BacktestTrade> Trades,
    IReadOnlyList<EquityPoint> Equity,
    ForecastAccuracy? Forecast)
{
    public double TotalReturn => (double)(FinalValue / StartingCash - 1);

    public double BuyAndHoldReturn => (double)(BuyAndHoldFinalValue / StartingCash - 1);

    public double? WinRate => RoundTrips == 0 ? null : (double)WinningRoundTrips / RoundTrips;
}

/// <summary>
/// Long-only, all-in/all-out simulation of the signal rules over stored daily history.
/// The signal for day t uses data through t's close and executes at day t+1's open, so there is no look-ahead.
/// Buy and hold buys at the same first open. Prices are split/dividend adjusted.
/// </summary>
public static class Backtester
{
    // First bar where every indicator (the 200-day SMA is the longest) has a value.
    public const int WarmupBars = 200;

    public static BacktestResult? Run(string symbol, IReadOnlyList<DailyPrice> history, SignalSettings settings, BacktestOptions options)
    {
        // Need the warm-up, at least one bar to execute on, and one more to measure.
        if (history.Count < WarmupBars + 2)
        {
            return null;
        }

        var ind = new IndicatorSet(history);
        var costRate = options.CostBps / 10_000m;
        var start = WarmupBars - 1; // first signal day

        var useForecast = settings.Enabled.Contains(Strategy.Forecast);
        if (useForecast)
        {
            ind.ComputeForecasts(start, history.Count - 2);
        }
        var firstOpen = history[start + 1].Open;

        decimal cash = options.StartingCash, shares = 0;
        var bhShares = options.StartingCash * (1 - costRate) / firstOpen;

        var trades = new List<BacktestTrade>();
        var equity = new List<EquityPoint>(history.Count - start);
        decimal? entryCost = null;
        int roundTrips = 0, wins = 0, daysInMarket = 0;

        var signal = SignalEngine.EvaluateAt(ind, start, isOwned: false, settings);

        for (var j = start + 1; j < history.Count; j++)
        {
            var bar = history[j];

            // Execute yesterday's signal at today's open.
            if (shares == 0 && signal.Action == RecommendedAction.Buy && bar.Open > 0)
            {
                entryCost = cash;
                shares = cash * (1 - costRate) / bar.Open;
                cash = 0;
                trades.Add(new(signal.AsOf, bar.Date, TradeSide.Buy, bar.Open, shares, signal.Score));
            }
            else if (shares > 0 && signal.Action == RecommendedAction.Sell)
            {
                cash = shares * bar.Open * (1 - costRate);
                trades.Add(new(signal.AsOf, bar.Date, TradeSide.Sell, bar.Open, shares, signal.Score));
                roundTrips++;
                if (cash > entryCost)
                {
                    wins++;
                }
                shares = 0;
                entryCost = null;
            }

            var inMarket = shares > 0;
            if (inMarket)
            {
                daysInMarket++;
            }
            equity.Add(new(bar.Date, cash + shares * bar.Close, bhShares * bar.Close, inMarket));

            if (j < history.Count - 1)
            {
                signal = SignalEngine.EvaluateAt(ind, j, isOwned: inMarket, settings);
            }
        }

        var last = equity[^1];
        return new BacktestResult(
            symbol,
            equity[0].Date,
            last.Date,
            options.StartingCash,
            last.Strategy,
            last.BuyAndHold,
            MaxDrawdown(equity.Select(e => e.Strategy)),
            MaxDrawdown(equity.Select(e => e.BuyAndHold)),
            (double)daysInMarket / equity.Count,
            roundTrips,
            wins,
            shares > 0,
            trades,
            equity,
            useForecast ? MeasureForecasts(ind, start) : null);
    }

    private static ForecastAccuracy? MeasureForecasts(IndicatorSet ind, int start)
    {
        int samples = 0, hits = 0;
        double absError = 0, naiveAbsError = 0;
        var closes = ind.Closes;
        for (var i = start; i + PriceForecaster.Horizon < closes.Length; i++)
        {
            if (ind.ForecastChange[i] is not double predictedChange || closes[i] == 0)
            {
                continue;
            }

            var actualChange = closes[i + PriceForecaster.Horizon] / closes[i] - 1;
            samples++;
            if (Math.Sign(predictedChange) == Math.Sign(actualChange) && actualChange != 0)
            {
                hits++;
            }
            absError += Math.Abs(predictedChange - actualChange);
            naiveAbsError += Math.Abs(actualChange);
        }

        return samples == 0 ? null : new ForecastAccuracy(samples, (double)hits / samples, absError / samples, naiveAbsError / samples);
    }

    /// <summary>Largest peak-to-trough decline, as a positive fraction (0.25 = 25%).</summary>
    private static double MaxDrawdown(IEnumerable<decimal> values)
    {
        decimal peak = 0;
        double maxDd = 0;
        foreach (var v in values)
        {
            peak = Math.Max(peak, v);
            if (peak > 0)
            {
                maxDd = Math.Max(maxDd, (double)((peak - v) / peak));
            }
        }
        return maxDd;
    }
}
