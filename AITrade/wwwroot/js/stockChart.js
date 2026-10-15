// Renders a price chart with indicator panes using TradingView Lightweight Charts v5 (Apache 2.0).
// Docs: https://tradingview.github.io/lightweight-charts/docs

const LIB_URL = '/lib/lightweight-charts/lightweight-charts.standalone.production.js';
const charts = new Map();
let libPromise;

function loadLibrary() {
    if (window.LightweightCharts) {
        return Promise.resolve();
    }
    libPromise ??= new Promise((resolve, reject) => {
        const script = document.createElement('script');
        script.src = LIB_URL;
        script.onload = resolve;
        script.onerror = () => reject(new Error('Failed to load the charting library.'));
        document.head.appendChild(script);
    });
    return libPromise;
}

const toPoints = (times, values) => {
    const points = [];
    for (let i = 0; i < times.length; i++) {
        if (values[i] !== null && values[i] !== undefined) {
            points.push({ time: times[i], value: values[i] });
        }
    }
    return points;
};

export async function render(element, data) {
    await loadLibrary();
    dispose(element);

    const LC = window.LightweightCharts;
    const chart = LC.createChart(element, {
        autoSize: true,
        layout: { attributionLogo: true }, // Required attribution for TradingView Lightweight Charts.
        rightPriceScale: { borderVisible: false },
        timeScale: { borderVisible: false },
        crosshair: { mode: LC.CrosshairMode.Normal },
    });

    const t = data.times;
    const line = (color, extra = {}) => ({ color, lineWidth: 1, priceLineVisible: false, lastValueVisible: false, ...extra });

    // Pane 0: price, moving averages, Bollinger Bands.
    const candles = chart.addSeries(LC.CandlestickSeries, {
        upColor: '#198754', downColor: '#dc3545', borderVisible: false,
        wickUpColor: '#198754', wickDownColor: '#dc3545',
    });
    candles.setData(t.map((time, i) => ({ time, open: data.open[i], high: data.high[i], low: data.low[i], close: data.close[i] })));

    chart.addSeries(LC.LineSeries, line('#0d6efd', { title: 'SMA 20' })).setData(toPoints(t, data.sma20));
    chart.addSeries(LC.LineSeries, line('#fd7e14', { title: 'SMA 50' })).setData(toPoints(t, data.sma50));
    chart.addSeries(LC.LineSeries, line('#6f42c1', { title: 'SMA 200' })).setData(toPoints(t, data.sma200));
    chart.addSeries(LC.LineSeries, line('#adb5bd', { lineStyle: LC.LineStyle.Dashed })).setData(toPoints(t, data.bbUpper));
    chart.addSeries(LC.LineSeries, line('#adb5bd', { lineStyle: LC.LineStyle.Dashed })).setData(toPoints(t, data.bbLower));

    // ML.NET forecast: starts at the last close and extends into the next few (approximate) trading days.
    if (data.forecastTimes && data.forecastTimes.length) {
        const lastTime = t[t.length - 1];
        const lastClose = data.close[data.close.length - 1];
        const ahead = (values) => [{ time: lastTime, value: lastClose }, ...toPoints(data.forecastTimes, values)];
        chart.addSeries(LC.LineSeries, line('#20c997', { lineWidth: 2, lineStyle: LC.LineStyle.Dashed, title: 'Forecast', lastValueVisible: true }))
            .setData(ahead(data.forecastValues));
        chart.addSeries(LC.LineSeries, line('#20c997', { lineStyle: LC.LineStyle.Dotted })).setData(ahead(data.forecastUpper));
        chart.addSeries(LC.LineSeries, line('#20c997', { lineStyle: LC.LineStyle.Dotted })).setData(ahead(data.forecastLower));
    }

    // Pane 1: RSI with the conventional 70/30 levels.
    const rsi = chart.addSeries(LC.LineSeries, line('#6610f2', { title: 'RSI 14', lastValueVisible: true }), 1);
    rsi.setData(toPoints(t, data.rsi));
    rsi.createPriceLine({ price: 70, color: '#dc3545', lineWidth: 1, lineStyle: LC.LineStyle.Dotted, axisLabelVisible: true });
    rsi.createPriceLine({ price: 30, color: '#198754', lineWidth: 1, lineStyle: LC.LineStyle.Dotted, axisLabelVisible: true });

    // Pane 2: MACD histogram, MACD line and signal line.
    const hist = chart.addSeries(LC.HistogramSeries, { priceLineVisible: false, lastValueVisible: false }, 2);
    hist.setData(t.flatMap((time, i) => data.macdHist[i] === null ? [] :
        [{ time, value: data.macdHist[i], color: data.macdHist[i] >= 0 ? 'rgba(25,135,84,0.5)' : 'rgba(220,53,69,0.5)' }]));
    chart.addSeries(LC.LineSeries, line('#0d6efd', { title: 'MACD' }), 2).setData(toPoints(t, data.macd));
    chart.addSeries(LC.LineSeries, line('#fd7e14', { title: 'Signal' }), 2).setData(toPoints(t, data.macdSignal));

    const panes = chart.panes();
    panes[1]?.setHeight(110);
    panes[2]?.setHeight(130);

    // Range buttons end at the last forecast day so the forecast stays in view.
    const lastTime = data.forecastTimes?.length ? data.forecastTimes[data.forecastTimes.length - 1] : t[t.length - 1];
    charts.set(element, { chart, times: t, lastTime });
    setRange(element, data.initialMonths);
}

// Backtest equity curves (strategy vs buy and hold) with buy/sell markers on the strategy line.
export async function renderEquity(element, data) {
    await loadLibrary();
    dispose(element);

    const LC = window.LightweightCharts;
    const chart = LC.createChart(element, {
        autoSize: true,
        layout: { attributionLogo: true }, // Required attribution for TradingView Lightweight Charts.
        rightPriceScale: { borderVisible: false },
        timeScale: { borderVisible: false },
        localization: { priceFormatter: p => '$' + p.toLocaleString(undefined, { maximumFractionDigits: 0 }) },
    });

    const strategy = chart.addSeries(LC.LineSeries, { color: '#0d6efd', lineWidth: 2, title: 'Strategy', priceLineVisible: false });
    strategy.setData(toPoints(data.times, data.strategy));
    chart.addSeries(LC.LineSeries, { color: '#adb5bd', lineWidth: 2, title: 'Buy & hold', priceLineVisible: false })
        .setData(toPoints(data.times, data.buyHold));

    LC.createSeriesMarkers(strategy, data.trades.map(t => t.side === 'Buy'
        ? { time: t.time, position: 'belowBar', color: '#198754', shape: 'arrowUp', text: 'Buy' }
        : { time: t.time, position: 'aboveBar', color: '#dc3545', shape: 'arrowDown', text: 'Sell' }));

    chart.timeScale().fitContent();
    charts.set(element, { chart, times: data.times });
}

export function setRange(element, months) {
    const entry = charts.get(element);
    if (!entry || entry.times.length === 0) {
        return;
    }
    const last = entry.lastTime ?? entry.times[entry.times.length - 1];
    if (!months) {
        entry.chart.timeScale().fitContent();
        return;
    }
    const from = new Date(last + 'T00:00:00Z');
    from.setUTCMonth(from.getUTCMonth() - months);
    entry.chart.timeScale().setVisibleRange({ from: from.toISOString().slice(0, 10), to: last });
}

export function dispose(element) {
    const entry = charts.get(element);
    if (entry) {
        entry.chart.remove();
        charts.delete(element);
    }
}
