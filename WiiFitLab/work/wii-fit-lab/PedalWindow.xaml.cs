using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using BalanceBoard.Core.Models;
using BalanceBoard.Core.Processing;
using BalanceBoard.Core.Services.Output;

namespace WiiFitLab;

public partial class PedalWindow : Window
{
    private readonly PedalKeyboardOutput _keyboard = new(new Win32InputBackend());
    private readonly VJoyController _vJoy = new();
    private readonly DispatcherTimer _watchdog;
    private long _lastUpdateAt;
    private double _currentBrakeKg;
    private double _currentAcceleratorKg;
    private double _restBrakeKg;
    private double _restAcceleratorKg;
    private double _maximumBrakeKg;
    private double _maximumAcceleratorKg;
    private double _smoothedBrake;
    private double _smoothedAccelerator;
    private bool _hasRest;
    private bool _hasBrakeMaximum;
    private bool _hasAcceleratorMaximum;
    private bool _connected;

    public PedalWindow()
    {
        InitializeComponent();
        _vJoy.Log += message => OutputStatusText.Text = message;
        _watchdog = new DispatcherTimer(TimeSpan.FromMilliseconds(100), DispatcherPriority.Normal, Watchdog_Tick, Dispatcher);
        _watchdog.Start();
        Closed += (_, _) =>
        {
            _watchdog.Stop();
            ReleaseOutputs();
            _vJoy.Dispose();
        };
    }

    public void SetConnectionState(bool connected)
    {
        _connected = connected;
        if (!connected)
        {
            OutputEnabledCheckBox.IsChecked = false;
            OutputEnabledCheckBox.IsEnabled = false;
            ReleaseOutputs();
            SignalStatusText.Text = "切断中・出力解除";
            SignalStatusText.Foreground = BrushFrom("#FF6B7A");
        }
    }

    public void Update(ProcessedBalance data)
    {
        _lastUpdateAt = Stopwatch.GetTimestamp();
        _connected = true;
        OutputEnabledCheckBox.IsEnabled = _hasRest && _hasBrakeMaximum && _hasAcceleratorMaximum;
        (_currentBrakeKg, _currentAcceleratorKg) = PedalMath.ComputeSideLoads(data);
        BrakeLoadText.Text = $"{_currentBrakeKg:0.0} kg";
        AcceleratorLoadText.Text = $"{_currentAcceleratorKg:0.0} kg";
        SignalStatusText.Text = "信号受信中";
        SignalStatusText.Foreground = BrushFrom("#48D7C8");

        if (!_hasRest || !_hasBrakeMaximum || !_hasAcceleratorMaximum)
        {
            return;
        }

        var deadzone = DeadzoneSlider.Value / 100.0;
        var exponent = ResponseCurveCombo.SelectedIndex switch
        {
            0 => 0.65,
            2 => 1.5,
            _ => 1.0,
        };
        var brake = PedalMath.Normalize(_currentBrakeKg, _restBrakeKg, _maximumBrakeKg, deadzone, exponent);
        var accelerator = PedalMath.Normalize(
            _currentAcceleratorKg,
            _restAcceleratorKg,
            _maximumAcceleratorKg,
            deadzone,
            exponent);

        const double smoothing = 0.30;
        _smoothedBrake += (brake - _smoothedBrake) * smoothing;
        _smoothedAccelerator += (accelerator - _smoothedAccelerator) * smoothing;
        DisplayPedals();

        if (OutputEnabledCheckBox.IsChecked == true)
        {
            ApplyOutputs();
        }
    }

    private void CaptureRest_Click(object sender, RoutedEventArgs e)
    {
        _restBrakeKg = _currentBrakeKg;
        _restAcceleratorKg = _currentAcceleratorKg;
        _hasRest = true;
        _hasBrakeMaximum = false;
        _hasAcceleratorMaximum = false;
        _smoothedBrake = 0;
        _smoothedAccelerator = 0;
        ReleaseOutputs();
        UpdateCalibrationState("基準を記録しました。次に左足を強く踏んでください。");
    }

    private void CaptureBrakeMax_Click(object sender, RoutedEventArgs e)
    {
        if (!_hasRest)
        {
            UpdateCalibrationState("先に基準を記録してください。");
            return;
        }

        if (_currentBrakeKg < _restBrakeKg + 1)
        {
            UpdateCalibrationState("左足をもう少し強く踏んだ状態で記録してください。");
            return;
        }

        _maximumBrakeKg = _currentBrakeKg;
        _hasBrakeMaximum = true;
        UpdateCalibrationState("左最大を記録しました。次に右足を強く踏んでください。");
    }

    private void CaptureAcceleratorMax_Click(object sender, RoutedEventArgs e)
    {
        if (!_hasRest)
        {
            UpdateCalibrationState("先に基準を記録してください。");
            return;
        }

        if (_currentAcceleratorKg < _restAcceleratorKg + 1)
        {
            UpdateCalibrationState("右足をもう少し強く踏んだ状態で記録してください。");
            return;
        }

        _maximumAcceleratorKg = _currentAcceleratorKg;
        _hasAcceleratorMaximum = true;
        UpdateCalibrationState("右最大を記録しました。");
    }

