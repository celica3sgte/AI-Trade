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
/// Where the backtest's trade rules stand as of the latest final bar, i.e. what the backtest chart's markers say to do now.
/// </summary>
/// <param name="InPosition">Whether the backtest is holding the stock before acting on the latest signal.</param>
/// <param name="Pending">Trade the latest signal triggers at the next open (a new marker), if any.</param>
/// <param name="LastTradeDate">Date of the trade that set the current position; null if the backtest has never bought.</param>
/// <param name="ProvisionalBar">Date of a newer, still-forming bar that was left out, if any.</param>
/// <param name="Threshold">Score needed to trade under the settings used (buy at +Threshold, sell at -Threshold).</param>
public sealed record TradeSignal(
    bool InPosition,
    TradeSide? Pending,
    DateOnly? LastTradeDate,
    double Score,
    DateOnly SignalDate,
    DateOnly? ProvisionalBar,
    bool IsStale,
    double Threshold);

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

    /// <summary>
    /// The rules of <see cref="Run"/> applied through the latest final bar, without simulating every day.
    /// Run buys when out and the score reaches +threshold, and sells when in and it reaches -threshold, so the
    /// position on any day is set by the run of same-side threshold signals ending at the most recent one. Scanning
    /// back to the start of that run gives the same position and trade dates as the backtest chart.
    /// </summary>
    public static TradeSignal? CurrentSignal(IReadOnlyList<DailyPrice> history, SignalSettings settings, DateOnly today)
    {
        var last = history.Count - 1;
        DateOnly? provisional = null;
        if (last >= 0 && !SignalEngine.IsFinal(history[last]))
        {
            provisional = history[last].Date;
            last--;
        }

        var start = WarmupBars - 1;
        if (last < start)
        {
            return null;
        }

        var ind = new IndicatorSet(history);
        var useForecast = settings.Enabled.Contains(Strategy.Forecast);
        double latestScore = 0;

        // Buy if the bar's score reached +threshold, Sell if it reached -threshold, otherwise null.
        TradeSide? SideAt(int i)
        {
            if (useForecast)
            {
                ind.ComputeForecasts(i, i);
            }

            var r = SignalEngine.EvaluateAt(ind, i, isOwned: true, settings);
            if (i == last)
            {
                latestScore = r.Score;
            }
            return r.Action switch
            {
                RecommendedAction.BuyMore => TradeSide.Buy,
                RecommendedAction.Sell => TradeSide.Sell,
                _ => null
            };
        }

        TradeSide? side = null;
        int runStart = -1;
        var reachedOpposite = false;
        for (var i = last; i >= start; i--)
        {
            var s = SideAt(i);
            if (s is null)
            {
                continue;
            }
            if (side is null)
            {
                side = s;
            }
            else if (s != side)
            {
                reachedOpposite = true;
                break;
            }
            runStart = i;
        }

        // A sell signal only trades if an earlier buy put the backtest in the stock.
        var traded = side == TradeSide.Buy || (side == TradeSide.Sell && reachedOpposite);
        var signalDate = history[last].Date;
        var isStale = today.DayNumber - signalDate.DayNumber > SignalEngine.StaleAfterDays;
        if (!traded)
        {
            return new TradeSignal(false, null, null, latestScore, signalDate, provisional, isStale, settings.Threshold);
        }

        var isBuy = side == TradeSide.Buy;
        return runStart == last
            ? new TradeSignal(!isBuy, side, null, latestScore, signalDate, provisional, isStale, settings.Threshold)
            : new TradeSignal(isBuy, null, history[runStart + 1].Date, latestScore, signalDate, provisional, isStale, settings.Threshold);
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
