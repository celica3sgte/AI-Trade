using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace AITrade.Data;

public class ApplicationDbContext(DbContextOptions<ApplicationDbContext> options) : IdentityDbContext<ApplicationUser>(options)
{
    public DbSet<WatchlistItem> WatchlistItems => Set<WatchlistItem>();

    public DbSet<DailyPrice> DailyPrices => Set<DailyPrice>();

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
    }
}
