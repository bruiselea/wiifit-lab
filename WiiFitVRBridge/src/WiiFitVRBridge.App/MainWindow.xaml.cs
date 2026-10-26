using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Media;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using BalanceBoard.Core.Models;
using BalanceBoard.Core.Services.Session;
using WiiFitVRBridge.Core;

namespace WiiFitVRBridge.App;

public partial class MainWindow : Window
{
    private const string BridgeRoot = @"E:\WiiFitVRBridge";
    private static readonly string SettingsPath = Path.Combine(BridgeRoot, "data", "bridge-settings.json");
    private const int HotkeyWalk = 0xB801;
    private const int HotkeySki = 0xB802;
    private const int HotkeySnowboard = 0xB803;
    private const int HotkeyEmergency = 0xB804;
    private const int WmHotkey = 0x0312;
    private readonly BalanceBoardSession _session = new();
    private readonly BridgeSettings _settings;
    private readonly LocomotionEngine _engine;
    private readonly VrchatOscOutput _oscOutput;
    private readonly OscOutputPump _outputPump;
    private readonly DispatcherTimer _uiTimer;
    private readonly object _sampleSync = new();
    private readonly Queue<BoardSignal> _recentSamples = new();
    private volatile bool _outputArmed;
    private BoardSignal? _latestSignal;
    private bool _connecting;
    private bool _closing;
    private bool _shutdownComplete;
    private HwndSource? _windowSource;

    public MainWindow()
    {
        InitializeComponent();
        Directory.CreateDirectory(Path.Combine(BridgeRoot, "data"));
        Directory.CreateDirectory(Path.Combine(BridgeRoot, "outputs"));
        _settings = BridgeSettingsStore.Load(SettingsPath);
        _settings.OutputEnabled = false;
        _outputArmed = false;
        _engine = new LocomotionEngine(_settings.ActiveMode, _settings.ProfileFor(_settings.ActiveMode));
        _oscOutput = new VrchatOscOutput(_settings.OscHost, _settings.OscPort);
        _outputPump = new OscOutputPump(_oscOutput, () =>
            _engine.GetLatestFrame(DateTimeOffset.UtcNow, _outputArmed));

        var boardSettings = new AppSettings
        {
            EnableVJoy = false,
            DisableKeyboardActions = true,
            AutoConnectOnStartup = true,
            AutoTareOnConnect = true,
            HasConnectedBefore = _settings.BoardConnection.HasConnectedBefore,
            LastConnectedDeviceId = _settings.BoardConnection.LastConnectedDeviceId,
            LastConnectedAtUtc = _settings.BoardConnection.LastConnectedAtUtc,
            LastBluetoothAdapterMac = _settings.BoardConnection.LastBluetoothAdapterMac,
        };
        _session.LoadSettings(boardSettings, initializeVJoy: false);
        _session.Processed += OnBoardProcessed;
        _session.StatusChanged += message => Dispatcher.BeginInvoke(() => SetBoardStatus(message));
        _session.Log += message =>
        {
            if (message.Contains("error", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("failed", StringComparison.OrdinalIgnoreCase))
            {
                Dispatcher.BeginInvoke(() => StatusDetailText.Text = message);
            }
        };

        _uiTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(100), DispatcherPriority.Background, UpdateUi, Dispatcher);
        Loaded += MainWindow_Loaded;
        SourceInitialized += MainWindow_SourceInitialized;
        Closing += MainWindow_Closing;
        ApplyModeToUi();
        _uiTimer.Start();
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        _outputPump.Start();
        await DiscoverOscAsync();
        await ConnectBoardAsync();
    }

    private void MainWindow_SourceInitialized(object? sender, EventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;
        _windowSource = HwndSource.FromHwnd(handle);
        _windowSource.AddHook(WindowMessageHook);
        RegisterHotKey(handle, HotkeyWalk, 0, 0x77);      // F8
        RegisterHotKey(handle, HotkeySki, 0, 0x78);       // F9
        RegisterHotKey(handle, HotkeySnowboard, 0, 0x79); // F10
        RegisterHotKey(handle, HotkeyEmergency, 0, 0x7B); // F12
    }

