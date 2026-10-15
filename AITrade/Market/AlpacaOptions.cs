namespace AITrade.Market;

public sealed class AlpacaOptions
{
    public const string SectionName = "Alpaca";

    public string KeyId { get; set; } = "";

    public string SecretKey { get; set; } = "";

    // Market data API (historical bars).
    public string DataBaseUrl { get; set; } = "https://data.alpaca.markets";

    // Trading API, used for asset (symbol) lookups. Must match the key type: paper keys only work with paper-api.
    public string TradingBaseUrl { get; set; } = "https://paper-api.alpaca.markets";

    // "sip" = all US exchanges. Without a paid subscription, SIP queries must end at least 15 minutes ago.
    public string Feed { get; set; } = "sip";

    // How much daily history to keep per symbol (calendar days). Alpaca's SIP daily bars go back to 2016.
    public int HistoryDays { get; set; } = 3650;

    public bool IsConfigured => IsSet(KeyId) && IsSet(SecretKey);

    // Treats blank values and "<placeholder>" values as not set.
    private static bool IsSet(string value) => !string.IsNullOrWhiteSpace(value) && !value.TrimStart().StartsWith('<');
}
