using System.Globalization;
using System.IO;
using System.Text;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using BalanceBoard.Core.Models;
using BalanceBoard.Core.Processing;
using BalanceBoard.Core.Services.Session;
using Microsoft.Win32;

namespace WiiFitLab;

public partial class MainWindow : Window
{
    private readonly BalanceBoardSession _session = new();
    private StreamWriter? _recordWriter;
    private DateTimeOffset _recordStartedAt;
    private int _uiUpdatePending;
    private bool _connecting;
    private readonly object _reportRateSync = new();
    private long _reportRateWindowStarted = Stopwatch.GetTimestamp();
    private int _reportsInWindow;
    private double _reportRateHz;
    private DiagnosticsWindow? _diagnosticsWindow;
    private PedalWindow? _pedalWindow;
    private DriveSimulatorWindow? _driveSimulatorWindow;
    private GameHubWindow? _gameHubWindow;
    private BreakoutWindow? _breakoutWindow;
    private HoverRaceWindow? _hoverRaceWindow;
    private TiltMazeWindow? _tiltMazeWindow;
    private SnowboardWindow? _snowboardWindow;

    public MainWindow()
    {
        InitializeComponent();

        _session.LoadSettings(new AppSettings
        {
            EnableVJoy = false,
            DisableKeyboardActions = true,
            AutoConnectOnStartup = false,
            HasConnectedBefore = true,
            AutoTareOnConnect = true,
        }, initializeVJoy: false);

        _session.Processed += OnProcessed;
        _session.StatusChanged += status => SetStatusFromWorker(TranslateStatus(status), false);
        _session.Log += message =>
        {
            if (message.Contains("error", StringComparison.OrdinalIgnoreCase))
            {
                SetStatusFromWorker("接続エラー", false);
            }
        };

        Loaded += async (_, _) => await ConnectAsync();
        Closing += (_, _) =>
        {
            _diagnosticsWindow?.Close();
            _pedalWindow?.Close();
            _driveSimulatorWindow?.Close();
            _gameHubWindow?.Close();
            _breakoutWindow?.Close();
            _hoverRaceWindow?.Close();
            _tiltMazeWindow?.Close();
            _snowboardWindow?.Close();
            StopRecording();
            _session.Dispose();
        };
    }

    private async void ConnectButton_Click(object sender, RoutedEventArgs e)
    {
        if (_session.IsConnected)
        {
            _session.Disconnect();
            ApplyDisconnectedState("切断しました");
            return;
        }

        await ConnectAsync();
    }

    private async Task ConnectAsync()
    {
        if (_connecting || _session.IsConnected)
        {
            return;
        }

        _connecting = true;
        ConnectButton.IsEnabled = false;
        SetStatus("接続中…", "#F0A84A");

        try
        {
            var result = await _session.ConnectWithIntentAsync(ConnectionIntent.QuickReconnect);
            if (!result.IsSuccess)
            {
                ApplyDisconnectedState("ボードの前面電源を押して、もう一度接続してください");
                return;
            }

            SetStatus("接続済み", "#48D7C8");
            ConnectButton.Content = "切断する";
            TareButton.IsEnabled = true;
            RecordButton.IsEnabled = true;
        }
        catch (Exception exception)
        {
            ApplyDisconnectedState("接続できませんでした: " + exception.Message);
        }
        finally
        {
            _connecting = false;
            ConnectButton.IsEnabled = true;
        }
    }

    private void TareButton_Click(object sender, RoutedEventArgs e)
    {
        _session.Tare();
        SetStatus("ゼロ調整しました", "#48D7C8");
    }

