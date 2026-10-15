using System.Globalization;
using System.Net;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace AITrade.Market;

// Thin wrapper over the Alpaca REST APIs.
// Docs: https://docs.alpaca.markets/us/reference/stockbars and https://docs.alpaca.markets/us/reference/get-v2-assets-symbol_or_asset_id
public partial class AlpacaClient(HttpClient http, IOptions<AlpacaOptions> options)
{
    private static readonly TimeZoneInfo MarketTimeZone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

    private readonly AlpacaOptions opts = options.Value;

    /// <summary>Returns the asset, or null if Alpaca doesn't know the symbol.</summary>
    public async Task<AlpacaAsset?> GetAssetAsync(string symbol, CancellationToken ct = default)
    {
        var url = $"{ApiRoot(opts.TradingBaseUrl)}/v2/assets/{Uri.EscapeDataString(symbol)}";
        using var request = CreateRequest(url);
        using var response = await http.SendAsync(request, ct);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        await EnsureSuccessAsync(response, ct);
        return await response.Content.ReadFromJsonAsync<AlpacaAsset>(ct);
    }

    /// <summary>Gets split- and dividend-adjusted daily bars for the symbols, following pagination.</summary>
    public async Task<Dictionary<string, List<AlpacaBar>>> GetDailyBarsAsync(
        IReadOnlyCollection<string> symbols, DateOnly start, DateTimeOffset end, CancellationToken ct = default)
    {
        var result = new Dictionary<string, List<AlpacaBar>>(StringComparer.OrdinalIgnoreCase);
        string? pageToken = null;

        do
        {
            var url = $"{ApiRoot(opts.DataBaseUrl)}/v2/stocks/bars" +
                $"?symbols={Uri.EscapeDataString(string.Join(',', symbols))}" +
                "&timeframe=1Day" +
                $"&start={start:yyyy-MM-dd}" +
                $"&end={Uri.EscapeDataString(end.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture))}" +
                "&adjustment=all" +
                $"&feed={Uri.EscapeDataString(opts.Feed)}" +
                "&limit=10000";
            if (pageToken is not null)
            {
                url += $"&page_token={Uri.EscapeDataString(pageToken)}";
            }

            using var request = CreateRequest(url);
            using var response = await http.SendAsync(request, ct);
            await EnsureSuccessAsync(response, ct);

            var page = await response.Content.ReadFromJsonAsync<AlpacaBarsResponse>(ct)
                ?? throw new AlpacaApiException(response.StatusCode, "Empty response body.");

            foreach (var (symbol, bars) in page.Bars ?? [])
            {
                if (!result.TryGetValue(symbol, out var list))
                {
                    result[symbol] = list = [];
                }
                list.AddRange(bars);
            }

            pageToken = page.NextPageToken;
        }
        while (!string.IsNullOrEmpty(pageToken));

        return result;
    }

    /// <summary>Converts a daily bar timestamp to the US market (Eastern) trading date.</summary>
    public static DateOnly ToMarketDate(string timestamp)
    {
        // Alpaca timestamps can carry nanoseconds; .NET parses at most 7 fractional digits.
        var trimmed = ExtraFractionDigits().Replace(timestamp, "$1");
        var utc = DateTimeOffset.Parse(trimmed, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal);
        return DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(utc, MarketTimeZone).DateTime);
    }

    // Accepts base URLs with or without a trailing "/v2" (Alpaca's dashboard shows the endpoint with it).
    private static string ApiRoot(string baseUrl)
    {
        var root = baseUrl.TrimEnd('/');
        return root.EndsWith("/v2", StringComparison.OrdinalIgnoreCase) ? root[..^3] : root;
    }

    private HttpRequestMessage CreateRequest(string url)
    {
        if (!opts.IsConfigured)
        {
            throw new InvalidOperationException("Alpaca API keys are not configured. Set Alpaca:KeyId and Alpaca:SecretKey.");
        }

        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("APCA-API-KEY-ID", opts.KeyId);
        request.Headers.Add("APCA-API-SECRET-KEY", opts.SecretKey);
        return request;
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            throw new AlpacaApiException(response.StatusCode, body);
        }
    }

    [GeneratedRegex(@"(\.\d{7})\d+")]
    private static partial Regex ExtraFractionDigits();
}

public class AlpacaApiException(HttpStatusCode statusCode, string body)
    : Exception($"Alpaca API returned {(int)statusCode} {statusCode}: {body}")
{
    public HttpStatusCode StatusCode { get; } = statusCode;
}

public sealed record AlpacaAsset(
    [property: JsonPropertyName("symbol")] string Symbol,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("exchange")] string? Exchange,
    [property: JsonPropertyName("class")] string? AssetClass,
    [property: JsonPropertyName("status")] string? Status,
    [property: JsonPropertyName("tradable")] bool Tradable);

public sealed record AlpacaBar(
    [property: JsonPropertyName("t")] string Timestamp,
    [property: JsonPropertyName("o")] decimal Open,
    [property: JsonPropertyName("h")] decimal High,
    [property: JsonPropertyName("l")] decimal Low,
    [property: JsonPropertyName("c")] decimal Close,
    [property: JsonPropertyName("v")] long Volume,
    [property: JsonPropertyName("n")] long TradeCount,
    [property: JsonPropertyName("vw")] decimal Vwap);

public sealed record AlpacaBarsResponse(
    [property: JsonPropertyName("bars")] Dictionary<string, List<AlpacaBar>>? Bars,
    [property: JsonPropertyName("next_page_token")] string? NextPageToken);
