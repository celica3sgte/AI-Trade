using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;

namespace AITrade.Data;

public class WatchlistItem
{
    public int Id { get; set; }

    [Required]
    public string UserId { get; set; } = default!;

    public ApplicationUser User { get; set; } = default!;

    [Required, MaxLength(10)]
    public string Symbol { get; set; } = default!;

    // Company name from Alpaca's asset lookup.
    [MaxLength(200)]
    public string? CompanyName { get; set; }

    [MaxLength(500)]
    public string? Notes { get; set; }

    // 0 means the stock is watched but not owned. Decimal to allow fractional shares.
    [Precision(18, 6)]
    public decimal SharesOwned { get; set; }

    // Average price paid per share; only meaningful when the stock is owned.
    [Precision(18, 4)]
    public decimal? AverageCostPerShare { get; set; }

    public bool IsOwned => SharesOwned > 0;

    public decimal? TotalCost => IsOwned && AverageCostPerShare is decimal cost ? cost * SharesOwned : null;

    public DateTime AddedAtUtc { get; set; }
}
