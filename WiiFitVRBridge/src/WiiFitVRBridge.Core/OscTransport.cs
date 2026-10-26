using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace WiiFitVRBridge.Core;

public static class OscPacketCodec
{
    public static byte[] EncodeFloat(string address, float value)
    {
        if (string.IsNullOrWhiteSpace(address) || address[0] != '/')
        {
            throw new ArgumentException("OSC address must start with '/'.", nameof(address));
        }
        var addressBytes = EncodePaddedString(address);
        var typeBytes = EncodePaddedString(",f");
        var packet = new byte[addressBytes.Length + typeBytes.Length + 4];
        addressBytes.CopyTo(packet, 0);
        typeBytes.CopyTo(packet, addressBytes.Length);
        BinaryPrimitives.WriteInt32BigEndian(packet.AsSpan(addressBytes.Length + typeBytes.Length),
            BitConverter.SingleToInt32Bits(value));
        return packet;
    }

    public static (string Address, float Value) DecodeFloat(ReadOnlySpan<byte> packet)
    {
        var (address, addressEnd) = DecodePaddedString(packet, 0);
        var (typeTag, typeEnd) = DecodePaddedString(packet, addressEnd);
        if (typeTag != ",f" || packet.Length < typeEnd + 4)
        {
            throw new FormatException("Packet is not a single-float OSC message.");
        }
        var bits = BinaryPrimitives.ReadInt32BigEndian(packet.Slice(typeEnd, 4));
        return (address, BitConverter.Int32BitsToSingle(bits));
    }

    private static byte[] EncodePaddedString(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        var length = ((bytes.Length + 1 + 3) / 4) * 4;
        var padded = new byte[length];
        bytes.CopyTo(padded, 0);
        return padded;
    }

    private static (string Value, int NextOffset) DecodePaddedString(ReadOnlySpan<byte> packet, int offset)
    {
        var terminator = packet[offset..].IndexOf((byte)0);
        if (terminator < 0)
        {
            throw new FormatException("OSC string is not null-terminated.");
        }
        var value = Encoding.UTF8.GetString(packet.Slice(offset, terminator));
        var next = offset + ((terminator + 1 + 3) / 4) * 4;
        return (value, next);
    }
}

public sealed class VrchatOscOutput : IVrchatOscOutput
{
    private readonly UdpClient _client = new(AddressFamily.InterNetwork);
    private IPEndPoint _target;
    private DateTimeOffset? _lastSentAt;
    private string? _lastError;
    private bool _disposed;

    public VrchatOscOutput(string host = "127.0.0.1", int port = 9000)
    {
        _target = ResolveTarget(host, port);
    }

    public OscDiagnostic Diagnostic => new(
        _target.Address.ToString(),
        _target.Port,
        !_disposed && _lastError is null,
        _lastSentAt,
        _disposed ? "停止" : "OSC送信準備完了",
        _lastError);

    public void SetTarget(string host, int port)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _target = ResolveTarget(host, port);
        _lastError = null;
    }

    public async ValueTask SendFrameAsync(LocomotionFrame frame, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        try
        {
            await SendFloatAsync("/input/Vertical", frame.Enabled ? frame.Vertical : 0, cancellationToken);
            await SendFloatAsync("/input/Horizontal", frame.Enabled ? frame.Horizontal : 0, cancellationToken);
            _lastSentAt = DateTimeOffset.UtcNow;
            _lastError = null;
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
        {
            _lastError = ex.Message;
            throw;
        }
    }

    public async ValueTask ResetAsync(CancellationToken cancellationToken = default)
    {
        for (var index = 0; index < 3; index++)
        {
            await SendFloatAsync("/input/Vertical", 0, cancellationToken);
            await SendFloatAsync("/input/Horizontal", 0, cancellationToken);
            if (index < 2)
            {
                await Task.Delay(15, cancellationToken);
            }
        }
        _lastSentAt = DateTimeOffset.UtcNow;
    }

    private async ValueTask SendFloatAsync(string address, float value, CancellationToken cancellationToken)
    {
        var packet = OscPacketCodec.EncodeFloat(address, value);
        await _client.SendAsync(packet, _target, cancellationToken);
    }

    private static IPEndPoint ResolveTarget(string host, int port)
    {
        if (port is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(port));
        }
        var address = IPAddress.TryParse(host, out var parsed)
            ? parsed
            : Dns.GetHostAddresses(host).First(value => value.AddressFamily == AddressFamily.InterNetwork);
        return new IPEndPoint(address, port);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _client.Dispose();
    }
}

public sealed record OscQueryResult(bool Found, string Host, int Port, string Status);

public static class VrchatOscQueryDiscovery
{
    public static async Task<OscQueryResult> DiscoverAsync(CancellationToken cancellationToken = default)
    {
        if (!Process.GetProcessesByName("VRChat").Any())
        {
            return new(false, "127.0.0.1", 9000, "VRChat未起動・既定ポートを使用");
        }

        using var client = new HttpClient { Timeout = TimeSpan.FromMilliseconds(800) };
        foreach (var url in new[] { "http://127.0.0.1:9001/?HOST_INFO", "http://127.0.0.1:9001/" })
        {
            try
            {
                var json = await client.GetStringAsync(url, cancellationToken);
                using var document = JsonDocument.Parse(json);
                var port = FindInteger(document.RootElement, "OSC_PORT") ?? 9000;
                var host = FindString(document.RootElement, "OSC_IP") ?? "127.0.0.1";
                if (host is "0.0.0.0" or "::" or "localhost")
                {
                    host = "127.0.0.1";
                }
                return new(true, host, port, $"VRChat OSCQuery検出: {host}:{port}");
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
            {
                // Try the next conventional OSCQuery endpoint before falling back.
            }
        }
        return new(false, "127.0.0.1", 9000, "VRChat起動中・OSCQuery未検出（OSCを有効にしてください）");
    }

    private static int? FindInteger(JsonElement element, string name)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && property.Value.TryGetInt32(out var value))
                {
                    return value;
                }
                var nested = FindInteger(property.Value, name);
                if (nested is not null)
                {
                    return nested;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in element.EnumerateArray())
            {
                var nested = FindInteger(child, name);
                if (nested is not null)
                {
                    return nested;
                }
            }
        }
        return null;
    }

    private static string? FindString(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }
        foreach (var property in element.EnumerateObject())
        {
            if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && property.Value.ValueKind == JsonValueKind.String)
            {
                return property.Value.GetString();
            }
            var nested = FindString(property.Value, name);
            if (nested is not null)
            {
                return nested;
            }
        }
        return null;
    }
}

public sealed class OscOutputPump : IAsyncDisposable
{
    private readonly IVrchatOscOutput _output;
    private readonly Func<LocomotionFrame> _frameProvider;
    private readonly CancellationTokenSource _cts = new();
    private Task? _runTask;

    public OscOutputPump(IVrchatOscOutput output, Func<LocomotionFrame> frameProvider)
    {
        _output = output;
        _frameProvider = frameProvider;
    }

    public void Start() => _runTask ??= Task.Run(() => RunAsync(_cts.Token));

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1d / 60));
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                try
                {
                    await _output.SendFrameAsync(_frameProvider(), cancellationToken);
                }
                catch (SocketException)
                {
                    // UDP failures are reflected in diagnostics. The next frame retries automatically.
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        if (_runTask is not null)
        {
            await _runTask.ConfigureAwait(false);
        }
        try
        {
            using var resetCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
            await _output.ResetAsync(resetCts.Token);
        }
        catch
        {
            // Best effort during process shutdown.
        }
        _cts.Dispose();
    }
}
