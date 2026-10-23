using AITrade.Data;
using Microsoft.Extensions.Options;

namespace AITrade.Market;

/// <summary>
/// Refreshes daily prices for every watchlist symbol each weekday after the close, so signals use the day's final bar
/// without anyone clicking Refresh prices. Runs only while the app is running; on startup it catches up if the most
/// recent scheduled run was missed. Market holidays aren't skipped: a refresh then finds no new bar and writes nothing.
/// </summary>
public sealed class DailyPriceRefresher(
    IServiceScopeFactory scopeFactory,
    IOptions<PriceRefreshOptions> options,
    IOptions<AlpacaOptions> alpacaOptions,
    ILogger<DailyPriceRefresher> logger) : BackgroundService
{
    private const int MaxAttempts = 3;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMinutes(15);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var opts = options.Value;
        if (!opts.Enabled)
        {
            logger.LogInformation("Scheduled price refresh is disabled");
            return;
        }
        if (!alpacaOptions.Value.IsConfigured)
        {
            logger.LogWarning("Scheduled price refresh is off because Alpaca API keys aren't configured");
            return;
        }

        var runAt = opts.RunAt;
        if (runAt < SignalEngine.BarFinalAt)
        {
            logger.LogWarning("PriceRefresh:RunAt {RunAt} is before daily bars are final; using {FinalAt} instead", runAt, SignalEngine.BarFinalAt);
            runAt = SignalEngine.BarFinalAt;
        }

        DateOnly? handled = null;
        var failures = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            // The latest scheduled run at or before now; on startup this catches up on a run missed while the app was down.
            var due = LastRunDay(DateTime.UtcNow, runAt);
            if (due != handled)
            {
                if (await TryRefreshAsync(due, stoppingToken) || ++failures >= MaxAttempts)
                {
                    handled = due;
                    failures = 0;
                }
                else
                {
                    await Task.Delay(RetryDelay, stoppingToken);
                    continue;
                }
            }

            var next = NextRunDay(DateTime.UtcNow, runAt);
            var nextUtc = ScheduledUtc(next, runAt);
            logger.LogInformation("Next scheduled price refresh: {Next} {RunAt} Eastern", next, runAt);
            var wait = nextUtc - DateTime.UtcNow;
            await Task.Delay(wait > TimeSpan.Zero ? wait : TimeSpan.Zero, stoppingToken);
        }
    }

    // True if prices are current for the run on `due` (refreshing them if needed), false if the refresh failed.
    private async Task<bool> TryRefreshAsync(DateOnly due, CancellationToken ct)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var watchlist = scope.ServiceProvider.GetRequiredService<WatchlistService>();
            var prices = scope.ServiceProvider.GetRequiredService<PriceDataService>();

            var symbols = await watchlist.GetAllSymbolsAsync(ct);
            if (symbols.Count == 0)
            {
                return true;
            }

            // Skip if someone already refreshed after the close. A bar newer than `due` means the whole history,
            // including the final `due` bar, was downloaded after that close.
            var latest = await prices.GetLatestAsync(symbols, ct);
            if (latest.Count == symbols.Count && latest.All(p => p.Date > due || (p.Date == due && SignalEngine.IsFinal(p))))
            {
                logger.LogInformation("Prices are already up to date for {Date}; skipping scheduled refresh", due);
                return true;
            }

            await prices.RefreshAsync(symbols, ct);
            logger.LogInformation("Scheduled price refresh for {Date} finished ({Count} symbols)", due, symbols.Count);
            return true;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // Unhandled exceptions in a BackgroundService stop the host, so log and retry instead.
            logger.LogError(ex, "Scheduled price refresh for {Date} failed", due);
            return false;
        }
    }

    private static DateOnly LastRunDay(DateTime nowUtc, TimeOnly runAt)
    {
        var day = MarketDate(nowUtc);
        while (!IsWeekday(day) || ScheduledUtc(day, runAt) > nowUtc)
        {
            day = day.AddDays(-1);
        }
        return day;
    }

    private static DateOnly NextRunDay(DateTime nowUtc, TimeOnly runAt)
    {
        var day = MarketDate(nowUtc);
        while (!IsWeekday(day) || ScheduledUtc(day, runAt) <= nowUtc)
        {
            day = day.AddDays(1);
        }
        return day;
    }

    private static DateOnly MarketDate(DateTime utc) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(utc, SignalEngine.MarketTimeZone));

    // Eastern wall-clock time to UTC, so the schedule follows daylight saving time.
    private static DateTime ScheduledUtc(DateOnly day, TimeOnly runAt) =>
        TimeZoneInfo.ConvertTimeToUtc(day.ToDateTime(runAt), SignalEngine.MarketTimeZone);

    private static bool IsWeekday(DateOnly day) => day.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday);
}
