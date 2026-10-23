using AITrade.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AITrade.Market;

public sealed record PriceSummary(string Symbol, DateOnly Date, decimal Close, decimal? PreviousClose)
{
    /// <summary>Latest and prior close from a history ordered oldest first; null if empty.</summary>
    public static PriceSummary? FromHistory(IReadOnlyList<DailyPrice> history) =>
        history.Count == 0 ? null
            : new(history[^1].Symbol, history[^1].Date, history[^1].Close, history.Count > 1 ? history[^2].Close : null);

    public decimal? Change => PreviousClose is decimal prev ? Close - prev : null;

    public decimal? ChangePercent => PreviousClose is decimal prev && prev != 0 ? (Close - prev) / prev : null;
}

public class PriceDataService(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    AlpacaClient alpaca,
    IOptions<AlpacaOptions> options,
    ILogger<PriceDataService> logger)
{
    // Alpaca returns at most 10,000 bars per page (pagination handles more, but one page per batch is cheapest).
    private const int BarsPerPage = 10_000;

    private readonly AlpacaOptions opts = options.Value;

    // Size batches so each request fits in about one page: ~252 trading days per 365 calendar days.
    private int SymbolsPerRequest => Math.Clamp(BarsPerPage / (opts.HistoryDays * 252 / 365 + 10), 1, 50);

    /// <summary>
    /// Re-downloads the full history window for each symbol and upserts it. Re-fetching everything (rather than
    /// only new days) keeps stored prices consistent after splits/dividends, since Alpaca adjusts the whole series.
    /// </summary>
    public async Task RefreshAsync(IEnumerable<string> symbols, CancellationToken ct = default)
    {
        var list = symbols.Select(WatchlistService.NormalizeSymbol).Distinct().ToList();
        if (list.Count == 0)
        {
            return;
        }

        var start = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-opts.HistoryDays));
        // Without a paid plan, SIP data must be at least 15 minutes old.
        var end = DateTimeOffset.UtcNow.AddMinutes(-16);

        foreach (var batch in list.Chunk(SymbolsPerRequest))
        {
            var bars = await alpaca.GetDailyBarsAsync(batch, start, end, ct);
            await SaveAsync(batch, bars, start, ct);
            logger.LogInformation("Refreshed daily prices for {Symbols} ({BarCount} bars)",
                string.Join(',', batch), bars.Values.Sum(b => b.Count));
        }
    }

    /// <summary>All stored daily prices for a symbol, oldest first.</summary>
    public async Task<List<DailyPrice>> GetHistoryAsync(string symbol, CancellationToken ct = default)
    {
        symbol = WatchlistService.NormalizeSymbol(symbol);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.DailyPrices
            .AsNoTracking()
            .Where(p => p.Symbol == symbol)
            .OrderBy(p => p.Date)
            .ToListAsync(ct);
    }

    /// <summary>All stored daily prices for each symbol, oldest first, in one query.</summary>
    public async Task<Dictionary<string, List<DailyPrice>>> GetHistoriesAsync(IEnumerable<string> symbols, CancellationToken ct = default)
    {
        var list = symbols.Select(WatchlistService.NormalizeSymbol).Distinct().ToList();
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var rows = await db.DailyPrices
            .AsNoTracking()
            .Where(p => list.Contains(p.Symbol))
            .OrderBy(p => p.Symbol).ThenBy(p => p.Date)
            .ToListAsync(ct);

        return rows.GroupBy(p => p.Symbol)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The most recent stored bar for each symbol; symbols with no stored prices are left out.</summary>
    public async Task<List<DailyPrice>> GetLatestAsync(IEnumerable<string> symbols, CancellationToken ct = default)
    {
        var list = symbols.Select(WatchlistService.NormalizeSymbol).Distinct().ToList();
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.DailyPrices
            .AsNoTracking()
            .Where(p => list.Contains(p.Symbol))
            .GroupBy(p => p.Symbol)
            .Select(g => g.OrderByDescending(p => p.Date).First())
            .ToListAsync(ct);
    }

    private async Task SaveAsync(string[] symbols, Dictionary<string, List<AlpacaBar>> bars, DateOnly start, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var now = DateTime.UtcNow;

        var existing = await db.DailyPrices.Where(p => symbols.Contains(p.Symbol)).ToListAsync(ct);

        // Rows outside the window would no longer get adjustment updates, so drop them.
        db.DailyPrices.RemoveRange(existing.Where(p => p.Date < start));
        var lookup = existing.Where(p => p.Date >= start).ToDictionary(p => (p.Symbol, p.Date));

        foreach (var (symbol, symbolBars) in bars)
        {
            var normalized = WatchlistService.NormalizeSymbol(symbol);
            foreach (var bar in symbolBars)
            {
                var date = AlpacaClient.ToMarketDate(bar.Timestamp);
                if (!lookup.TryGetValue((normalized, date), out var row))
                {
                    row = new DailyPrice { Symbol = normalized, Date = date };
                    db.DailyPrices.Add(row);
                    lookup[(normalized, date)] = row;
                }
                else if (Matches(row, bar))
                {
                    // Unchanged rows generate no SQL, so a typical refresh only writes the latest day.
                    continue;
                }

                row.Open = Round(bar.Open);
                row.High = Round(bar.High);
                row.Low = Round(bar.Low);
                row.Close = Round(bar.Close);
                row.Volume = bar.Volume;
                row.TradeCount = bar.TradeCount;
                row.Vwap = Round(bar.Vwap);
                row.UpdatedAtUtc = now;
            }
        }

        await db.SaveChangesAsync(ct);
    }

    // Prices are rounded to the stored precision (decimal(18,4)) before saving and comparing,
    // so extra decimals from the API never make an unchanged row look changed.
    private static bool Matches(DailyPrice row, AlpacaBar bar) =>
        row.Open == Round(bar.Open) &&
        row.High == Round(bar.High) &&
        row.Low == Round(bar.Low) &&
        row.Close == Round(bar.Close) &&
        row.Volume == bar.Volume &&
        row.TradeCount == bar.TradeCount &&
        row.Vwap == Round(bar.Vwap);

    private static decimal Round(decimal value) => Math.Round(value, 4, MidpointRounding.AwayFromZero);
}
