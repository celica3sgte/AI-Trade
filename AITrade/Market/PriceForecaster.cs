using Microsoft.ML;
using Microsoft.ML.Transforms.TimeSeries;

namespace AITrade.Market;

/// <summary>Forecast closes for the next <see cref="PriceForecaster.Horizon"/> trading days, with a 95% band.</summary>
public sealed record PriceForecast(double[] Values, double[] Lower, double[] Upper)
{
    /// <summary>Forecast change from <paramref name="lastClose"/> to the final forecast day, as a fraction.</summary>
    public double ChangeFrom(double lastClose) => lastClose == 0 ? 0 : Values[^1] / lastClose - 1;
}

/// <summary>
/// ML.NET Singular Spectrum Analysis (SSA) forecaster over daily closes. A fresh model is trained on the trailing
/// year of closes ending at the requested bar, so a forecast at bar i only uses data up to i (walk-forward).
/// Training takes about a millisecond per call. Parameters are reasonable starting points, not tuned values.
/// Docs: https://learn.microsoft.com/dotnet/api/microsoft.ml.timeseriescatalog.forecastbyssa
/// </summary>
public static class PriceForecaster
{
    public const int Horizon = 5;

    // About one trading year of training data.
    private const int TrainSize = 252;
    private const int WindowSize = 20;
    private const int SeriesLength = 60;

    // Minimum closes needed to train a meaningful model.
    public const int MinHistory = 100;

    public static PriceForecast? Forecast(IReadOnlyList<double> closes, int endIndex)
    {
        var count = Math.Min(TrainSize, endIndex + 1);
        if (count < MinHistory)
        {
            return null;
        }

        var ml = new MLContext(seed: 0);
        var points = new List<SeriesPoint>(count);
        for (var i = endIndex - count + 1; i <= endIndex; i++)
        {
            points.Add(new SeriesPoint { Value = (float)closes[i] });
        }

        var pipeline = ml.Forecasting.ForecastBySsa(
            outputColumnName: nameof(ForecastOutput.Forecast),
            inputColumnName: nameof(SeriesPoint.Value),
            windowSize: WindowSize,
            seriesLength: SeriesLength,
            trainSize: count,
            horizon: Horizon,
            confidenceLevel: 0.95f,
            confidenceLowerBoundColumn: nameof(ForecastOutput.Lower),
            confidenceUpperBoundColumn: nameof(ForecastOutput.Upper));

        var model = pipeline.Fit(ml.Data.LoadFromEnumerable(points));
        using var engine = model.CreateTimeSeriesEngine<SeriesPoint, ForecastOutput>(ml);
        var output = engine.Predict();

        return new PriceForecast(
            output.Forecast.Select(v => (double)v).ToArray(),
            output.Lower.Select(v => (double)v).ToArray(),
            output.Upper.Select(v => (double)v).ToArray());
    }

    // ML.NET requires public types with float columns for its schema.
    public sealed class SeriesPoint
    {
        public float Value { get; set; }
    }

    public sealed class ForecastOutput
    {
        public float[] Forecast { get; set; } = [];
        public float[] Lower { get; set; } = [];
        public float[] Upper { get; set; } = [];
    }
}