    private IntPtr WindowMessageHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WmHotkey)
        {
            return IntPtr.Zero;
        }
        handled = true;
        switch (wParam.ToInt32())
        {
            case HotkeyWalk:
                _ = SwitchModeAsync(MovementMode.Walking);
                break;
            case HotkeySki:
                _ = SwitchModeAsync(MovementMode.Ski);
                break;
            case HotkeySnowboard:
                _ = SwitchModeAsync(MovementMode.Snowboard);
                break;
            case HotkeyEmergency:
                _ = EmergencyStopAsync("F12 緊急停止");
                break;
        }
        return IntPtr.Zero;
    }

    private void OnBoardProcessed(ProcessedBalance data)
    {
        var signal = new BoardSignal(
            data.TopLeftKg,
            data.TopRightKg,
            data.BottomLeftKg,
            data.BottomRightKg,
            data.TemperatureCorrectedWeightKg,
            data.BalanceX,
            data.BalanceY,
            DateTimeOffset.UtcNow);
        _latestSignal = signal;
        lock (_sampleSync)
        {
            _recentSamples.Enqueue(signal);
            var cutoff = signal.Timestamp - TimeSpan.FromSeconds(1.5);
            while (_recentSamples.Count > 0 && _recentSamples.Peek().Timestamp < cutoff)
            {
                _recentSamples.Dequeue();
            }
        }
        _engine.Process(signal, _outputArmed);
    }

    private async Task ConnectBoardAsync()
    {
        if (_connecting || _session.IsConnected)
        {
            return;
        }
        _connecting = true;
        ConnectButton.IsEnabled = false;
        SetBoardStatus("ボードを検索中…");
        try
        {
            var result = await _session.ConnectWithIntentAsync(ConnectionIntent.QuickReconnect);
            if (result.IsSuccess)
            {
                SetBoardStatus("接続済み");
                SyncConnectionSettings();
                SaveSettings();
                PlayCue(SystemSounds.Asterisk);
            }
            else
            {
                SetBoardStatus(result.Status == ConnectStatus.AlreadyInProgress
                    ? "接続処理中"
                    : "未接続・ボードの電源を押してください");
                StatusDetailText.Text = result.Message ?? $"接続結果: {result.Status}";
            }
        }
        catch (Exception ex)
        {
            SetBoardStatus("接続エラー");
            StatusDetailText.Text = ex.Message;
        }
        finally
        {
            _connecting = false;
            ConnectButton.IsEnabled = true;
        }
    }

    private void SetBoardStatus(string message)
    {
        BoardStatusText.Text = message switch
        {
            "Connected!" => "接続済み",
            "Finding board..." => "ボードを検索中…",
            "Trying again..." => "自動再接続中…",
            _ => message,
        };
        BoardStatusText.Foreground = BrushFrom(_session.IsConnected ? "#55DFC8" : "#F0A84A");
    }

    private async Task DiscoverOscAsync()
    {
        VrchatStatusText.Text = "OSCQueryを検索中…";
        var result = _settings.UseOscQuery
            ? await VrchatOscQueryDiscovery.DiscoverAsync()
            : new OscQueryResult(false, _settings.OscHost, _settings.OscPort, "手動OSC設定");
        _settings.OscHost = result.Found ? result.Host : "127.0.0.1";
        _settings.OscPort = result.Found ? result.Port : 9000;
        _oscOutput.SetTarget(_settings.OscHost, _settings.OscPort);
        OscTargetText.Text = $"{_settings.OscHost}:{_settings.OscPort}";
        VrchatStatusText.Text = result.Status;
        VrchatStatusText.Foreground = BrushFrom(result.Found ? "#55DFC8" : "#F0A84A");
        SaveSettings();
    }

    private async Task SwitchModeAsync(MovementMode mode)
    {
        if (_settings.ActiveMode == mode)
        {
            return;
        }
        _outputArmed = false;
        await SafeResetOscAsync();
        _settings.ActiveMode = mode;
        _settings.OutputEnabled = false;
        _engine.ChangeMode(mode, _settings.ProfileFor(mode));
        ApplyModeToUi();
        SaveSettings();
        PlayCue(SystemSounds.Beep);
        StatusDetailText.Text = $"{ModeName(mode)}へ切替。安全のためOSC出力を停止しました。";
    }

    private void ApplyModeToUi()
    {
        var mode = _settings.ActiveMode;
        WalkingModeButton.Background = BrushFrom(mode == MovementMode.Walking ? "#55DFC8" : "#294158");
        SkiModeButton.Background = BrushFrom(mode == MovementMode.Ski ? "#55DFC8" : "#294158");
        SnowboardModeButton.Background = BrushFrom(mode == MovementMode.Snowboard ? "#55DFC8" : "#294158");
        WalkingModeButton.Foreground = BrushFrom(mode == MovementMode.Walking ? "#061512" : "#F4FAFF");
        SkiModeButton.Foreground = BrushFrom(mode == MovementMode.Ski ? "#061512" : "#F4FAFF");
        SnowboardModeButton.Foreground = BrushFrom(mode == MovementMode.Snowboard ? "#061512" : "#F4FAFF");
        PlacementText.Text = mode switch
        {
            MovementMode.Walking => "通常向きで自然に立ち、1.2秒ほど静止します。",
            MovementMode.Ski => "通常向きで両足を平行に置き、滑走姿勢で静止します。",
            _ => "ボードを90°回し、前足・後ろ足を置いて横向きに静止します。",
        };
        ModeGuideText.Text = mode switch
        {
            MovementMode.Walking => "左右の足へ交互に荷重を移します。大きく足を上げる必要はありません。",
            MovementMode.Ski => "左右荷重でカーブ、前荷重で加速、後荷重で減速します。",
            _ => "つま先／かかとでカーブ、前足で加速、後ろ足で減速します。",
        };
        StancePanel.Visibility = mode == MovementMode.Snowboard ? Visibility.Visible : Visibility.Collapsed;
        var profile = _settings.ProfileFor(mode);
        SensitivitySlider.Value = profile.Sensitivity;
        MaximumOutputSlider.Value = profile.MaximumOutput;
        InvertHorizontalCheck.IsChecked = profile.InvertHorizontal;
        InvertVerticalCheck.IsChecked = profile.InvertVertical;
        StanceCombo.SelectedIndex = profile.Stance == SnowboardStance.Goofy ? 1 : 0;
        CalibrationText.Text = profile.IsCalibrated
            ? $"校正済み  X {profile.CenterX:0.0} / Y {profile.CenterY:0.0}"
            : "未校正";
        CalibrationText.Foreground = BrushFrom(profile.IsCalibrated ? "#55DFC8" : "#F0A84A");
        UpdateOutputButton();
    }

    private void UpdateUi(object? sender, EventArgs e)
    {
        if (_closing)
        {
            return;
        }
        var signal = _latestSignal;
        if (signal is not null)
        {
            WeightText.Text = $"{signal.TotalWeightKg:0.0} kg";
            BalanceXText.Text = $"X {signal.BalanceX:0.0} %";
            BalanceYText.Text = $"Y {signal.BalanceY:0.0} %";
            TopLeftText.Text = $"{signal.TopLeftKg:0.0} kg";
            TopRightText.Text = $"{signal.TopRightKg:0.0} kg";
            BottomLeftText.Text = $"{signal.BottomLeftKg:0.0} kg";
            BottomRightText.Text = $"{signal.BottomRightKg:0.0} kg";
        }
        var frame = _engine.GetLatestFrame(DateTimeOffset.UtcNow, _outputArmed);
        VerticalText.Text = $"Vertical  {frame.Vertical:+0.00;-0.00;0.00}";
        HorizontalText.Text = $"Horizontal  {frame.Horizontal:+0.00;-0.00;0.00}";
        VerticalBar.Value = Math.Abs(frame.Vertical);
        StepText.Text = $"歩数 {frame.StepCount}";
        BridgeStateText.Text = _outputArmed ? $"● {ModeName(frame.Mode)} 出力中" : "● 安全停止中";
        BridgeStateText.Foreground = BrushFrom(_outputArmed ? "#55DFC8" : "#F0A84A");
    }

    private async void WalkingModeButton_Click(object sender, RoutedEventArgs e) => await SwitchModeAsync(MovementMode.Walking);
    private async void SkiModeButton_Click(object sender, RoutedEventArgs e) => await SwitchModeAsync(MovementMode.Ski);
    private async void SnowboardModeButton_Click(object sender, RoutedEventArgs e) => await SwitchModeAsync(MovementMode.Snowboard);
    private async void ConnectButton_Click(object sender, RoutedEventArgs e) => await ConnectBoardAsync();

    private async void DisconnectButton_Click(object sender, RoutedEventArgs e)
    {
        await EmergencyStopAsync("ボード切断");
        _session.Disconnect();
        SetBoardStatus("切断しました");
    }

    private void CalibrateButton_Click(object sender, RoutedEventArgs e)
    {
        BoardSignal[] samples;
        lock (_sampleSync)
        {
            samples = _recentSamples.ToArray();
        }
        try
        {
            var current = _settings.ProfileFor(_settings.ActiveMode);
            var calibrated = LocomotionEngine.Calibrate(samples, current);
            _settings.Profiles[_settings.ActiveMode] = calibrated;
            _engine.UpdateProfile(calibrated);
            ApplyModeToUi();
            SaveSettings();
            StatusDetailText.Text = "ニュートラルを記録しました。次にVRChat出力をONにしてください。";
            PlayCue(SystemSounds.Asterisk);
        }
        catch (InvalidOperationException ex)
        {
            StatusDetailText.Text = ex.Message + " ボードに乗って静止してから再実行してください。";
            PlayCue(SystemSounds.Exclamation);
        }
    }

    private async void OutputButton_Click(object sender, RoutedEventArgs e)
    {
        if (_outputArmed)
        {
            await EmergencyStopAsync("OSC出力を停止しました");
            return;
        }
        if (!_settings.ProfileFor(_settings.ActiveMode).IsCalibrated)
        {
            StatusDetailText.Text = "先に現在のモードでニュートラル校正を行ってください。";
            PlayCue(SystemSounds.Exclamation);
            return;
        }
        _outputArmed = true;
        _settings.OutputEnabled = true;
        UpdateOutputButton();
        SaveSettings();
        PlayCue(SystemSounds.Asterisk);
        StatusDetailText.Text = "VRChat OSC出力中。F12でいつでも停止できます。";
    }

    private async void EmergencyStopButton_Click(object sender, RoutedEventArgs e) =>
        await EmergencyStopAsync("緊急停止しました");

    private async Task EmergencyStopAsync(string message)
    {
        _outputArmed = false;
        _settings.OutputEnabled = false;
        _engine.EmergencyStop();
        await SafeResetOscAsync();
        UpdateOutputButton();
        SaveSettings();
        StatusDetailText.Text = message;
        PlayCue(SystemSounds.Hand);
    }

    private async Task SafeResetOscAsync()
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
            await _oscOutput.ResetAsync(cts.Token);
        }
        catch (Exception ex)
        {
            StatusDetailText.Text = $"OSCゼロ送信エラー: {ex.Message}";
        }
    }

    private async void DiscoverOscButton_Click(object sender, RoutedEventArgs e) => await DiscoverOscAsync();

    private void SensitivitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsLoaded || _settings is null)
        {
            return;
        }
        var profile = _settings.ProfileFor(_settings.ActiveMode);
        profile.Sensitivity = SensitivitySlider.Value;
        SensitivityText.Text = $"{profile.Sensitivity:0.0}倍";
        _engine.UpdateProfile(profile);
        SaveSettings();
    }

    private void MaximumOutputSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsLoaded || _settings is null)
        {
            return;
        }
        var profile = _settings.ProfileFor(_settings.ActiveMode);
        profile.MaximumOutput = MaximumOutputSlider.Value;
        MaximumOutputText.Text = $"{profile.MaximumOutput * 100:0} %";
        _engine.UpdateProfile(profile);
        SaveSettings();
    }

    private void ProfileOption_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _settings is null)
        {
            return;
        }
        var profile = _settings.ProfileFor(_settings.ActiveMode);
        profile.InvertHorizontal = InvertHorizontalCheck.IsChecked == true;
        profile.InvertVertical = InvertVerticalCheck.IsChecked == true;
        _engine.UpdateProfile(profile);
        SaveSettings();
    }

    private void StanceCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (!IsLoaded || _settings is null)
        {
            return;
        }
        var profile = _settings.ProfileFor(_settings.ActiveMode);
        profile.Stance = StanceCombo.SelectedIndex == 1 ? SnowboardStance.Goofy : SnowboardStance.Regular;
        _engine.UpdateProfile(profile);
        SaveSettings();
    }

    private void UpdateOutputButton()
    {
        OutputButton.Content = _outputArmed ? "VRChat出力OFF" : "VRChat出力ON";
        OutputButton.Background = BrushFrom(_outputArmed ? "#FFB347" : "#55DFC8");
    }

    private void SyncConnectionSettings()
    {
        _settings.BoardConnection.HasConnectedBefore = _session.Settings.HasConnectedBefore;
        _settings.BoardConnection.LastConnectedDeviceId = _session.Settings.LastConnectedDeviceId;
        _settings.BoardConnection.LastConnectedAtUtc = _session.Settings.LastConnectedAtUtc;
        _settings.BoardConnection.LastBluetoothAdapterMac = _session.Settings.LastBluetoothAdapterMac;
    }

    private void SaveSettings()
    {
        if (_closing)
        {
            return;
        }
        try
        {
            SyncConnectionSettings();
            BridgeSettingsStore.Save(SettingsPath, _settings);
        }
        catch (Exception ex)
        {
            StatusDetailText.Text = $"設定保存エラー: {ex.Message}";
        }
    }

    private void PlayCue(SystemSound sound)
    {
        if (_settings.AudioCues)
        {
            sound.Play();
        }
    }

    private async void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (_shutdownComplete)
        {
            return;
        }
        e.Cancel = true;
        if (_closing)
        {
            return;
        }
        _closing = true;
        _uiTimer.Stop();
        _outputArmed = false;
        _settings.OutputEnabled = false;
        _engine.EmergencyStop();
        SyncConnectionSettings();
        BridgeSettingsStore.Save(SettingsPath, _settings);
        var handle = new WindowInteropHelper(this).Handle;
        foreach (var id in new[] { HotkeyWalk, HotkeySki, HotkeySnowboard, HotkeyEmergency })
        {
            UnregisterHotKey(handle, id);
        }
        _windowSource?.RemoveHook(WindowMessageHook);
        try { await _outputPump.DisposeAsync(); } catch { }
        _oscOutput.Dispose();
        try
        {
            await Task.Run(_session.Dispose).WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch
        {
            // The process is already safe-zeroed. Do not let a stuck HID teardown prevent exit.
        }
        _shutdownComplete = true;
        Close();
    }

    private static string ModeName(MovementMode mode) => mode switch
    {
        MovementMode.Walking => "歩行",
        MovementMode.Ski => "スキー",
        MovementMode.Snowboard => "スノーボード",
        _ => mode.ToString(),
    };

    private static SolidColorBrush BrushFrom(string color) =>
        new((Color)ColorConverter.ConvertFromString(color));

    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
