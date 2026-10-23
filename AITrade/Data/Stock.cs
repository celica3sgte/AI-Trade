using System.ComponentModel.DataAnnotations;

namespace AITrade.Data;

// An active, tradable US stock from Alpaca's asset list, shared by all users. Feeds the Watchlist symbol search.
public class Stock
{
    [Key, MaxLength(10)]
    public string Symbol { get; set; } = default!;

    [Required, MaxLength(200)]
    public string Name { get; set; } = default!;

    [MaxLength(20)]
    public string? Exchange { get; set; }

    // When the list was last downloaded from Alpaca; every row gets the same value on each refresh.
    public DateTime RefreshedAtUtc { get; set; }
}
