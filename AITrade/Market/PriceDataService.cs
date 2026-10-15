using AITrade.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AITrade.Market;

public sealed record PriceSummary(string Symbol, DateOnly Date, decimal Close, decimal? PreviousClose)
{
    public decimal? Change => PreviousClose is decimal prev ? Close - prev : null;

    public decimal? ChangePercent => PreviousClose is decimal prev && prev != 0 ? (Close - prev) / prev : null;
}

public class PriceDataService(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    AlpacaClient alpaca,
    IOptions<AlpacaOptions> options,
    ILogger<PriceDataService> logger)
{
    // ~500 trading days per symbol at the default history length keeps each batch near one 10,000-bar page.
    private const int SymbolsPerRequest = 20;

    private readonly AlpacaOptions opts = options.Value;

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

    /// <summary>Latest close and the prior close for each symbol that has stored prices.</summary>
    public async Task<Dictionary<string, PriceSummary>> GetLatestAsync(IEnumerable<string> symbols, CancellationToken ct = default)
    {
        var list = symbols.Distinct().ToList();
        // Two weeks comfortably covers the last two trading days across weekends and holidays.
        var since = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-14));

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var recent = await db.DailyPrices
            .AsNoTracking()
            .Where(p => list.Contains(p.Symbol) && p.Date >= since)
            .Select(p => new { p.Symbol, p.Date, p.Close })
            .ToListAsync(ct);

        return recent
            .GroupBy(p => p.Symbol)
            .Select(g =>
            {
                var lastTwo = g.OrderByDescending(p => p.Date).Take(2).ToList();
                return new PriceSummary(g.Key, lastTwo[0].Date, lastTwo[0].Close, lastTwo.Count > 1 ? lastTwo[1].Close : null);
            })
            .ToDictionary(s => s.Symbol, StringComparer.OrdinalIgnoreCase);
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
