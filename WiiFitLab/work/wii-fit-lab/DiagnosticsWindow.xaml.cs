using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using BalanceBoard.Core.Models;

namespace WiiFitLab;

public partial class DiagnosticsWindow : Window
{
    private const int MaxSamples = 1200;
    private readonly List<GraphSample> _samples = [];
    private long _lastGraphSampleAt;

    public DiagnosticsWindow()
    {
        InitializeComponent();
        GraphCanvas.SizeChanged += (_, _) => RedrawGraph();
    }

    public void Update(ProcessedBalance data, double reportRateHz)
    {
        ReportRateText.Text = reportRateHz > 0 ? $"{reportRateHz:0.0} Hz" : "計測中…";
        SetSensorText(TopLeftRawText, TopLeftCalibrationText, data.TopLeftSensor);
        SetSensorText(TopRightRawText, TopRightCalibrationText, data.TopRightSensor);
        SetSensorText(BottomLeftRawText, BottomLeftCalibrationText, data.BottomLeftSensor);
        SetSensorText(BottomRightRawText, BottomRightCalibrationText, data.BottomRightSensor);

        var now = Stopwatch.GetTimestamp();
        if (_lastGraphSampleAt != 0 && Stopwatch.GetElapsedTime(_lastGraphSampleAt, now).TotalMilliseconds < 50)
        {
            return;
        }

        _lastGraphSampleAt = now;
        _samples.Add(new GraphSample(
            Math.Max(0, data.TemperatureCorrectedWeightKg),
            Math.Clamp(data.BalanceX, 0, 100),
            Math.Clamp(data.BalanceY, 0, 100),
            data.TemperatureRaw));
        if (_samples.Count > MaxSamples)
        {
            _samples.RemoveRange(0, _samples.Count - MaxSamples);
        }

        RedrawGraph();
    }

    private static void SetSensorText(
        System.Windows.Controls.TextBlock rawText,
        System.Windows.Controls.TextBlock calibrationText,
        LoadSensorTelemetry sensor)
    {
        rawText.Text = $"生値 {sensor.Raw:N0}";
        var lowCountsPerKg = (sensor.Calibration17KgRaw - sensor.Calibration0KgRaw) / 17.0;
        var highCountsPerKg = (sensor.Calibration34KgRaw - sensor.Calibration17KgRaw) / 17.0;
        calibrationText.Text =
            $"0 kg   {sensor.Calibration0KgRaw:N0}\n" +
            $"17 kg  {sensor.Calibration17KgRaw:N0}\n" +
            $"34 kg  {sensor.Calibration34KgRaw:N0}\n" +
            $"感度   {lowCountsPerKg:0}/{highCountsPerKg:0} count/kg";
    }

    private void RedrawGraph()
    {
        var width = GraphCanvas.ActualWidth;
        var height = GraphCanvas.ActualHeight;
        if (width <= 1 || height <= 1 || _samples.Count == 0)
        {
            return;
        }

        WeightLine.Points = ToPoints(sample => sample.WeightKg, 150, width, height);
        BalanceXLine.Points = ToPoints(sample => sample.BalanceX, 100, width, height);
        BalanceYLine.Points = ToPoints(sample => sample.BalanceY, 100, width, height);
        TemperatureLine.Points = ToPoints(sample => sample.TemperatureRaw, 50, width, height);
    }

    private PointCollection ToPoints(Func<GraphSample, double> selector, double maximum, double width, double height)
    {
        var points = new PointCollection(_samples.Count);
        var denominator = Math.Max(1, MaxSamples - 1);
        for (var index = 0; index < _samples.Count; index++)
        {
            var x = width - ((_samples.Count - 1 - index) / (double)denominator * width);
            var normalized = Math.Clamp(selector(_samples[index]) / maximum, 0, 1);
            points.Add(new Point(x, height - normalized * height));
        }

        return points;
    }

    private sealed record GraphSample(double WeightKg, double BalanceX, double BalanceY, double TemperatureRaw);
}
