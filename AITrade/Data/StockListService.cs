using System.Text.RegularExpressions;
using AITrade.Market;
using Microsoft.EntityFrameworkCore;

namespace AITrade.Data;

public partial class StockListService(IDbContextFactory<ApplicationDbContext> dbFactory, AlpacaClient alpaca)
{
    private const int MaxQueryLength = 50;

    // One refresh at a time across the app (the scheduled one and the Watchlist button), so two can't insert the
    // same new symbol.
    private static readonly SemaphoreSlim RefreshLock = new(1, 1);

    /// <summary>When the stock list was last downloaded, or null if it never has been.</summary>
    public async Task<DateTime?> GetLastRefreshedAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Stocks.MaxAsync(s => (DateTime?)s.RefreshedAtUtc, ct);
    }

    /// <summary>True once the stock list has been downloaded at least once.</summary>
    public async Task<bool> IsLoadedAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Stocks.AnyAsync(ct);
    }

    /// <summary>
    /// Downloads Alpaca's active US equities and syncs the table: adds new listings, updates renamed ones and drops
    /// delisted ones. Keeps only symbols the Watchlist would accept (tradable, and a shape its form allows).
    /// Returns the number of stocks stored.
    /// </summary>
    public async Task<int> RefreshAsync(CancellationToken ct = default)
    {
        await RefreshLock.WaitAsync(ct);
        try
        {
            return await RefreshCoreAsync(ct);
        }
        finally
        {
            RefreshLock.Release();
        }
    }

    private async Task<int> RefreshCoreAsync(CancellationToken ct)
    {
        var assets = await alpaca.GetActiveUsEquitiesAsync(ct);
        var latest = new Dictionary<string, AlpacaAsset>(StringComparer.OrdinalIgnoreCase);
        foreach (var asset in assets)
        {
            if (asset.Tradable && SymbolShape().IsMatch(asset.Symbol))
            {
                latest.TryAdd(WatchlistService.NormalizeSymbol(asset.Symbol), asset);
            }
        }

        // Don't wipe the list if Alpaca ever returns nothing usable.
        if (latest.Count == 0)
        {
            throw new InvalidOperationException("Alpaca returned no tradable US stocks; keeping the existing list.");
        }

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var now = DateTime.UtcNow;
        var existing = await db.Stocks.ToDictionaryAsync(s => s.Symbol, StringComparer.OrdinalIgnoreCase, ct);

        db.Stocks.RemoveRange(existing.Values.Where(s => !latest.ContainsKey(s.Symbol)));

        foreach (var (symbol, asset) in latest)
        {
            var name = Truncate(asset.Name?.Trim() ?? "", 200);
            var exchange = asset.Exchange is { } ex ? Truncate(ex, 20) : null;
            if (!existing.TryGetValue(symbol, out var row))
            {
                db.Stocks.Add(new Stock { Symbol = symbol, Name = name, Exchange = exchange, RefreshedAtUtc = now });
            }
            else if (row.Name != name || row.Exchange != exchange)
            {
                row.Name = name;
                row.Exchange = exchange;
            }
        }

        await db.SaveChangesAsync(ct);

        // One statement instead of an UPDATE per unchanged row.
        await db.Stocks.ExecuteUpdateAsync(s => s.SetProperty(x => x.RefreshedAtUtc, now), ct);
        return latest.Count;
    }

    /// <summary>
    /// Stocks whose symbol starts with, or whose name contains, the query (case-insensitive under the database's
    /// default collation). Best matches first: exact symbol, symbol prefix, name prefix, then anywhere in the name.
    /// </summary>
    public async Task<List<Stock>> SearchAsync(string query, int limit, CancellationToken ct = default)
    {
        query = query.Trim();
        if (query.Length == 0 || query.Length > MaxQueryLength)
        {
            return [];
        }

        var symbol = WatchlistService.NormalizeSymbol(query);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Stocks
            .AsNoTracking()
            .Where(s => s.Symbol.StartsWith(symbol) || s.Name.Contains(query))
            .OrderBy(s => s.Symbol == symbol ? 0 : s.Symbol.StartsWith(symbol) ? 1 : s.Name.StartsWith(query) ? 2 : 3)
            .ThenBy(s => s.Symbol.Length)
            .ThenBy(s => s.Symbol)
            .Take(limit)
            .ToListAsync(ct);
    }

    private static string Truncate(string value, int maxLength) => value.Length > maxLength ? value[..maxLength] : value;

    // Same symbol shape the Watchlist form accepts (e.g. MSFT, BRK.B), so every suggestion can be added.
    [GeneratedRegex(@"^[A-Za-z]{1,5}([.\-][A-Za-z]{1,2})?$")]
    private static partial Regex SymbolShape();
}
