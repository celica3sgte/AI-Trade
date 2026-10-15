using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;

namespace AITrade.Data;

// One adjusted daily bar per symbol, shared by all users. Key is (Symbol, Date).
public class DailyPrice
{
    [MaxLength(10)]
    public string Symbol { get; set; } = default!;

    // US market (Eastern) trading date.
    public DateOnly Date { get; set; }

    [Precision(18, 4)]
    public decimal Open { get; set; }

    [Precision(18, 4)]
    public decimal High { get; set; }

    [Precision(18, 4)]
    public decimal Low { get; set; }

    [Precision(18, 4)]
    public decimal Close { get; set; }

    public long Volume { get; set; }

    public long TradeCount { get; set; }

    [Precision(18, 4)]
    public decimal Vwap { get; set; }

    public DateTime UpdatedAtUtc { get; set; }
}