    private void RecordButton_Click(object sender, RoutedEventArgs e)
    {
        if (_recordWriter is not null)
        {
            StopRecording();
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "測定データの保存先",
            Filter = "CSVファイル (*.csv)|*.csv",
            DefaultExt = ".csv",
            AddExtension = true,
            FileName = $"wii-fit-{DateTime.Now:yyyyMMdd-HHmmss}.csv",
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        _recordWriter = new StreamWriter(dialog.FileName, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true))
        {
            AutoFlush = true,
        };
        _recordWriter.WriteLine("elapsed_ms,timestamp,weight_uncorrected_kg,weight_temperature_corrected_kg,top_left_kg,top_right_kg,bottom_left_kg,bottom_right_kg,top_left_raw,top_right_raw,bottom_left_raw,bottom_right_raw,balance_x_percent,balance_y_percent,temperature_raw,reference_temperature_raw,temperature_delta,battery_raw,battery_bars");
        _recordStartedAt = DateTimeOffset.Now;
        RecordButton.Content = "CSV記録を停止";
        RecordStatusText.Text = "● 記録中";
        RecordStatusText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FF6B7A"));
    }

    private void StopRecording()
    {
        _recordWriter?.Dispose();
        _recordWriter = null;

        if (!IsLoaded)
        {
            return;
        }

        RecordButton.Content = "CSV記録を開始";
        RecordStatusText.Text = "未記録";
        RecordStatusText.Foreground = (Brush)FindResource("MutedBrush");
    }

    private void OnProcessed(ProcessedBalance data)
    {
        UpdateReportRate();

        if (Interlocked.Exchange(ref _uiUpdatePending, 1) != 0)
        {
            return;
        }

        _ = Dispatcher.BeginInvoke(() =>
        {
            try
            {
                UpdateReadings(data);
            }
            finally
            {
                Interlocked.Exchange(ref _uiUpdatePending, 0);
            }
        });
    }

    private void UpdateReadings(ProcessedBalance data)
    {
        var uncorrectedWeight = Math.Max(0, data.WeightKg);
        var correctedWeight = Math.Max(0, data.TemperatureCorrectedWeightKg);
        WeightText.Text = correctedWeight.ToString("0.0", CultureInfo.InvariantCulture);
        UncorrectedWeightText.Text = FormatKg(uncorrectedWeight);
        CorrectionText.Text = $"温度補正差  {correctedWeight - uncorrectedWeight:+0.000;-0.000;0.000} kg";

        var temperatureDelta = data.TemperatureRaw - data.ReferenceTemperatureRaw;
        TemperatureText.Text = $"{data.TemperatureRaw}  (約 {data.TemperatureRaw} ℃)";
        ReferenceTemperatureText.Text = data.ReferenceTemperatureRaw == 0
            ? "未取得"
            : $"{data.ReferenceTemperatureRaw} / {temperatureDelta:+0;-0;0}";
        BatteryText.Text = $"{new string('●', data.BatteryBars)}{new string('○', 4 - data.BatteryBars)}  0x{data.BatteryRaw:X2}";
        TopLeftText.Text = FormatKg(data.TopLeftKg);
        TopRightText.Text = FormatKg(data.TopRightKg);
        BottomLeftText.Text = FormatKg(data.BottomLeftKg);
        BottomRightText.Text = FormatKg(data.BottomRightKg);

        var (balanceX, balanceY) = BalanceDisplay.GetCenterDotPercent(data);
        BalanceXText.Text = $"{balanceX:0.0} %";
        BalanceYText.Text = $"{balanceY:0.0} %";

        var (left, top) = BalanceDisplay.CenterDotCanvasPosition(
            balanceX,
            balanceY,
            BalanceCanvas.Width,
            BalanceCanvas.Height,
            CenterDot.Width,
            CenterDot.Height);
        System.Windows.Controls.Canvas.SetLeft(CenterDot, left);
        System.Windows.Controls.Canvas.SetTop(CenterDot, top);

        _diagnosticsWindow?.Update(data, GetReportRateHz());
        _pedalWindow?.Update(data);
        _driveSimulatorWindow?.Update(data);
        _breakoutWindow?.Update(data);
        _hoverRaceWindow?.Update(data);
        _tiltMazeWindow?.Update(data);
        _snowboardWindow?.Update(data);

        if (_recordWriter is not null)
        {
            var now = DateTimeOffset.Now;
            var elapsed = (long)(now - _recordStartedAt).TotalMilliseconds;
            _recordWriter.WriteLine(string.Join(",",
                elapsed.ToString(CultureInfo.InvariantCulture),
                now.ToString("O", CultureInfo.InvariantCulture),
                uncorrectedWeight.ToString("0.000", CultureInfo.InvariantCulture),
                correctedWeight.ToString("0.000", CultureInfo.InvariantCulture),
                Math.Max(0, data.TopLeftKg).ToString("0.000", CultureInfo.InvariantCulture),
                Math.Max(0, data.TopRightKg).ToString("0.000", CultureInfo.InvariantCulture),
                Math.Max(0, data.BottomLeftKg).ToString("0.000", CultureInfo.InvariantCulture),
                Math.Max(0, data.BottomRightKg).ToString("0.000", CultureInfo.InvariantCulture),
                data.TopLeftSensor.Raw.ToString(CultureInfo.InvariantCulture),
                data.TopRightSensor.Raw.ToString(CultureInfo.InvariantCulture),
                data.BottomLeftSensor.Raw.ToString(CultureInfo.InvariantCulture),
                data.BottomRightSensor.Raw.ToString(CultureInfo.InvariantCulture),
                balanceX.ToString("0.000", CultureInfo.InvariantCulture),
                balanceY.ToString("0.000", CultureInfo.InvariantCulture),
                data.TemperatureRaw.ToString(CultureInfo.InvariantCulture),
                data.ReferenceTemperatureRaw.ToString(CultureInfo.InvariantCulture),
                temperatureDelta.ToString(CultureInfo.InvariantCulture),
                data.BatteryRaw.ToString(CultureInfo.InvariantCulture),
                data.BatteryBars.ToString(CultureInfo.InvariantCulture)));
        }
    }

