using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using WiiFitVRBridge.Core;

namespace WiiFitVRBridge.Tests;

public sealed class OscTransportTests
{
    [Theory]
    [InlineData("/input/Vertical", 0.75f)]
    [InlineData("/input/Horizontal", -0.25f)]
    public void FloatPacketRoundTrips(string address, float value)
    {
        var decoded = OscPacketCodec.DecodeFloat(OscPacketCodec.EncodeFloat(address, value));

        Assert.Equal(address, decoded.Address);
        Assert.Equal(value, decoded.Value);
    }

    [Fact]
    public async Task FrameAndResetUseExpectedAddressesAndFloatValues()
    {
        using var listener = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)listener.Client.LocalEndPoint!).Port;
        using var output = new VrchatOscOutput("127.0.0.1", port);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));

        await output.SendFrameAsync(new LocomotionFrame(0.8f, -0.4f, true, MovementMode.Ski, true, 0));
        var first = OscPacketCodec.DecodeFloat((await listener.ReceiveAsync(timeout.Token)).Buffer);
        var second = OscPacketCodec.DecodeFloat((await listener.ReceiveAsync(timeout.Token)).Buffer);
        await output.ResetAsync(timeout.Token);
        var resetPackets = new List<(string Address, float Value)>();
        for (var index = 0; index < 6; index++)
        {
            resetPackets.Add(OscPacketCodec.DecodeFloat((await listener.ReceiveAsync(timeout.Token)).Buffer));
        }

        Assert.Equal(("/input/Vertical", 0.8f), first);
        Assert.Equal(("/input/Horizontal", -0.4f), second);
        Assert.Equal(3, resetPackets.Count(packet => packet == ("/input/Vertical", 0f)));
        Assert.Equal(3, resetPackets.Count(packet => packet == ("/input/Horizontal", 0f)));
    }

    [Fact]
    public async Task PumpSendsAtApproximatelySixtyHertzAndZerosOnShutdown()
    {
        using var listener = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)listener.Client.LocalEndPoint!).Port;
        using var output = new VrchatOscOutput("127.0.0.1", port);
        var frame = new LocomotionFrame(0.6f, 0.2f, true, MovementMode.Ski, true, 0);
        var pump = new OscOutputPump(output, () => frame);
        pump.Start();
        var stopwatch = Stopwatch.StartNew();
        var packets = new List<(TimeSpan Time, string Address, float Value)>();
        using var receiveWindow = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        try
        {
            while (true)
            {
                var packet = await listener.ReceiveAsync(receiveWindow.Token);
                var decoded = OscPacketCodec.DecodeFloat(packet.Buffer);
                packets.Add((stopwatch.Elapsed, decoded.Address, decoded.Value));
            }
        }
        catch (OperationCanceledException)
        {
            // Expected end of measurement window.
        }

        await pump.DisposeAsync();
        var framePairs = packets.Count / 2d;
        Assert.True(framePairs >= 8, $"Only {framePairs} OSC frames were observed.");
        var measuredSpan = packets[^1].Time - packets[0].Time;
        var measuredHz = (framePairs - 1) / measuredSpan.TotalSeconds;
        Assert.InRange(measuredHz, 45, 75);
    }
}