    private void ResetCalibration_Click(object sender, RoutedEventArgs e)
    {
        _hasRest = false;
        _hasBrakeMaximum = false;
        _hasAcceleratorMaximum = false;
        _smoothedBrake = 0;
        _smoothedAccelerator = 0;
        ReleaseOutputs();
        DisplayPedals();
        UpdateCalibrationState("校正をリセットしました。");
    }

    private void UpdateCalibrationState(string message)
    {
        CalibrationText.Text =
            $"基準 {FormatCalibration(_hasRest, _restBrakeKg)} / {FormatCalibration(_hasRest, _restAcceleratorKg)} kg\n" +
            $"最大 {FormatCalibration(_hasBrakeMaximum, _maximumBrakeKg)} / {FormatCalibration(_hasAcceleratorMaximum, _maximumAcceleratorKg)} kg";
        var complete = _hasRest && _hasBrakeMaximum && _hasAcceleratorMaximum;
        OutputEnabledCheckBox.IsEnabled = complete && _connected;
        OutputStatusText.Text = complete ? "校正完了。出力方式を選んで有効化できます。" : message;
    }

    private static string FormatCalibration(bool available, double value) => available ? value.ToString("0.0") : "—";

    private void OutputEnabledCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (OutputEnabledCheckBox.IsChecked != true)
        {
            ReleaseOutputs();
            OutputStatusText.Text = "出力OFF";
            return;
        }

        if (!_connected || !_hasRest || !_hasBrakeMaximum || !_hasAcceleratorMaximum)
        {
            OutputEnabledCheckBox.IsChecked = false;
            return;
        }

        if (OutputModeCombo.SelectedIndex == 2 && !_vJoy.Initialize(attemptCleanupOnBusy: false))
        {
            OutputEnabledCheckBox.IsChecked = false;
            OutputStatusText.Text = "vJoyを開始できません: " + (_vJoy.LastError ?? "不明なエラー");
            return;
        }

        OutputStatusText.Text = OutputModeCombo.SelectedIndex switch
        {
            1 => "キーボード出力中：右足 W / 左足 S",
            2 => "vJoy出力中：Z アクセル / Rz ブレーキ",
            _ => "表示のみ：外部入力は送信しません",
        };
    }

    private void OutputModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded)
        {
            return;
        }

        OutputEnabledCheckBox.IsChecked = false;
        ReleaseOutputs();
        OutputStatusText.Text = OutputModeCombo.SelectedIndex == 2
            ? "vJoyドライバーとデバイス1が必要です"
            : "出力方式を変更しました。確認後に出力を有効化してください。";
    }

    private void TuningSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsLoaded)
        {
            return;
        }

        DeadzoneText.Text = $"{DeadzoneSlider.Value:0} %";
        KeyboardThresholdText.Text = $"{KeyboardThresholdSlider.Value:0} %";
    }

    private void DisplayPedals()
    {
        var brakePercent = Math.Clamp(_smoothedBrake * 100, 0, 100);
        var acceleratorPercent = Math.Clamp(_smoothedAccelerator * 100, 0, 100);
        BrakePercentText.Text = brakePercent.ToString("0");
        AcceleratorPercentText.Text = acceleratorPercent.ToString("0");
        BrakeProgress.Value = brakePercent;
        AcceleratorProgress.Value = acceleratorPercent;
    }

    private void ApplyOutputs()
    {
        if (OutputModeCombo.SelectedIndex == 1)
        {
            var threshold = KeyboardThresholdSlider.Value / 100.0;
            _keyboard.Update(_smoothedAccelerator, _smoothedBrake, threshold);
        }
        else if (OutputModeCombo.SelectedIndex == 2 && _vJoy.IsReady)
        {
            _vJoy.Update(new ProcessedBalance
            {
                JoyZ = PedalMath.ToPositiveVJoyAxis(_smoothedAccelerator),
                JoyRz = PedalMath.ToPositiveVJoyAxis(_smoothedBrake),
            }, new AppSettings());
        }
    }

    private void ReleaseOutputs()
    {
        _keyboard.ReleaseAll();
        if (_vJoy.IsReady)
        {
            _vJoy.Center();
            _vJoy.Shutdown();
        }
    }

    private void Watchdog_Tick(object? sender, EventArgs e)
    {
        if (_lastUpdateAt == 0 || Stopwatch.GetElapsedTime(_lastUpdateAt).TotalMilliseconds <= 250)
        {
            return;
        }

        ReleaseOutputs();
        SignalStatusText.Text = "信号停止・出力解除";
        SignalStatusText.Foreground = BrushFrom("#FF6B7A");
    }

    private static SolidColorBrush BrushFrom(string color) =>
        new((Color)ColorConverter.ConvertFromString(color));
}
