namespace WiiFitVRBridge.Core;

public sealed class LocomotionEngine
{
    public static readonly TimeSpan SignalTimeout = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan FilterTimeConstant = TimeSpan.FromMilliseconds(80);
    private static readonly TimeSpan StepDebounce = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan WalkDecay = TimeSpan.FromMilliseconds(450);
    private readonly object _sync = new();
    private MovementMode _mode;
    private CalibrationProfile _profile;
    private BoardSignal? _filtered;
    private DateTimeOffset? _lastSignalAt;
    private DateTimeOffset? _lastStepAt;
    private DateTimeOffset? _previousStepAt;
    private int _lastWalkPhase;
    private int _stepCount;
    private double _cadenceOutput;
    private LocomotionFrame _latestFrame;

    public LocomotionEngine(MovementMode mode, CalibrationProfile profile)
    {
        _mode = mode;
        _profile = profile.Clone();
        _latestFrame = LocomotionFrame.Zero(mode);
    }

    public MovementMode Mode
    {
        get { lock (_sync) return _mode; }
    }

    public BoardSignal? FilteredSignal
    {
        get { lock (_sync) return _filtered; }
    }

    public void ChangeMode(MovementMode mode, CalibrationProfile profile)
    {
        lock (_sync)
        {
            _mode = mode;
            _profile = profile.Clone();
            _filtered = null;
            _lastSignalAt = null;
            ResetWalkingState();
            _latestFrame = LocomotionFrame.Zero(mode);
        }
    }

    public void UpdateProfile(CalibrationProfile profile)
    {
        lock (_sync)
        {
            _profile = profile.Clone();
        }
    }

    public LocomotionFrame Process(BoardSignal signal, bool outputArmed)
    {
        lock (_sync)
        {
            var filtered = ApplyFilter(signal);
            _lastSignalAt = signal.Timestamp;
            var riderThreshold = Math.Max(8, _profile.NeutralWeightKg * 0.15);
            var riderPresent = filtered.TotalWeightKg >= riderThreshold;

            if (!riderPresent || !_profile.IsCalibrated)
            {
                ResetWalkingState();
                _latestFrame = LocomotionFrame.Zero(_mode, riderPresent, _stepCount);
                return _latestFrame;
            }

            var (vertical, horizontal) = _mode switch
            {
                MovementMode.Walking => ProcessWalking(filtered),
                MovementMode.Ski => ProcessSki(filtered),
                MovementMode.Snowboard => ProcessSnowboard(filtered),
                _ => (0d, 0d),
            };

            var enabled = outputArmed;
            _latestFrame = new LocomotionFrame(
                enabled ? (float)Math.Clamp(vertical, -1, 1) : 0,
                enabled ? (float)Math.Clamp(horizontal, -1, 1) : 0,
                enabled,
                _mode,
                riderPresent,
                _stepCount);
            return _latestFrame;
        }
    }

    public LocomotionFrame GetLatestFrame(DateTimeOffset now, bool outputArmed)
    {
        lock (_sync)
        {
            if (_lastSignalAt is null || now - _lastSignalAt > SignalTimeout || !outputArmed)
            {
                return LocomotionFrame.Zero(_mode, false, _stepCount);
            }

            if (_mode == MovementMode.Walking && _filtered is not null && _latestFrame.RiderPresent)
            {
                var walking = ProcessWalkingDecay(now);
                return new LocomotionFrame(
                    (float)walking,
                    0,
                    _latestFrame.Enabled,
                    _mode,
                    true,
                    _stepCount);
            }
            return _latestFrame;
        }
    }

    public void EmergencyStop()
    {
        lock (_sync)
        {
            ResetWalkingState();
            _latestFrame = LocomotionFrame.Zero(_mode);
        }
    }

    public static CalibrationProfile Calibrate(
        IEnumerable<BoardSignal> samples,
        CalibrationProfile? current = null)
    {
        var values = samples.Where(value => value.TotalWeightKg >= 3).ToArray();
        if (values.Length < 10)
        {
            throw new InvalidOperationException("ニュートラル校正には10件以上の乗車サンプルが必要です。");
        }

        var profile = current?.Clone() ?? new CalibrationProfile();
        profile.CenterX = values.Average(value => value.BalanceX);
        profile.CenterY = values.Average(value => value.BalanceY);
        profile.NeutralWeightKg = values.Average(value => value.TotalWeightKg);
        profile.DeadzoneX = Math.Clamp(Math.Max(5, StandardDeviation(values.Select(value => value.BalanceX)) * 3), 5, 12);
        profile.DeadzoneY = Math.Clamp(Math.Max(5, StandardDeviation(values.Select(value => value.BalanceY)) * 3), 5, 12);
        profile.IsCalibrated = true;
        return profile;
    }

