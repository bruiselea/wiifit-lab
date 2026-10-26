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

public partial class BreakoutWindow : Window
{
    private const double PlayfieldWidth = 800;
    private const double PlayfieldHeight = 670;
    private const double PaddleWidth = 126;
    private const double PaddleTop = 620;
    private const double BallRadius = 9;
    private const double InitialBallSpeed = 330;
    private readonly DispatcherTimer _timer;
    private readonly List<Brick> _bricks = [];
    private long _lastFrameAt = Stopwatch.GetTimestamp();
    private long _lastBoardUpdateAt;
    private double _paddleLeft = (PlayfieldWidth - PaddleWidth) / 2;
    private double _targetPaddleLeft = (PlayfieldWidth - PaddleWidth) / 2;
    private double _ballX = PlayfieldWidth / 2;
    private double _ballY = PaddleTop - BallRadius - 4;
    private double _velocityX;
    private double _velocityY;
    private double _ballSpeed = InitialBallSpeed;
    private double _latestBalance = 50;
    private int _score;
    private int _lives = 3;
    private int _level = 1;
    private bool _ballLaunched;
    private bool _paused;
    private bool _gameOver;
    private bool _leftPressed;
    private bool _rightPressed;
    private bool _boardButtonWasPressed;
    private bool _hasBoardButtonState;
    private bool _boardControlling;
    private bool _connected;

    public BreakoutWindow()
    {
        InitializeComponent();
        SensitivitySlider.ValueChanged += SensitivitySlider_ValueChanged;
        CreateBricks();
        Render();
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Render, Timer_Tick, Dispatcher);
        _timer.Start();
        Deactivated += (_, _) => ClearKeyboardInput();
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
        var weight = Math.Max(0, data.TemperatureCorrectedWeightKg);
        _boardControlling = weight >= 3;

        if (_boardControlling)
        {
            var balance = Math.Clamp(data.BalanceX, 0, 100);
            _latestBalance = balance;
            UpdateBoardPaddleTarget();
            BalanceProgress.Value = balance;
            BalanceText.Text = $"{balance:0.0} %（{BalanceLabel(balance)}）";
            SignalStatusText.Text = "ボードで操作中";
            SignalStatusText.Foreground = BrushFrom("#48D7C8");
        }
        else
        {
            SignalStatusText.Text = "ボード接続済み・乗ると操作開始";
            SignalStatusText.Foreground = BrushFrom("#F0A84A");
        }

        if (_hasBoardButtonState && data.ButtonA && !_boardButtonWasPressed)
        {
            LaunchOrContinue();
        }

