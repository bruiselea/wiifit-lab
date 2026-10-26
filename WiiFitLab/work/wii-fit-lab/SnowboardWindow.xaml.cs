using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using BalanceBoard.Core.Models;

namespace WiiFitLab;

public partial class SnowboardWindow : Window
{
    private const double CourseCenter = 410;
    private const double ViewDistance = 92;
    private readonly DispatcherTimer _timer;
    private readonly List<CourseObject> _objects = [];
    private readonly Random _random = new(1225);
    private long _lastFrameAt = Stopwatch.GetTimestamp();
    private long _lastBoardUpdateAt;
    private double _latestBalanceX = 50;
    private double _latestBalanceY = 50;
    private double _latestWeight;
    private double _centerX = 50;
    private double _centerY = 50;
    private double _turnInput;
    private double _footInput;
    private double _distance;
    private double _speed;
    private double _lateral;
    private double _lateralVelocity;
    private double _remainingSeconds = 75;
    private double _jumpSeconds;
    private double _alertSeconds;
    private int _score;
    private int _gates;
    private bool _connected;
    private bool _boardControlling;
    private bool _hasCenter;
    private bool _running;
    private bool _left;
    private bool _right;
    private bool _accelerate;
    private bool _brake;

    public SnowboardWindow()
    {
        InitializeComponent();
        SensitivitySlider.ValueChanged += (_, _) =>
            SensitivityText.Text = $"{SensitivitySlider.Value:0.0}倍";
        InvertTurnCheck.Checked += (_, _) => RecalculateBoardInput();
        InvertTurnCheck.Unchecked += (_, _) => RecalculateBoardInput();
        CreateSnow();
        CreateCourseObjects();
        ResetRun();
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
        _latestWeight = data.TemperatureCorrectedWeightKg;
        _boardControlling = _latestWeight >= 3;

        if (_boardControlling)
        {
            RecalculateBoardInput();
            SignalStatusText.Text = "横置きボードで滑走中";
            SignalStatusText.Foreground = BrushFrom("#48D7C8");
        }
        else
        {
            _turnInput = 0;
            _footInput = 0;
            SignalStatusText.Text = "ボード接続済み・横向きに乗ってください";
            SignalStatusText.Foreground = BrushFrom("#F0A84A");
        }

        UpdateInputReadout();
    }

    private void RecalculateBoardInput()
    {
        if (!_hasCenter || !_boardControlling)
        {
            return;
        }

        var turnSign = InvertTurnCheck.IsChecked == true ? -1.0 : 1.0;
        var stanceSign = StanceCombo.SelectedIndex == 1 ? -1.0 : 1.0;
        _turnInput = Math.Clamp(((_latestBalanceY - _centerY) / 17.0) * SensitivitySlider.Value * turnSign, -1, 1);
        _footInput = Math.Clamp(((_latestBalanceX - _centerX) / 18.0) * stanceSign, -1, 1);
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
            _turnInput = 0;
            _footInput = 0;
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
            StepGame(elapsed);
        }

