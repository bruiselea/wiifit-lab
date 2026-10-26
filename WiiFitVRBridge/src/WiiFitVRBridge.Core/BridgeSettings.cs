using System.Text.Json;

namespace WiiFitVRBridge.Core;

public sealed class BridgeSettings
{
    public MovementMode ActiveMode { get; set; } = MovementMode.Walking;
    public bool OutputEnabled { get; set; }
    public string OscHost { get; set; } = "127.0.0.1";
    public int OscPort { get; set; } = 9000;
    public bool UseOscQuery { get; set; } = true;
    public bool AudioCues { get; set; } = true;
    public Dictionary<MovementMode, CalibrationProfile> Profiles { get; set; } = new()
    {
        [MovementMode.Walking] = new(),
        [MovementMode.Ski] = new(),
        [MovementMode.Snowboard] = new(),
    };
    public BoardConnectionSettings BoardConnection { get; set; } = new();

    public CalibrationProfile ProfileFor(MovementMode mode)
    {
        if (!Profiles.TryGetValue(mode, out var profile))
        {
            profile = new CalibrationProfile();
            Profiles[mode] = profile;
        }
        return profile;
    }
}

public sealed class BoardConnectionSettings
{
    public string? LastConnectedDeviceId { get; set; }
    public DateTime? LastConnectedAtUtc { get; set; }
    public string? LastBluetoothAdapterMac { get; set; }
    public bool HasConnectedBefore { get; set; }
}

public static class BridgeSettingsStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    public static BridgeSettings Load(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                return JsonSerializer.Deserialize<BridgeSettings>(File.ReadAllText(path), Options) ?? new BridgeSettings();
            }
        }
        catch
        {
            // A corrupt settings file must never prevent the emergency-zero path from starting.
        }
        return new BridgeSettings();
    }

    public static void Save(string path, BridgeSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(settings, Options));
        File.Move(temporary, path, overwrite: true);
    }
}
