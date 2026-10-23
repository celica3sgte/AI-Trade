namespace AITrade.Market;

public sealed class PriceRefreshOptions
{
    public const string SectionName = "PriceRefresh";

    // Turn off to rely on the Watchlist's Refresh prices button only.
    public bool Enabled { get; set; } = true;

    // US Eastern time, Monday to Friday. Runs set earlier than SignalEngine.BarFinalAt (4:16 pm) are moved to it,
    // since the day's bar isn't final until then.
    public TimeOnly RunAt { get; set; } = new(16, 30);
}