    private BoardSignal ApplyFilter(BoardSignal signal)
    {
        if (_filtered is null)
        {
            _filtered = signal;
            return signal;
        }

        var dt = Math.Clamp((signal.Timestamp - _filtered.Timestamp).TotalSeconds, 0.001, 0.25);
        var alpha = 1 - Math.Exp(-dt / FilterTimeConstant.TotalSeconds);
        double Lerp(double from, double to) => from + (to - from) * alpha;
        _filtered = new BoardSignal(
            Lerp(_filtered.TopLeftKg, signal.TopLeftKg),
            Lerp(_filtered.TopRightKg, signal.TopRightKg),
            Lerp(_filtered.BottomLeftKg, signal.BottomLeftKg),
            Lerp(_filtered.BottomRightKg, signal.BottomRightKg),
            Lerp(_filtered.TotalWeightKg, signal.TotalWeightKg),
            Lerp(_filtered.BalanceX, signal.BalanceX),
            Lerp(_filtered.BalanceY, signal.BalanceY),
            signal.Timestamp);
        return _filtered;
    }

    private (double Vertical, double Horizontal) ProcessWalking(BoardSignal signal)
    {
        var delta = signal.BalanceX - _profile.CenterX;
        var phase = delta >= 6 ? 1 : delta <= -6 ? -1 : 0;
        if (phase != 0 && phase != _lastWalkPhase)
        {
            var mayStep = _lastStepAt is null || signal.Timestamp - _lastStepAt >= StepDebounce;
            if (_lastWalkPhase != 0 && mayStep)
            {
                _previousStepAt = _lastStepAt;
                _lastStepAt = signal.Timestamp;
                _stepCount++;
                if (_previousStepAt is not null)
                {
                    var interval = Math.Clamp((_lastStepAt.Value - _previousStepAt.Value).TotalSeconds, 0.2, 2.0);
                    var cadenceHz = 1 / interval;
                    _cadenceOutput = Math.Clamp(0.25 + (cadenceHz - 0.5) / 1.5 * 0.75, 0.25, 1);
                }
                else
                {
                    _cadenceOutput = 0.25;
                }
            }
            _lastWalkPhase = phase;
        }

        return (ProcessWalkingDecay(signal.Timestamp), 0);
    }

    private double ProcessWalkingDecay(DateTimeOffset now)
    {
        if (_lastStepAt is null)
        {
            return 0;
        }
        var age = now - _lastStepAt.Value;
        if (age >= WalkDecay)
        {
            return 0;
        }
        var hold = TimeSpan.FromMilliseconds(150);
        var factor = age <= hold
            ? 1
            : 1 - (age - hold).TotalSeconds / (WalkDecay - hold).TotalSeconds;
        return _cadenceOutput * Math.Clamp(factor, 0, 1) * _profile.MaximumOutput;
    }

    private (double Vertical, double Horizontal) ProcessSki(BoardSignal signal)
    {
        var horizontal = ShapeAxis(signal.BalanceX - _profile.CenterX, _profile.HorizontalRange, _profile.DeadzoneX);
        var longitudinal = ShapeAxis(_profile.CenterY - signal.BalanceY, _profile.VerticalRange, _profile.DeadzoneY);
        horizontal = ApplyDirection(horizontal, _profile.InvertHorizontal);
        longitudinal = ApplyDirection(longitudinal, _profile.InvertVertical);
        var vertical = longitudinal >= 0 ? 0.35 + longitudinal * 0.65 : 0.35 + longitudinal * 0.35;
        return (vertical * _profile.MaximumOutput, horizontal * _profile.MaximumOutput);
    }

    private (double Vertical, double Horizontal) ProcessSnowboard(BoardSignal signal)
    {
        var horizontal = ShapeAxis(signal.BalanceY - _profile.CenterY, _profile.HorizontalRange, _profile.DeadzoneY);
        var stanceSign = _profile.Stance == SnowboardStance.Goofy ? -1 : 1;
        var longitudinal = ShapeAxis((signal.BalanceX - _profile.CenterX) * stanceSign, _profile.VerticalRange, _profile.DeadzoneX);
        horizontal = ApplyDirection(horizontal, _profile.InvertHorizontal);
        longitudinal = ApplyDirection(longitudinal, _profile.InvertVertical);
        var vertical = longitudinal >= 0 ? 0.35 + longitudinal * 0.65 : 0.35 + longitudinal * 0.35;
        return (vertical * _profile.MaximumOutput, horizontal * _profile.MaximumOutput);
    }

    private double ShapeAxis(double delta, double range, double deadzone)
    {
        var absolute = Math.Abs(delta);
        if (absolute <= deadzone)
        {
            return 0;
        }
        var denominator = Math.Max(1, range - deadzone);
        var normalized = Math.Clamp((absolute - deadzone) / denominator, 0, 1);
        var shaped = Math.Pow(normalized, 1.4) * Math.Clamp(_profile.Sensitivity, 0.5, 3);
        return Math.Sign(delta) * Math.Clamp(shaped, 0, 1);
    }

    private static double ApplyDirection(double value, bool invert) => invert ? -value : value;

    private static double StandardDeviation(IEnumerable<double> sequence)
    {
        var values = sequence.ToArray();
        var mean = values.Average();
        return Math.Sqrt(values.Sum(value => Math.Pow(value - mean, 2)) / values.Length);
    }

    private void ResetWalkingState()
    {
        _lastWalkPhase = 0;
        _lastStepAt = null;
        _previousStepAt = null;
        _cadenceOutput = 0;
    }
}
