using AITrade.Market;
using Microsoft.EntityFrameworkCore;

namespace AITrade.Data;

public sealed record UserSignalSettings(SignalSettings Signal, BacktestOptions Backtest)
{
    public static UserSignalSettings Default { get; } = new(SignalSettings.Default, new BacktestOptions());
}

/// <summary>
/// Each user's Backtest page settings. Signals on the Watchlist and stock pages use them, so a Buy/Sell there is a
/// new marker on the backtest chart the user last ran. Users who haven't run a backtest get the defaults.
/// </summary>
public class SignalSettingsService(IDbContextFactory<ApplicationDbContext> dbFactory)
{
    public async Task<UserSignalSettings> GetAsync(string userId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var saved = await db.SignalPreferences.AsNoTracking().FirstOrDefaultAsync(p => p.UserId == userId);
        if (saved is null)
        {
            return UserSignalSettings.Default;
        }

        var enabled = saved.EnabledStrategies
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(name => Enum.TryParse<Strategy>(name, out var s) ? s : (Strategy?)null)
            .OfType<Strategy>()
            .ToHashSet();

        // Fall back to the defaults for anything a later code change made invalid (e.g. a removed strategy).
        var signal = new SignalSettings(
            saved.Threshold is > 0 and <= 1 ? saved.Threshold : SignalSettings.Default.Threshold,
            enabled.Count > 0 ? enabled : SignalSettings.Default.Enabled);
        return new UserSignalSettings(signal, new BacktestOptions(saved.StartingCash, saved.CostBps));
    }

    public async Task SaveAsync(string userId, UserSignalSettings settings)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var saved = await db.SignalPreferences.FirstOrDefaultAsync(p => p.UserId == userId);
        if (saved is null)
        {
            saved = new SignalPreference { UserId = userId };
            db.SignalPreferences.Add(saved);
        }

        saved.Threshold = settings.Signal.Threshold;
        saved.EnabledStrategies = string.Join(",", settings.Signal.Enabled.Order());
        saved.StartingCash = settings.Backtest.StartingCash;
        saved.CostBps = settings.Backtest.CostBps;
        saved.UpdatedAtUtc = DateTime.UtcNow;

        try
        {
            await db.SaveChangesAsync();
        }
        catch (DbUpdateException) when (db.Entry(saved).State == EntityState.Added)
        {
            // Another tab inserted the row first; overwrite it with these settings.
            db.Entry(saved).State = EntityState.Modified;
            await db.SaveChangesAsync();
        }
    }
}