    private void DiagnosticsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_diagnosticsWindow is { IsLoaded: true })
        {
            _diagnosticsWindow.Activate();
            return;
        }

        _diagnosticsWindow = new DiagnosticsWindow
        {
            Owner = this,
        };
        _diagnosticsWindow.Closed += (_, _) => _diagnosticsWindow = null;
        _diagnosticsWindow.Show();
    }

    private void PedalButton_Click(object sender, RoutedEventArgs e)
    {
        if (_pedalWindow is { IsLoaded: true })
        {
            _pedalWindow.Activate();
            return;
        }

        _pedalWindow = new PedalWindow
        {
            Owner = this,
        };
        _pedalWindow.SetConnectionState(_session.IsConnected);
        _pedalWindow.Closed += (_, _) => _pedalWindow = null;
        _pedalWindow.Show();
    }

    private void MiniGamesButton_Click(object sender, RoutedEventArgs e)
    {
        if (_gameHubWindow is { IsLoaded: true })
        {
            _gameHubWindow.Activate();
            return;
        }

        _gameHubWindow = new GameHubWindow
        {
            Owner = this,
        };
        _gameHubWindow.SetConnectionState(_session.IsConnected);
        _gameHubWindow.DriveRequested += (_, _) => OpenDriveSimulator();
        _gameHubWindow.BreakoutRequested += (_, _) => OpenBreakout();
        _gameHubWindow.HoverRaceRequested += (_, _) => OpenHoverRace();
        _gameHubWindow.TiltMazeRequested += (_, _) => OpenTiltMaze();
        _gameHubWindow.SnowboardRequested += (_, _) => OpenSnowboard();
        _gameHubWindow.Closed += (_, _) => _gameHubWindow = null;
        _gameHubWindow.Show();
    }

    private void OpenDriveSimulator()
    {
        if (_driveSimulatorWindow is { IsLoaded: true })
        {
            _driveSimulatorWindow.Activate();
            return;
        }

        _driveSimulatorWindow = new DriveSimulatorWindow
        {
            Owner = this,
        };
        _driveSimulatorWindow.SetConnectionState(_session.IsConnected);
        _driveSimulatorWindow.Closed += (_, _) => _driveSimulatorWindow = null;
        _driveSimulatorWindow.Show();
    }

    private void OpenBreakout()
    {
        if (_breakoutWindow is { IsLoaded: true })
        {
            _breakoutWindow.Activate();
            return;
        }

        _breakoutWindow = new BreakoutWindow
        {
            Owner = this,
        };
        _breakoutWindow.SetConnectionState(_session.IsConnected);
        _breakoutWindow.Closed += (_, _) => _breakoutWindow = null;
        _breakoutWindow.Show();
    }

    private void OpenHoverRace()
    {
        if (_hoverRaceWindow is { IsLoaded: true })
        {
            _hoverRaceWindow.Activate();
            return;
        }

        _hoverRaceWindow = new HoverRaceWindow
        {
            Owner = this,
        };
        _hoverRaceWindow.SetConnectionState(_session.IsConnected);
        _hoverRaceWindow.Closed += (_, _) => _hoverRaceWindow = null;
        _hoverRaceWindow.Show();
    }

    private void OpenTiltMaze()
    {
        if (_tiltMazeWindow is { IsLoaded: true })
        {
            _tiltMazeWindow.Activate();
            return;
        }

        _tiltMazeWindow = new TiltMazeWindow
        {
            Owner = this,
        };
        _tiltMazeWindow.SetConnectionState(_session.IsConnected);
        _tiltMazeWindow.Closed += (_, _) => _tiltMazeWindow = null;
        _tiltMazeWindow.Show();
    }

    private void OpenSnowboard()
    {
        if (_snowboardWindow is { IsLoaded: true })
        {
            _snowboardWindow.Activate();
            return;
        }

        _snowboardWindow = new SnowboardWindow
        {
            Owner = this,
        };
        _snowboardWindow.SetConnectionState(_session.IsConnected);
        _snowboardWindow.Closed += (_, _) => _snowboardWindow = null;
        _snowboardWindow.Show();
    }

    private void UpdateReportRate()
    {
        lock (_reportRateSync)
        {
            _reportsInWindow++;
            var now = Stopwatch.GetTimestamp();
            var elapsed = Stopwatch.GetElapsedTime(_reportRateWindowStarted, now).TotalSeconds;
            if (elapsed < 1)
            {
                return;
            }

            _reportRateHz = _reportsInWindow / elapsed;
            _reportsInWindow = 0;
            _reportRateWindowStarted = now;
        }
    }

    private double GetReportRateHz()
    {
        lock (_reportRateSync)
        {
            return _reportRateHz;
        }
    }

    private static string FormatKg(float value) => $"{Math.Max(0, value):0.0} kg";

    private void ApplyDisconnectedState(string message)
    {
        _pedalWindow?.SetConnectionState(false);
        _driveSimulatorWindow?.SetConnectionState(false);
        _gameHubWindow?.SetConnectionState(false);
        _breakoutWindow?.SetConnectionState(false);
        _hoverRaceWindow?.SetConnectionState(false);
        _tiltMazeWindow?.SetConnectionState(false);
        _snowboardWindow?.SetConnectionState(false);
        SetStatus(message, "#FF6B7A");
        ConnectButton.Content = "接続する";
        ConnectButton.IsEnabled = true;
        TareButton.IsEnabled = false;
        RecordButton.IsEnabled = false;
        StopRecording();
    }

    private void SetStatusFromWorker(string status, bool connected)
    {
        _ = Dispatcher.BeginInvoke(() => SetStatus(status, connected ? "#48D7C8" : "#F0A84A"));
    }

    private void SetStatus(string text, string color)
    {
        StatusText.Text = text;
        StatusDot.Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
    }

    private static string TranslateStatus(string status)
    {
        if (status.Contains("Connected", StringComparison.OrdinalIgnoreCase))
        {
            return "接続済み";
        }

        if (status.Contains("No balance board", StringComparison.OrdinalIgnoreCase))
        {
            return "ボードが見つかりません";
        }

        if (status.Contains("disconnected", StringComparison.OrdinalIgnoreCase))
        {
            return "切断されました";
        }

        return status;
    }
}
