using AITrade.Data;
using Microsoft.Extensions.Options;

namespace AITrade.Market;

/// <summary>
/// Keeps the Stocks table (the Watchlist's symbol search) in sync with Alpaca. Loads it on first startup, then
/// refreshes it once a day so new listings show up and delisted ones drop out. Runs only while the app is running.
/// </summary>
public sealed class StockListRefresher(
    IServiceScopeFactory scopeFactory,
    IOptions<AlpacaOptions> alpacaOptions,
    ILogger<StockListRefresher> logger) : BackgroundService
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromDays(1);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!alpacaOptions.Value.IsConfigured)
        {
            logger.LogWarning("Stock list refresh is off because Alpaca API keys aren't configured");
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            TimeSpan wait;
            try
            {
                using var scope = scopeFactory.CreateScope();
                var stocks = scope.ServiceProvider.GetRequiredService<StockListService>();

                // Based on the stored refresh time, so restarting the app doesn't re-download a current list.
                var last = await stocks.GetLastRefreshedAsync(stoppingToken);
                if (last is null || DateTime.UtcNow - last.Value >= RefreshInterval)
                {
                    var count = await stocks.RefreshAsync(stoppingToken);
                    logger.LogInformation("Refreshed stock list from Alpaca ({Count} stocks)", count);
                    wait = RefreshInterval;
                }
                else
                {
                    wait = last.Value + RefreshInterval - DateTime.UtcNow;
                }
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                // Unhandled exceptions in a BackgroundService stop the host, so log and retry instead.
                logger.LogError(ex, "Stock list refresh failed; retrying in {Delay}", RetryDelay);
                wait = RetryDelay;
            }

            await Task.Delay(wait, stoppingToken);
        }
    }
}
