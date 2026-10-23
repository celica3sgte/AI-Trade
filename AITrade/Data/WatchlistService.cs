using AITrade.Market;
using Microsoft.EntityFrameworkCore;

namespace AITrade.Data;

public enum AddWatchlistResult
{
    Added,
    AlreadyExists,
    NotFound,
    NotTradable
}

public sealed record AddWatchlistOutcome(AddWatchlistResult Result, string Symbol);

// Uses a DbContext factory so each operation gets a short-lived context,
// which is the recommended pattern for Blazor Server circuits.
public class WatchlistService(IDbContextFactory<ApplicationDbContext> dbFactory, AlpacaClient alpaca)
{
    public async Task<List<WatchlistItem>> GetForUserAsync(string userId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.WatchlistItems
            .AsNoTracking()
            .Where(w => w.UserId == userId)
            .OrderBy(w => w.Symbol)
            .ToListAsync();
    }

    /// <summary>Every symbol on any user's watchlist, without duplicates.</summary>
    public async Task<List<string>> GetAllSymbolsAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.WatchlistItems.Select(w => w.Symbol).Distinct().OrderBy(s => s).ToListAsync(ct);
    }

    /// <summary>
    /// Verifies the symbol with Alpaca, then adds it. Throws if Alpaca can't be reached or isn't configured.
    /// </summary>
    public async Task<AddWatchlistOutcome> AddAsync(string userId, string symbol, string? notes, decimal sharesOwned, decimal? averageCostPerShare)
    {
        symbol = NormalizeSymbol(symbol);

        await using var db = await dbFactory.CreateDbContextAsync();
        if (await db.WatchlistItems.AnyAsync(w => w.UserId == userId && w.Symbol == symbol))
        {
            return new(AddWatchlistResult.AlreadyExists, symbol);
        }

        var asset = await alpaca.GetAssetAsync(symbol);
        if (asset is null)
        {
            return new(AddWatchlistResult.NotFound, symbol);
        }
        if (!asset.Tradable || !string.Equals(asset.Status, "active", StringComparison.OrdinalIgnoreCase))
        {
            return new(AddWatchlistResult.NotTradable, symbol);
        }

        // Use Alpaca's canonical form (e.g. BRK-B -> BRK.B) and re-check for a duplicate under that name.
        var canonical = NormalizeSymbol(asset.Symbol);
        if (canonical != symbol && await db.WatchlistItems.AnyAsync(w => w.UserId == userId && w.Symbol == canonical))
        {
            return new(AddWatchlistResult.AlreadyExists, canonical);
        }
        symbol = canonical;

        db.WatchlistItems.Add(new WatchlistItem
        {
            UserId = userId,
            Symbol = symbol,
            CompanyName = asset.Name is { Length: > 200 } name ? name[..200] : asset.Name,
            Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim(),
            SharesOwned = sharesOwned,
            AverageCostPerShare = sharesOwned > 0 ? averageCostPerShare : null,
            AddedAtUtc = DateTime.UtcNow
        });

        try
        {
            await db.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            // Unique index on (UserId, Symbol) caught a concurrent duplicate insert.
            return new(AddWatchlistResult.AlreadyExists, symbol);
        }

        return new(AddWatchlistResult.Added, symbol);
    }

    public async Task UpdateHoldingAsync(string userId, int id, decimal sharesOwned, decimal? averageCostPerShare)
    {
        // Cost basis is cleared when the position is closed out.
        var cost = sharesOwned > 0 ? averageCostPerShare : null;

        await using var db = await dbFactory.CreateDbContextAsync();
        await db.WatchlistItems
            .Where(w => w.Id == id && w.UserId == userId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(w => w.SharesOwned, sharesOwned)
                .SetProperty(w => w.AverageCostPerShare, cost));
    }

    public async Task RemoveAsync(string userId, int id)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        await db.WatchlistItems
            .Where(w => w.Id == id && w.UserId == userId)
            .ExecuteDeleteAsync();
    }

    public static string NormalizeSymbol(string symbol) => symbol.Trim().ToUpperInvariant();
}
