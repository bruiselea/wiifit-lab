using WiiFitVRBridge.Core;

namespace WiiFitVRBridge.Tests;

public sealed class LocomotionEngineTests
{
    private static readonly DateTimeOffset Epoch = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void CalibrationRecordsCenterWeightAndNoiseDeadzone()
    {
        var samples = Enumerable.Range(0, 30)
            .Select(index => Signal(49.5 + index % 2, 50.5 - index % 2, Epoch.AddMilliseconds(index * 16)))
            .ToArray();

        var profile = LocomotionEngine.Calibrate(samples);

        Assert.True(profile.IsCalibrated);
        Assert.Equal(50, profile.CenterX, 5);
        Assert.Equal(50, profile.CenterY, 5);
        Assert.Equal(60, profile.NeutralWeightKg, 5);
        Assert.Equal(5, profile.DeadzoneX);
        Assert.Equal(5, profile.DeadzoneY);
    }

    [Fact]
    public void SkiMapsNeutralForwardBackAndTurn()
    {
        var engine = new LocomotionEngine(MovementMode.Ski, Calibrated());

        var neutral = engine.Process(Signal(50, 50, Epoch), true);
        engine.ChangeMode(MovementMode.Ski, Calibrated());
        var forward = engine.Process(Signal(50, 30, Epoch), true);
        engine.ChangeMode(MovementMode.Ski, Calibrated());
        var back = engine.Process(Signal(50, 70, Epoch), true);
        engine.ChangeMode(MovementMode.Ski, Calibrated());
        var turn = engine.Process(Signal(70, 50, Epoch), true);

        Assert.Equal(0.35f, neutral.Vertical, 3);
        Assert.Equal(1f, forward.Vertical, 3);
        Assert.Equal(0f, back.Vertical, 3);
        Assert.Equal(1f, turn.Horizontal, 3);
    }

    [Fact]
    public void AxisInversionIsApplied()
    {
        var profile = Calibrated();
        profile.InvertHorizontal = true;
        profile.InvertVertical = true;
        var engine = new LocomotionEngine(MovementMode.Ski, profile);

        var frame = engine.Process(Signal(70, 30, Epoch), true);

        Assert.Equal(-1f, frame.Horizontal, 3);
        Assert.Equal(0f, frame.Vertical, 3);
    }

    [Fact]
    public void SnowboardStanceSwapsFrontFootDirection()
    {
        var regular = Calibrated();
        var regularEngine = new LocomotionEngine(MovementMode.Snowboard, regular);
        var regularFrame = regularEngine.Process(Signal(70, 30, Epoch), true);
        var goofy = Calibrated();
        goofy.Stance = SnowboardStance.Goofy;
        var goofyEngine = new LocomotionEngine(MovementMode.Snowboard, goofy);
        var goofyFrame = goofyEngine.Process(Signal(70, 30, Epoch), true);

        Assert.Equal(1f, regularFrame.Vertical, 3);
        Assert.Equal(0f, goofyFrame.Vertical, 3);
        Assert.Equal(-1f, regularFrame.Horizontal, 3);
        Assert.Equal(-1f, goofyFrame.Horizontal, 3);
    }

    [Fact]
    public void WalkingDetectsTenAlternatingStepsWithinTolerance()
    {
        var engine = new LocomotionEngine(MovementMode.Walking, Calibrated());
        var time = Epoch;

        for (var phase = 0; phase < 11; phase++)
        {
            var x = phase % 2 == 0 ? 65d : 35d;
            for (var sample = 0; sample < 15; sample++)
            {
                engine.Process(Signal(x, 50, time), true);
                time = time.AddMilliseconds(20);
            }
        }

        var result = engine.GetLatestFrame(time.AddMilliseconds(-20), true);
        Assert.InRange(result.StepCount, 9, 11);
    }

    [Fact]
    public void WalkingDecaysAndStaleSignalAlwaysZeros()
    {
        var engine = new LocomotionEngine(MovementMode.Walking, Calibrated());
        engine.Process(Signal(65, 50, Epoch), true);
        engine.Process(Signal(35, 50, Epoch.AddMilliseconds(250)), true);

        var fresh = engine.GetLatestFrame(Epoch.AddMilliseconds(300), true);
        var stale = engine.GetLatestFrame(Epoch.AddMilliseconds(501), true);

        Assert.True(fresh.Vertical > 0);
        Assert.Equal(0, stale.Vertical);
        Assert.False(stale.Enabled);
    }

    [Fact]
    public void DisarmedOutputIsAlwaysZero()
    {
        var engine = new LocomotionEngine(MovementMode.Ski, Calibrated());
        var frame = engine.Process(Signal(70, 30, Epoch), false);

        Assert.Equal(0, frame.Vertical);
        Assert.Equal(0, frame.Horizontal);
        Assert.False(frame.Enabled);
    }

    private static CalibrationProfile Calibrated() => new()
    {
        CenterX = 50,
        CenterY = 50,
        NeutralWeightKg = 60,
        HorizontalRange = 17,
        VerticalRange = 17,
        DeadzoneX = 5,
        DeadzoneY = 5,
        Sensitivity = 1,
        MaximumOutput = 1,
        IsCalibrated = true,
    };

    private static BoardSignal Signal(double x, double y, DateTimeOffset timestamp, double weight = 60) =>
        new(weight / 4, weight / 4, weight / 4, weight / 4, weight, x, y, timestamp);
}
