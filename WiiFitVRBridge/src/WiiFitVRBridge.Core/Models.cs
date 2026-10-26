namespace WiiFitVRBridge.Core;

public enum MovementMode
{
    Walking,
    Ski,
    Snowboard,
}

public enum SnowboardStance
{
    Regular,
    Goofy,
}

public sealed record BoardSignal(
    double TopLeftKg,
    double TopRightKg,
    double BottomLeftKg,
    double BottomRightKg,
    double TotalWeightKg,
    double BalanceX,
    double BalanceY,
    DateTimeOffset Timestamp);

public sealed class CalibrationProfile
{
    public double CenterX { get; set; } = 50;
    public double CenterY { get; set; } = 50;
    public double NeutralWeightKg { get; set; } = 60;
    public double HorizontalRange { get; set; } = 17;
    public double VerticalRange { get; set; } = 17;
    public double DeadzoneX { get; set; } = 5;
    public double DeadzoneY { get; set; } = 5;
    public double Sensitivity { get; set; } = 1;
    public double MaximumOutput { get; set; } = 1;
    public bool InvertHorizontal { get; set; }
    public bool InvertVertical { get; set; }
    public SnowboardStance Stance { get; set; } = SnowboardStance.Regular;
    public bool IsCalibrated { get; set; }

    public CalibrationProfile Clone() => (CalibrationProfile)MemberwiseClone();
}

public readonly record struct LocomotionFrame(
    float Vertical,
    float Horizontal,
    bool Enabled,
    MovementMode Mode,
    bool RiderPresent,
    int StepCount)
{
    public static LocomotionFrame Zero(MovementMode mode, bool riderPresent = false, int stepCount = 0) =>
        new(0, 0, false, mode, riderPresent, stepCount);
}

public sealed record OscDiagnostic(
    string TargetHost,
    int TargetPort,
    bool IsAvailable,
    DateTimeOffset? LastSentAt,
    string Status,
    string? LastError);

public interface IVrchatOscOutput : IDisposable
{
    OscDiagnostic Diagnostic { get; }
    void SetTarget(string host, int port);
    ValueTask SendFrameAsync(LocomotionFrame frame, CancellationToken cancellationToken = default);
    ValueTask ResetAsync(CancellationToken cancellationToken = default);
}