        RenderScene();
    }

    private void StepGame(double elapsed)
    {
        var keyboardTurn = (_right ? 1.0 : 0) - (_left ? 1.0 : 0);
        var turn = keyboardTurn != 0 ? keyboardTurn : _turnInput;
        var frontLoad = Math.Max(_footInput, _accelerate ? 1 : 0);
        var backLoad = Math.Max(-_footInput, _brake ? 1 : 0);
        var targetSpeed = 13.5 + frontLoad * 11 - backLoad * 7 - Math.Abs(turn) * 1.8;
        _speed += (targetSpeed - _speed) * Math.Min(1, elapsed * 2.2);
        _speed = Math.Clamp(_speed, 5.5, 26);

        _lateralVelocity += turn * 9.2 * elapsed;
        _lateralVelocity *= Math.Pow(0.12, elapsed);
        _lateral += _lateralVelocity * elapsed;
        if (Math.Abs(_lateral) > 3.9)
        {
            _lateral = Math.Clamp(_lateral, -3.9, 3.9);
            _lateralVelocity *= -0.18;
            _speed *= 0.72;
            ShowAlert("コース端！", "#FFB347");
        }

        _distance += _speed * elapsed;
        _remainingSeconds -= elapsed;
        _score += (int)Math.Round(_speed * elapsed * 0.5);
        _jumpSeconds = Math.Max(0, _jumpSeconds - elapsed);
        UpdateCourseObjects();

        if (_remainingSeconds <= 0)
        {
            _remainingSeconds = 0;
            _running = false;
            StartButton.Content = "もう一度滑る";
            StartTitle.Text = "FINISH!";
            StartMessage.Text = $"{_distance:0} m / {_gates}ゲート / {_score:0000}点";
            StartPanel.Visibility = Visibility.Visible;
            GameStatusText.Text = "ラン完了。リセットせず、そのまま再スタートできます";
        }
    }

    private void CreateSnow()
    {
        var random = new Random(88);
        for (var index = 0; index < 70; index++)
        {
            var size = index % 8 == 0 ? 4.0 : 2.0;
            var flake = new Ellipse
            {
                Width = size,
                Height = size,
                Fill = Brushes.White,
                Opacity = 0.25 + random.NextDouble() * 0.65,
            };
            Canvas.SetLeft(flake, random.NextDouble() * 820);
            Canvas.SetTop(flake, 60 + random.NextDouble() * 600);
            SnowLayer.Children.Add(flake);
        }
    }

    private void CreateCourseObjects()
    {
        ObjectLayer.Children.Clear();
        _objects.Clear();
        for (var index = 0; index < 15; index++)
        {
            var kind = index % 5 == 0 ? CourseObjectKind.Ramp :
                index % 3 == 0 ? CourseObjectKind.Tree : CourseObjectKind.Gate;
            var visual = CreateObjectVisual(kind);
            ObjectLayer.Children.Add(visual);
            _objects.Add(new CourseObject(kind, visual, 34 + index * 23, RandomLane(kind), false));
        }
    }

    private FrameworkElement CreateObjectVisual(CourseObjectKind kind)
    {
        if (kind == CourseObjectKind.Tree)
        {
            var canvas = new Canvas { Width = 58, Height = 88 };
            var trunk = new Rectangle { Width = 10, Height = 26, Fill = BrushFrom("#6E4428") };
            Canvas.SetLeft(trunk, 24);
            Canvas.SetTop(trunk, 61);
            canvas.Children.Add(trunk);
            canvas.Children.Add(new Polygon
            {
                Points = [new(29, 0), new(2, 68), new(56, 68)],
                Fill = BrushFrom("#176B5C"), Stroke = BrushFrom("#BDF7EA"), StrokeThickness = 2,
            });
            return canvas;
        }

        if (kind == CourseObjectKind.Ramp)
        {
            return new Border
            {
                Width = 82, Height = 31, CornerRadius = new CornerRadius(5),
                Background = BrushFrom("#49BFE3"), BorderBrush = Brushes.White, BorderThickness = new Thickness(2),
                Child = new TextBlock
                {
                    Text = "▲", Foreground = Brushes.White, FontSize = 20,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                },
            };
        }

        var gate = new Canvas { Width = 118, Height = 82 };
        gate.Children.Add(new Line { X1 = 9, Y1 = 6, X2 = 9, Y2 = 82, Stroke = BrushFrom("#FF5D72"), StrokeThickness = 5 });
        gate.Children.Add(new Line { X1 = 109, Y1 = 6, X2 = 109, Y2 = 82, Stroke = BrushFrom("#4D8DFF"), StrokeThickness = 5 });
        gate.Children.Add(new Polygon { Points = [new(11, 8), new(42, 17), new(11, 29)], Fill = BrushFrom("#FF5D72") });
        gate.Children.Add(new Polygon { Points = [new(107, 8), new(76, 17), new(107, 29)], Fill = BrushFrom("#4D8DFF") });
        return gate;
    }

    private double RandomLane(CourseObjectKind kind) =>
        kind == CourseObjectKind.Tree
            ? (_random.NextDouble() < 0.5 ? -1 : 1) * (2.1 + _random.NextDouble() * 1.4)
            : -2.4 + _random.NextDouble() * 4.8;

    private void UpdateCourseObjects()
    {
        for (var index = 0; index < _objects.Count; index++)
        {
            var item = _objects[index];
            var relative = item.Distance - _distance;

            if (!item.Resolved && relative is <= 2.5 and >= -2.5)
            {
                var difference = Math.Abs(item.Lateral - _lateral);
                if (item.Kind == CourseObjectKind.Gate)
                {
                    if (difference <= 1.2)
                    {
                        _gates++;
                        _score += 250;
                        ShowAlert("GATE +250", "#48D7C8");
                    }
                }
                else if (item.Kind == CourseObjectKind.Tree && difference <= 0.62)
                {
                    _speed = Math.Max(5.5, _speed * 0.38);
                    _score = Math.Max(0, _score - 120);
                    _lateralVelocity += _lateral <= item.Lateral ? -3 : 3;
                    ShowAlert("TREE -120", "#FF5D72");
                }
                else if (item.Kind == CourseObjectKind.Ramp && difference <= 0.9)
                {
                    _jumpSeconds = 1.05;
                    _score += 180;
                    ShowAlert("AIR +180", "#8DE8FF");
                }

                item = item with { Resolved = true };
                _objects[index] = item;
            }

            if (relative < -9)
            {
                var furthest = _objects.Max(value => value.Distance);
                item = item with
                {
                    Distance = furthest + 19 + _random.NextDouble() * 13,
                    Lateral = RandomLane(item.Kind),
                    Resolved = false,
                };
                _objects[index] = item;
            }
        }
    }

    private void RenderScene()
    {
        var keyboardTurn = (_right ? 1.0 : 0) - (_left ? 1.0 : 0);
        var turn = keyboardTurn != 0 ? keyboardTurn : _turnInput;
        var jumpProgress = _jumpSeconds <= 0 ? 0 : Math.Sin(Math.Clamp(_jumpSeconds / 1.05, 0, 1) * Math.PI);
        Canvas.SetLeft(PlayerRoot, 363 + _lateral * 61);
        Canvas.SetTop(PlayerRoot, 535 - jumpProgress * 82);
        PlayerRoot.RenderTransform = new RotateTransform(turn * 20);

        foreach (var item in _objects)
        {
            var relative = item.Distance - _distance;
            if (relative is < -8 or > ViewDistance)
            {
                item.Visual.Visibility = Visibility.Collapsed;
                continue;
            }

            item.Visual.Visibility = Visibility.Visible;
            var progress = Math.Clamp(1 - (relative / ViewDistance), 0, 1);
            var depth = Math.Pow(progress, 1.62);
            var y = 136 + depth * 520;
            var roadHalfWidth = 98 + depth * 245;
            var x = CourseCenter + item.Lateral * (roadHalfWidth / 4.1);
            var scale = 0.22 + depth * 1.04;
            item.Visual.RenderTransformOrigin = new Point(0.5, 1);
            item.Visual.RenderTransform = new ScaleTransform(scale, scale);
            Canvas.SetLeft(item.Visual, x - item.Visual.Width / 2);
            Canvas.SetTop(item.Visual, y - item.Visual.Height);
            Panel.SetZIndex(item.Visual, (int)y);
        }

        SpeedText.Text = $"{_speed * 3.6:0} km/h";
        DistanceText.Text = $"{_distance:0} m";
        ScoreText.Text = $"SCORE {_score:0000}";
        TimeText.Text = $"{Math.Ceiling(_remainingSeconds):0}";
    }

    private void UpdateInputReadout()
    {
        TurnText.Text = $"{_turnInput * 100:+0;-0;0} %";
        FootText.Text = Math.Abs(_footInput) < 0.08
            ? "中央"
            : _footInput > 0
                ? $"前足 {_footInput * 100:0}%"
                : $"後ろ足 {-_footInput * 100:0}%";
    }

    private void ShowAlert(string text, string color)
    {
        AlertText.Text = text;
        AlertBanner.Background = BrushFrom(color);
        AlertBanner.Visibility = Visibility.Visible;
        _alertSeconds = 1.15;
    }

    private void CalibrateButton_Click(object sender, RoutedEventArgs e)
    {
        _centerX = _latestBalanceX;
        _centerY = _latestBalanceY;
        _hasCenter = true;
        StartButton.IsEnabled = true;
        RecalculateBoardInput();
        StartMessage.Text = "Enter または ラン開始";
        GameStatusText.Text = $"ニュートラル記録 X {_centerX:0.0} / Y {_centerY:0.0} / {_latestWeight:0.0} kg";
    }

    private void StartButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_hasCenter)
        {
            GameStatusText.Text = "先に横向きで自然に立ち、ニュートラルを記録してください";
            return;
        }

        if (_running)
        {
            Pause("一時停止中");
            return;
        }

        if (_remainingSeconds <= 0)
        {
            ResetRun();
        }

        _running = true;
        StartButton.Content = "一時停止";
        StartPanel.Visibility = Visibility.Collapsed;
        GameStatusText.Text = "滑走中：ゲートを通り、木を避けてください";
        _lastFrameAt = Stopwatch.GetTimestamp();
    }

    private void Pause(string message)
    {
        _running = false;
        StartButton.Content = "再開";
        GameStatusText.Text = message;
    }

    private void ResetButton_Click(object sender, RoutedEventArgs e) => ResetRun();

    private void ResetRun()
    {
        _running = false;
        _distance = 0;
        _speed = 12;
        _lateral = 0;
        _lateralVelocity = 0;
        _remainingSeconds = 75;
        _jumpSeconds = 0;
        _score = 0;
        _gates = 0;
        StartButton.Content = "ラン開始";
        StartTitle.Text = "SIDE STANCE SNOW";
        StartMessage.Text = _hasCenter ? "Enter または ラン開始" : "ボードを90°回してニュートラルを記録";
        StartPanel.Visibility = Visibility.Visible;
        GameStatusText.Text = _hasCenter ? "スタートできます" : "ニュートラル姿勢を記録してください";
        for (var index = 0; index < _objects.Count; index++)
        {
            _objects[index] = _objects[index] with
            {
                Distance = 34 + index * 23,
                Lateral = RandomLane(_objects[index].Kind),
                Resolved = false,
            };
        }
        RenderScene();
    }

    private void StanceCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded)
        {
            RecalculateBoardInput();
        }
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.IsRepeat && e.Key is Key.Enter or Key.R)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Left:
            case Key.A:
                _left = true;
                break;
            case Key.Right:
            case Key.D:
                _right = true;
                break;
            case Key.Up:
            case Key.W:
                _accelerate = true;
                break;
            case Key.Down:
            case Key.S:
                _brake = true;
                break;
            case Key.Enter:
                StartButton_Click(sender, e);
                break;
            case Key.R:
                ResetRun();
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    private void Window_PreviewKeyUp(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Left:
            case Key.A:
                _left = false;
                break;
            case Key.Right:
            case Key.D:
                _right = false;
                break;
            case Key.Up:
            case Key.W:
                _accelerate = false;
                break;
            case Key.Down:
            case Key.S:
                _brake = false;
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    private void ClearKeyboardInput() => _left = _right = _accelerate = _brake = false;

    private static SolidColorBrush BrushFrom(string color) =>
        new((Color)ColorConverter.ConvertFromString(color));

    private enum CourseObjectKind
    {
        Gate,
        Tree,
        Ramp,
    }

    private sealed record CourseObject(
        CourseObjectKind Kind,
        FrameworkElement Visual,
        double Distance,
        double Lateral,
        bool Resolved);
}
