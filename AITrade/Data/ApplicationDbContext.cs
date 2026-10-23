using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace AITrade.Data;

public class ApplicationDbContext(DbContextOptions<ApplicationDbContext> options) : IdentityDbContext<ApplicationUser>(options)
{
    public DbSet<WatchlistItem> WatchlistItems => Set<WatchlistItem>();

    public DbSet<DailyPrice> DailyPrices => Set<DailyPrice>();

    public DbSet<SignalPreference> SignalPreferences => Set<SignalPreference>();

    public DbSet<Stock> Stocks => Set<Stock>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<WatchlistItem>(entity =>
        {
            entity.HasIndex(w => new { w.UserId, w.Symbol }).IsUnique();
            entity.HasOne(w => w.User)
                .WithMany()
                .HasForeignKey(w => w.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<DailyPrice>(entity =>
        {
            entity.HasKey(p => new { p.Symbol, p.Date });
        });

        builder.Entity<SignalPreference>(entity =>
        {
            entity.HasOne(p => p.User)
                .WithOne()
                .HasForeignKey<SignalPreference>(p => p.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
