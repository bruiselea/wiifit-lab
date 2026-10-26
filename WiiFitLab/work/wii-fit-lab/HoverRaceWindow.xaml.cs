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

public partial class HoverRaceWindow : Window
{
    private const double PlayerTop = 545;
    private readonly DispatcherTimer _timer;
    private readonly List<Rectangle> _laneMarks = [];
    private readonly List<TrackObject> _trackObjects = [];
    private readonly Random _random = new(704);
    private HoverRaceState _state = new(0, 0, 0, 100);
    private HoverRaceInput _boardInput;
    private long _lastFrameAt = Stopwatch.GetTimestamp();
    private long _lastBoardUpdateAt;
    private double _latestBalanceX = 50;
    private double _latestBalanceY = 50;
    private double _centerX = 50;
    private double _centerY = 50;
    private double _bestSpeed;
    private double _alertSeconds;
    private bool _running;
    private bool _connected;
    private bool _boardControlling;
    private bool _hasCenter;
    private bool _boardBoost;
    private bool _boardButtonWasPressed;
    private bool _hasBoardButtonState;
    private bool _left;
    private bool _right;
    private bool _accelerate;
    private bool _brake;
    private bool _boost;

    public HoverRaceWindow()
    {
        InitializeComponent();
        SensitivitySlider.ValueChanged += SensitivitySlider_ValueChanged;
        CreateStars();
        CreateLaneMarks();
        CreateTrackObjects();
        RenderScene();
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Render, Timer_Tick, Dispatcher);
        _timer.Start();
        Deactivated += (_, _) =>
        {
            ClearKeyboardInput();
            if (_running)
            {
                Pause("画面が非アクティブになったため一時停止しました");
            }
        };
        Closed += (_, _) => _timer.Stop();
    }

    public void SetConnectionState(bool connected)
    {
        _connected = connected;
        if (!connected)
        {
            _boardControlling = false;
            SignalStatusText.Text = "キーボード操作（ボード未接続）";
            SignalStatusText.Foreground = BrushFrom("#F0A84A");
        }
    }

    public void Update(ProcessedBalance data)
    {
        _connected = true;
        _lastBoardUpdateAt = Stopwatch.GetTimestamp();
        _latestBalanceX = data.BalanceX;
        _latestBalanceY = data.BalanceY;
        _boardControlling = data.TemperatureCorrectedWeightKg >= 3;
        _boardBoost = data.ButtonA;

        if (_boardControlling)
        {
            _boardInput = HoverRaceMath.MapBalanceInput(
                _latestBalanceX,
                _latestBalanceY,
                _centerX,
                _centerY,
                SensitivitySlider.Value);
            SignalStatusText.Text = "ボードで操縦中";
            SignalStatusText.Foreground = BrushFrom("#48D7C8");
        }
        else
        {
            _boardInput = default;
            SignalStatusText.Text = "ボード接続済み・乗ると操縦開始";
            SignalStatusText.Foreground = BrushFrom("#F0A84A");
        }

        if (_hasBoardButtonState && data.ButtonA && !_boardButtonWasPressed && !_running && _hasCenter)
        {
            StartRace();
        }

        _boardButtonWasPressed = data.ButtonA;
        _hasBoardButtonState = true;
        UpdateInputReadout();
    }

    private void Timer_Tick(object? sender, EventArgs e)
    {
        var now = Stopwatch.GetTimestamp();
        var elapsed = Math.Clamp(Stopwatch.GetElapsedTime(_lastFrameAt, now).TotalSeconds, 0, 0.05);
        _lastFrameAt = now;

        if (_connected && _lastBoardUpdateAt != 0 &&
            Stopwatch.GetElapsedTime(_lastBoardUpdateAt, now).TotalMilliseconds > 500)
        {
            _boardControlling = false;
            _boardInput = default;
            SignalStatusText.Text = "信号待ち・キーボード操作可";
            SignalStatusText.Foreground = BrushFrom("#F0A84A");
        }

        if (_alertSeconds > 0)
        {
            _alertSeconds -= elapsed;
            if (_alertSeconds <= 0)
            {
                AlertBanner.Visibility = Visibility.Collapsed;
            }
        }

        if (_running)
        {
            var throttle = Math.Max(_boardInput.Throttle, _accelerate ? 1 : 0);
            var brake = Math.Max(_boardInput.Brake, _brake ? 1 : 0);
            var keyboardSteering = (_right ? 1.0 : 0) - (_left ? 1.0 : 0);
            var steering = keyboardSteering != 0 ? keyboardSteering : _boardInput.Steering;
            _state = HoverRaceMath.Step(_state, throttle, brake, steering, _boardBoost || _boost, elapsed);
            _bestSpeed = Math.Max(_bestSpeed, _state.SpeedMetersPerSecond);
            UpdateTrackObjects();

            if (_state.EnergyPercent <= 0)
            {
                Pause("エネルギー切れ。最初からやり直してください");
                StartTitle.Text = "ENERGY EMPTY";
                StartMessage.Text = "リセットして再挑戦";
                StartPanel.Visibility = Visibility.Visible;
            }
        }

        RenderScene();
    }

    private void CreateStars()
    {
        var random = new Random(2077);
        for (var index = 0; index < 80; index++)
        {
            var star = new Ellipse
            {
                Width = index % 9 == 0 ? 3 : 1.5,
                Height = index % 9 == 0 ? 3 : 1.5,
                Fill = index % 5 == 0 ? BrushFrom("#9E8CFF") : Brushes.White,
                Opacity = 0.3 + random.NextDouble() * 0.7,
            };
            Canvas.SetLeft(star, random.NextDouble() * 800);
            Canvas.SetTop(star, random.NextDouble() * 670);
            StarLayer.Children.Add(star);
        }
    }

    private void CreateLaneMarks()
    {
        for (var index = 0; index < 18; index++)
        {
            var mark = new Rectangle
            {
                Width = 5,
                Height = 34,
                RadiusX = 2,
                RadiusY = 2,
                Fill = BrushFrom(index % 2 == 0 ? "#19E6FF" : "#7E5CFF"),
                Opacity = 0.65,
            };
            LaneLayer.Children.Add(mark);
            _laneMarks.Add(mark);
        }
    }

    private void CreateTrackObjects()
    {
        for (var index = 0; index < 10; index++)
        {
            var kind = index % 4 == 3 ? TrackObjectKind.BoostPad : TrackObjectKind.Obstacle;
            var visual = new Rectangle
            {
                Width = kind == TrackObjectKind.BoostPad ? 72 : 46,
                Height = kind == TrackObjectKind.BoostPad ? 34 : 46,
                RadiusX = kind == TrackObjectKind.BoostPad ? 15 : 8,
                RadiusY = kind == TrackObjectKind.BoostPad ? 15 : 8,
                Fill = BrushFrom(kind == TrackObjectKind.BoostPad ? "#19E6FF" : "#8C48FF"),
                Stroke = BrushFrom(kind == TrackObjectKind.BoostPad ? "#E6FFFF" : "#FF63D8"),
                StrokeThickness = 2,
            };
            ObjectLayer.Children.Add(visual);
            _trackObjects.Add(new TrackObject(
                kind,
                visual,
                LateralMeters: -3.2 + _random.NextDouble() * 6.4,
                DistanceMeters: 55 + index * 34,
                Active: true));
        }
    }

    private void UpdateTrackObjects()
    {
        for (var index = 0; index < _trackObjects.Count; index++)
        {
            var item = _trackObjects[index];
            var relative = item.DistanceMeters - _state.DistanceMeters;
            if (relative < -8)
            {
                var furthest = _trackObjects.Max(trackObject => trackObject.DistanceMeters);
                item = item with
                {
                    DistanceMeters = furthest + 30 + _random.NextDouble() * 35,
                    LateralMeters = -3.4 + _random.NextDouble() * 6.8,
                    Active = true,
                };
                _trackObjects[index] = item;
                continue;
            }

            if (!item.Active || relative is < 1.5 or > 7.5 ||
                Math.Abs(item.LateralMeters - _state.LateralMeters) > 0.72)
            {
                continue;
            }

            if (item.Kind == TrackObjectKind.Obstacle)
            {
                _state = HoverRaceMath.ApplyImpact(_state);
                ShowAlert("IMPACT!", "#D9364B");
            }
            else
            {
                _state = HoverRaceMath.ApplyBoostPad(_state);
                ShowAlert("BOOST PAD!", "#0D7890");
            }

            _trackObjects[index] = item with { Active = false };
        }
    }

    private void RenderScene()
    {
        var speedKmH = _state.SpeedMetersPerSecond * 3.6;
        SpeedText.Text = speedKmH.ToString("0");
        DistanceText.Text = $"{_state.DistanceMeters / 1000:0.00} km";
        BestSpeedText.Text = $"{_bestSpeed * 3.6:0} km/h";
        EnergyProgress.Value = _state.EnergyPercent;
        EnergyProgress.Foreground = BrushFrom(_state.EnergyPercent < 25 ? "#FF4F67" : "#19E6FF");

        var craftLeft = 400 + _state.LateralMeters * 54 - HoverCraft.Width / 2;
        Canvas.SetLeft(HoverCraft, craftLeft);
        Canvas.SetTop(HoverCraft, PlayerTop);
        var steering = (_right ? 1.0 : 0) - (_left ? 1.0 : 0);
        if (steering == 0)
        {
            steering = _boardInput.Steering;
        }
        HoverCraft.RenderTransform = new RotateTransform(steering * 10);

        for (var index = 0; index < _laneMarks.Count; index++)
        {
            var column = index % 2;
            var row = index / 2;
            var scroll = (_state.DistanceMeters * 4.5) % 82;
            var y = (row * 82 + scroll) % 738 - 50;
            var perspective = Math.Clamp(y / 670, 0, 1);
            var x = 400 + (column == 0 ? -1 : 1) * (55 + perspective * 100);
            var mark = _laneMarks[index];
            mark.Height = 12 + perspective * 40;
            mark.Width = 2 + perspective * 5;
            Canvas.SetLeft(mark, x - mark.Width / 2);
            Canvas.SetTop(mark, y);
        }

        foreach (var item in _trackObjects)
        {
            var relative = item.DistanceMeters - _state.DistanceMeters;
            if (relative is < 0 or > 135)
            {
                item.Visual.Visibility = Visibility.Collapsed;
                continue;
            }

            var approach = 1 - relative / 135;
            var y = 65 + approach * 505;
            var scale = 0.35 + approach * 0.85;
            var x = 400 + item.LateralMeters * 50 * scale;
            item.Visual.Visibility = item.Active ? Visibility.Visible : Visibility.Hidden;
            item.Visual.RenderTransformOrigin = new Point(0.5, 0.5);
            item.Visual.RenderTransform = new ScaleTransform(scale, scale);
            Canvas.SetLeft(item.Visual, x - item.Visual.Width / 2);
            Canvas.SetTop(item.Visual, y - item.Visual.Height / 2);
        }

        if (Math.Abs(_state.LateralMeters) > HoverRaceMath.TrackHalfWidthMeters)
        {
            RaceStatusText.Text = "コース外！速度とエネルギーが減少中";
            RaceStatusText.Foreground = BrushFrom("#FF6B7A");
        }
        else if (_running)
        {
            RaceStatusText.Text = _boardBoost || _boost ? "ブースト中" : "走行中";
            RaceStatusText.Foreground = (Brush)FindResource("MutedBrush");
        }
    }

    private void CaptureCenter_Click(object sender, RoutedEventArgs e)
    {
        _centerX = _latestBalanceX;
        _centerY = _latestBalanceY;
        _hasCenter = true;
        StartButton.IsEnabled = true;
        RaceStatusText.Text = $"中心を記録しました X {_centerX:0.0} / Y {_centerY:0.0}";
        StartMessage.Text = "レース開始ボタン または Aボタン";
    }

    private void SensitivitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        SensitivityText.Text = $"{SensitivitySlider.Value:0.0}倍";
    }

    private void StartButton_Click(object sender, RoutedEventArgs e)
    {
        if (_running)
        {
            Pause("一時停止中");
            return;
        }

        if (!_hasCenter)
        {
            RaceStatusText.Text = "先に現在の姿勢を中心として記録してください";
            return;
        }

        StartRace();
    }

    private void StartRace()
    {
        _running = true;
        StartButton.Content = "一時停止";
        StartPanel.Visibility = Visibility.Collapsed;
        RaceStatusText.Text = "走行中";
        Focus();
    }

    private void Pause(string message)
    {
        _running = false;
        StartButton.Content = "レース開始";
        StartTitle.Text = "PAUSE";
        StartMessage.Text = message;
        StartPanel.Visibility = Visibility.Visible;
        RaceStatusText.Text = message;
        ClearKeyboardInput();
    }

    private void ResetButton_Click(object sender, RoutedEventArgs e)
    {
        _state = new HoverRaceState(0, 0, 0, 100);
        _bestSpeed = 0;
        _running = false;
        StartButton.Content = "レース開始";
        StartTitle.Text = "NEON HOVER CIRCUIT";
        StartMessage.Text = _hasCenter ? "レース開始ボタン または Aボタン" : "中心姿勢を記録してスタート";
        StartPanel.Visibility = Visibility.Visible;
        RaceStatusText.Text = _hasCenter ? "レース開始できます" : "中心姿勢を記録してください";
        ResetTrackObjects();
        RenderScene();
    }

    private void ResetTrackObjects()
    {
        for (var index = 0; index < _trackObjects.Count; index++)
        {
            _trackObjects[index] = _trackObjects[index] with
            {
                DistanceMeters = 55 + index * 34,
                LateralMeters = -3.2 + _random.NextDouble() * 6.4,
                Active = true,
            };
        }
    }

    private void UpdateInputReadout()
    {
        SteeringProgress.Value = _boardInput.Steering * 100;
        SteeringText.Text = $"{_boardInput.Steering * 100:+0;-0;0} %";
        ThrottleText.Text = $"{_boardInput.Throttle * 100:0} / {_boardInput.Brake * 100:0} %";
    }

    private void ShowAlert(string text, string background)
    {
        AlertText.Text = text;
        AlertBanner.Background = BrushFrom(background);
        AlertBanner.Visibility = Visibility.Visible;
        _alertSeconds = 0.8;
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Left or Key.A)
        {
            _left = true;
        }
        else if (e.Key is Key.Right or Key.D)
        {
            _right = true;
        }
        else if (e.Key is Key.Up or Key.W)
        {
            _accelerate = true;
        }
        else if (e.Key is Key.Down or Key.S)
        {
            _brake = true;
        }
        else if (e.Key == Key.Space)
        {
            _boost = true;
            if (!_running && _hasCenter)
            {
                StartRace();
            }
        }
        else if (e.Key == Key.R)
        {
            ResetButton_Click(sender, e);
        }
        else
        {
            return;
        }

        e.Handled = true;
    }

    private void Window_PreviewKeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Left or Key.A)
        {
            _left = false;
        }
        else if (e.Key is Key.Right or Key.D)
        {
            _right = false;
        }
        else if (e.Key is Key.Up or Key.W)
        {
            _accelerate = false;
        }
        else if (e.Key is Key.Down or Key.S)
        {
            _brake = false;
        }
        else if (e.Key == Key.Space)
        {
            _boost = false;
        }
        else
        {
            return;
        }

        e.Handled = true;
    }

    private void ClearKeyboardInput()
    {
        _left = false;
        _right = false;
        _accelerate = false;
        _brake = false;
        _boost = false;
    }

    private static SolidColorBrush BrushFrom(string color) =>
        new((Color)ColorConverter.ConvertFromString(color));

    private enum TrackObjectKind
    {
        Obstacle,
        BoostPad,
    }

    private sealed record TrackObject(
        TrackObjectKind Kind,
        Rectangle Visual,
        double LateralMeters,
        double DistanceMeters,
        bool Active);
}
