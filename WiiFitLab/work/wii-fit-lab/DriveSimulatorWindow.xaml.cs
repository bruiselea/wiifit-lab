using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using BalanceBoard.Core.Models;
using BalanceBoard.Core.Processing;

namespace WiiFitLab;

public partial class DriveSimulatorWindow : Window
{
    private readonly DispatcherTimer _timer;
    private readonly List<Rectangle> _laneMarks = [];
    private DriveSimulationState _state;
    private long _lastFrameAt = Stopwatch.GetTimestamp();
    private long _lastBoardUpdateAt;
    private double _currentBrakeKg;
    private double _currentAcceleratorKg;
    private double _restBrakeKg;
    private double _restAcceleratorKg;
    private double _maximumBrakeKg;
    private double _maximumAcceleratorKg;
    private double _brake;
    private double _accelerator;
    private double _roadScroll;
    private bool _hasRest;
    private bool _hasBrakeMaximum;
    private bool _hasAcceleratorMaximum;
    private bool _running;
    private bool _steerLeft;
    private bool _steerRight;
    private bool _connected;

    public DriveSimulatorWindow()
    {
        InitializeComponent();
        CreateLaneMarks();
        RenderScene();
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Render, Timer_Tick, Dispatcher);
        _timer.Start();
        Deactivated += (_, _) =>
        {
            _steerLeft = false;
            _steerRight = false;
        };
        Closed += (_, _) => _timer.Stop();
    }

    public void SetConnectionState(bool connected)
    {
        _connected = connected;
        if (!connected)
        {
            Pause("ボード切断・走行を一時停止しました");
            SignalStatusText.Text = "切断中";
            SignalStatusText.Foreground = BrushFrom("#FF6B7A");
        }
    }

    public void Update(ProcessedBalance data)
    {
        _connected = true;
        _lastBoardUpdateAt = Stopwatch.GetTimestamp();
        (_currentBrakeKg, _currentAcceleratorKg) = PedalMath.ComputeSideLoads(data);
        SignalStatusText.Text = "信号受信中";
        SignalStatusText.Foreground = BrushFrom("#48D7C8");

        if (_hasRest && _hasBrakeMaximum && _hasAcceleratorMaximum)
        {
            var targetBrake = PedalMath.Normalize(_currentBrakeKg, _restBrakeKg, _maximumBrakeKg, 0.05, 1);
            var targetAccelerator = PedalMath.Normalize(
                _currentAcceleratorKg,
                _restAcceleratorKg,
                _maximumAcceleratorKg,
                0.05,
                1);
            const double smoothing = 0.30;
            _brake += (targetBrake - _brake) * smoothing;
            _accelerator += (targetAccelerator - _accelerator) * smoothing;
        }

        BrakeProgress.Value = _brake * 100;
        AcceleratorProgress.Value = _accelerator * 100;
        BrakeText.Text = $"{_brake * 100:0} %  /  {_currentBrakeKg:0.0} kg";
        AcceleratorText.Text = $"{_accelerator * 100:0} %  /  {_currentAcceleratorKg:0.0} kg";
    }

    private void Timer_Tick(object? sender, EventArgs e)
    {
        var now = Stopwatch.GetTimestamp();
        var elapsed = Stopwatch.GetElapsedTime(_lastFrameAt, now).TotalSeconds;
        _lastFrameAt = now;

        if (_lastBoardUpdateAt == 0 || Stopwatch.GetElapsedTime(_lastBoardUpdateAt, now).TotalMilliseconds > 250)
        {
            if (_running)
            {
                Pause("信号が途切れたため一時停止しました");
            }

            return;
        }

        if (!_running)
        {
            return;
        }

        var steering = (_steerRight ? 1.0 : 0) - (_steerLeft ? 1.0 : 0);
        _state = DriveSimulationMath.Step(_state, _accelerator, _brake, steering, elapsed);
        _roadScroll = (_roadScroll + _state.SpeedMetersPerSecond * elapsed * 18) % 100;
        RenderScene();
    }

    private void CreateLaneMarks()
    {
        for (var index = 0; index < 8; index++)
        {
            var mark = new Rectangle
            {
                Width = 8,
                Height = 55,
                Fill = Brushes.White,
                Opacity = 0.78,
            };
            Canvas.SetLeft(mark, 356);
            LaneMarksLayer.Children.Add(mark);
            _laneMarks.Add(mark);
        }
    }

    private void RenderScene()
    {
        const double pixelsPerMeter = 45;
        var carLeft = 360 + _state.LateralMeters * pixelsPerMeter - CarSprite.Width / 2;
        Canvas.SetLeft(CarSprite, carLeft);
        var steering = (_steerRight ? 1.0 : 0) - (_steerLeft ? 1.0 : 0);
        CarSprite.RenderTransform = new RotateTransform(steering * 7);

        for (var index = 0; index < _laneMarks.Count; index++)
        {
            var y = (index * 100 + _roadScroll) % 800 - 70;
            Canvas.SetTop(_laneMarks[index], y);
        }

        SpeedText.Text = (_state.SpeedMetersPerSecond * 3.6).ToString("0");
        DistanceText.Text = $"{_state.DistanceMeters / 1000:0.00} km";
        OffRoadBanner.Visibility = DriveSimulationMath.IsOffRoad(_state.LateralMeters)
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void CaptureRest_Click(object sender, RoutedEventArgs e)
    {
        _restBrakeKg = _currentBrakeKg;
        _restAcceleratorKg = _currentAcceleratorKg;
        _hasRest = true;
        _hasBrakeMaximum = false;
        _hasAcceleratorMaximum = false;
        _brake = 0;
        _accelerator = 0;
        Pause("基準を記録しました。次に左最大を記録してください");
        UpdateCalibrationText();
    }

    private void CaptureBrakeMax_Click(object sender, RoutedEventArgs e)
    {
        if (!_hasRest || _currentBrakeKg < _restBrakeKg + 1)
        {
            DriveStatusText.Text = _hasRest ? "左足を強く踏んでください" : "先に基準を記録してください";
            return;
        }

        _maximumBrakeKg = _currentBrakeKg;
        _hasBrakeMaximum = true;
        DriveStatusText.Text = "左最大を記録しました。次に右最大を記録してください";
        UpdateCalibrationText();
    }

    private void CaptureAcceleratorMax_Click(object sender, RoutedEventArgs e)
    {
        if (!_hasRest || _currentAcceleratorKg < _restAcceleratorKg + 1)
        {
            DriveStatusText.Text = _hasRest ? "右足を強く踏んでください" : "先に基準を記録してください";
            return;
        }

        _maximumAcceleratorKg = _currentAcceleratorKg;
        _hasAcceleratorMaximum = true;
        DriveStatusText.Text = "校正完了。走行開始できます";
        UpdateCalibrationText();
    }

    private void ResetCalibration_Click(object sender, RoutedEventArgs e)
    {
        _hasRest = false;
        _hasBrakeMaximum = false;
        _hasAcceleratorMaximum = false;
        _brake = 0;
        _accelerator = 0;
        Pause("校正をリセットしました");
        UpdateCalibrationText();
    }

    private void UpdateCalibrationText()
    {
        CalibrationText.Text =
            $"基準 {FormatCalibration(_hasRest, _restBrakeKg)} / {FormatCalibration(_hasRest, _restAcceleratorKg)} kg\n" +
            $"最大 {FormatCalibration(_hasBrakeMaximum, _maximumBrakeKg)} / {FormatCalibration(_hasAcceleratorMaximum, _maximumAcceleratorKg)} kg";
        StartButton.IsEnabled = _connected && _hasRest && _hasBrakeMaximum && _hasAcceleratorMaximum;
    }

    private void StartButton_Click(object sender, RoutedEventArgs e)
    {
        if (_running)
        {
            Pause("一時停止中");
            return;
        }

        if (!_connected || !_hasRest || !_hasBrakeMaximum || !_hasAcceleratorMaximum)
        {
            return;
        }

        _running = true;
        StartButton.Content = "一時停止";
        DriveStatusText.Text = "走行中";
        Focus();
    }

    private void Pause(string message)
    {
        _running = false;
        _steerLeft = false;
        _steerRight = false;
        StartButton.Content = "走行開始";
        DriveStatusText.Text = message;
    }

    private void ResetCar_Click(object sender, RoutedEventArgs e) => ResetCar();

    private void ResetCar()
    {
        _state = new DriveSimulationState(0, 0, _state.DistanceMeters);
        _roadScroll = 0;
        RenderScene();
        DriveStatusText.Text = "車を道路中央へ戻しました";
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Left or Key.A)
        {
            _steerLeft = true;
            e.Handled = true;
        }
        else if (e.Key is Key.Right or Key.D)
        {
            _steerRight = true;
            e.Handled = true;
        }
        else if (e.Key == Key.R)
        {
            ResetCar();
            e.Handled = true;
        }
    }

    private void Window_PreviewKeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Left or Key.A)
        {
            _steerLeft = false;
            e.Handled = true;
        }
        else if (e.Key is Key.Right or Key.D)
        {
            _steerRight = false;
            e.Handled = true;
        }
    }

    private static string FormatCalibration(bool available, double value) => available ? value.ToString("0.0") : "—";

    private static SolidColorBrush BrushFrom(string color) =>
        new((Color)ColorConverter.ConvertFromString(color));
}