        _boardButtonWasPressed = data.ButtonA;
        _hasBoardButtonState = true;
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
            SignalStatusText.Text = "信号待ち・キーボード操作可";
            SignalStatusText.Foreground = BrushFrom("#F0A84A");
        }

        UpdatePaddle(elapsed);
        if (_paused || _gameOver)
        {
            Render();
            return;
        }

        if (!_ballLaunched)
        {
            AttachBallToPaddle();
            Render();
            return;
        }

        UpdateBall(elapsed);
        Render();
    }

    private void UpdatePaddle(double elapsed)
    {
        if (_leftPressed || _rightPressed)
        {
            var direction = (_rightPressed ? 1 : 0) - (_leftPressed ? 1 : 0);
            _targetPaddleLeft += direction * 520 * elapsed;
            _boardControlling = false;
        }

        _targetPaddleLeft = Math.Clamp(_targetPaddleLeft, 0, PlayfieldWidth - PaddleWidth);
        var smoothing = 1 - Math.Exp(-elapsed * 18);
        _paddleLeft += (_targetPaddleLeft - _paddleLeft) * smoothing;
    }

    private void UpdateBall(double elapsed)
    {
        _ballX += _velocityX * elapsed;
        _ballY += _velocityY * elapsed;

        if (_ballX <= BallRadius)
        {
            _ballX = BallRadius;
            _velocityX = Math.Abs(_velocityX);
        }
        else if (_ballX >= PlayfieldWidth - BallRadius)
        {
            _ballX = PlayfieldWidth - BallRadius;
            _velocityX = -Math.Abs(_velocityX);
        }

        if (_ballY <= BallRadius)
        {
            _ballY = BallRadius;
            _velocityY = Math.Abs(_velocityY);
        }

        var ballRect = new Rect(_ballX - BallRadius, _ballY - BallRadius, BallRadius * 2, BallRadius * 2);
        var paddleRect = new Rect(_paddleLeft, PaddleTop, PaddleWidth, Paddle.ActualHeight > 0 ? Paddle.ActualHeight : 18);
        if (_velocityY > 0 && ballRect.IntersectsWith(paddleRect) && _ballY < PaddleTop + 10)
        {
            _ballY = PaddleTop - BallRadius;
            var bounce = BreakoutMath.BounceFromPaddle(_ballX, _paddleLeft, PaddleWidth, _ballSpeed);
            _velocityX = bounce.X;
            _velocityY = bounce.Y;
        }

        ballRect = new Rect(_ballX - BallRadius, _ballY - BallRadius, BallRadius * 2, BallRadius * 2);
        foreach (var brick in _bricks.ToArray())
        {
            if (!ballRect.IntersectsWith(brick.Bounds))
            {
                continue;
            }

            BrickLayer.Children.Remove(brick.Visual);
            _bricks.Remove(brick);
            _score += 100;
            _velocityY = -_velocityY;
            break;
        }

        if (_bricks.Count == 0)
        {
            _level++;
            _ballSpeed = Math.Min(520, InitialBallSpeed + (_level - 1) * 28);
            CreateBricks();
            PrepareBall("LEVEL CLEAR", "Aボタン または Space / Enter で次の面");
        }
        else if (_ballY - BallRadius > PlayfieldHeight)
        {
            LoseLife();
        }
    }

    private void LoseLife()
    {
        _lives--;
        if (_lives <= 0)
        {
            _gameOver = true;
            _ballLaunched = false;
            MessageTitle.Text = "GAME OVER";
            MessageText.Text = "Rキー または「最初から」で再挑戦";
            MessagePanel.Visibility = Visibility.Visible;
            GameStatusText.Text = $"ゲームオーバー　スコア {_score:N0}";
            UpdateHud();
            return;
        }

        PrepareBall("MISS", "Aボタン または Space / Enter でもう一度");
    }

    private void LaunchOrContinue()
    {
        if (_gameOver)
        {
            ResetGame();
            return;
        }

        if (_paused)
        {
            TogglePause();
            return;
        }

        if (_ballLaunched)
        {
            return;
        }

        _ballLaunched = true;
        var launchDirection = _level % 2 == 0 ? -1 : 1;
        _velocityX = launchDirection * _ballSpeed * 0.30;
        _velocityY = -Math.Sqrt(_ballSpeed * _ballSpeed - _velocityX * _velocityX);
        MessagePanel.Visibility = Visibility.Collapsed;
        GameStatusText.Text = "プレイ中";
        Focus();
    }

    private void PrepareBall(string title, string message)
    {
        _ballLaunched = false;
        _velocityX = 0;
        _velocityY = 0;
        AttachBallToPaddle();
        MessageTitle.Text = title;
        MessageText.Text = message;
        MessagePanel.Visibility = Visibility.Visible;
        GameStatusText.Text = "発射待ち";
        UpdateHud();
    }

    private void AttachBallToPaddle()
    {
        _ballX = _paddleLeft + PaddleWidth / 2;
        _ballY = PaddleTop - BallRadius - 4;
    }

    private void CreateBricks()
    {
        BrickLayer.Children.Clear();
        _bricks.Clear();
        string[] colors = ["#FF6B7A", "#FF9B63", "#FFD166", "#55D6BE", "#5FA8FF"];
        const int columns = 8;
        const int rows = 5;
        const double width = 82;
        const double height = 26;
        const double gap = 10;
        const double startX = 37;
        const double startY = 88;

        for (var row = 0; row < rows; row++)
        {
            for (var column = 0; column < columns; column++)
            {
                var left = startX + column * (width + gap);
                var top = startY + row * (height + gap);
                var visual = new Rectangle
                {
                    Width = width,
                    Height = height,
                    RadiusX = 7,
                    RadiusY = 7,
                    Fill = BrushFrom(colors[row]),
                    Stroke = BrushFrom("#55FFFFFF"),
                    StrokeThickness = 1,
                };
                Canvas.SetLeft(visual, left);
                Canvas.SetTop(visual, top);
                BrickLayer.Children.Add(visual);
                _bricks.Add(new Brick(new Rect(left, top, width, height), visual));
            }
        }
    }

    private void ResetButton_Click(object sender, RoutedEventArgs e) => ResetGame();

    private void SensitivitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        var sensitivity = SensitivitySlider.Value;
        var halfRange = 50 / sensitivity;
        SensitivityText.Text = $"{sensitivity:0.0}倍（{50 - halfRange:0.0}〜{50 + halfRange:0.0}%で全幅）";
        UpdateBoardPaddleTarget();
    }

    private void UpdateBoardPaddleTarget()
    {
        _targetPaddleLeft = BreakoutMath.MapBalanceToPaddleLeft(
            _latestBalance,
            PlayfieldWidth,
            PaddleWidth,
            SensitivitySlider.Value);
    }

    private void ResetGame()
    {
        _score = 0;
        _lives = 3;
        _level = 1;
        _ballSpeed = InitialBallSpeed;
        _gameOver = false;
        _paused = false;
        PauseButton.Content = "一時停止";
        CreateBricks();
        PrepareBall("準備OK", "Aボタン または Space / Enter で発射");
    }

    private void PauseButton_Click(object sender, RoutedEventArgs e) => TogglePause();

    private void TogglePause()
    {
        if (_gameOver)
        {
            return;
        }

        _paused = !_paused;
        PauseButton.Content = _paused ? "再開" : "一時停止";
        MessageTitle.Text = "PAUSE";
        MessageText.Text = "Pキー / ボタンで再開";
        MessagePanel.Visibility = _paused || !_ballLaunched ? Visibility.Visible : Visibility.Collapsed;
        GameStatusText.Text = _paused ? "一時停止中" : _ballLaunched ? "プレイ中" : "発射待ち";
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Left or Key.A)
        {
            _leftPressed = true;
            e.Handled = true;
        }
        else if (e.Key is Key.Right or Key.D)
        {
            _rightPressed = true;
            e.Handled = true;
        }
        else if (e.Key is Key.Space or Key.Enter)
        {
            LaunchOrContinue();
            e.Handled = true;
        }
        else if (e.Key == Key.P)
        {
            TogglePause();
            e.Handled = true;
        }
        else if (e.Key == Key.R)
        {
            ResetGame();
            e.Handled = true;
        }
    }

    private void Window_PreviewKeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Left or Key.A)
        {
            _leftPressed = false;
            e.Handled = true;
        }
        else if (e.Key is Key.Right or Key.D)
        {
            _rightPressed = false;
            e.Handled = true;
        }
    }

    private void ClearKeyboardInput()
    {
        _leftPressed = false;
        _rightPressed = false;
    }

    private void Render()
    {
        Canvas.SetLeft(Paddle, _paddleLeft);
        Canvas.SetTop(Paddle, PaddleTop);
        Canvas.SetLeft(Ball, _ballX - BallRadius);
        Canvas.SetTop(Ball, _ballY - BallRadius);
        UpdateHud();
    }

    private void UpdateHud()
    {
        ScoreText.Text = _score.ToString("D6");
        LivesText.Text = string.Join(" ", Enumerable.Repeat("●", Math.Max(0, _lives)));
        LevelText.Text = _level.ToString();
    }

    private static string BalanceLabel(double balance) =>
        balance < 42 ? "左" : balance > 58 ? "右" : "中央";

    private static SolidColorBrush BrushFrom(string color) =>
        new((Color)ColorConverter.ConvertFromString(color));

    private sealed record Brick(Rect Bounds, Rectangle Visual);
}
