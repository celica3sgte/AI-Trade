using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;

namespace AITrade.Data;

/// <summary>A user's Backtest page settings, saved on each run so the Watchlist and stock pages signal with the same rules.</summary>
public class SignalPreference
{
    [Key]
    public string UserId { get; set; } = default!;

    public ApplicationUser User { get; set; } = default!;

    // Score needed to act (buy at +Threshold, sell at -Threshold).
    public double Threshold { get; set; }

    // Comma-separated Strategy names, so reordering the enum doesn't change saved choices.
    [Required, MaxLength(200)]
    public string EnabledStrategies { get; set; } = default!;

    [Precision(18, 2)]
    public decimal StartingCash { get; set; }

    [Precision(9, 2)]
    public decimal CostBps { get; set; }

    public DateTime UpdatedAtUtc { get; set; }
}
