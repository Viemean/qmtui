using System.Text.Json;
using QmTui.Connect.Models;
using QmTui.Utils;

namespace QmTui.Connect.Storage;

public sealed class ConnectStorage
{
    private static readonly string s_configDir = AppPathHelper.ConfigDir;
    private static readonly string s_configPath = Path.Combine(s_configDir, "connect.json");

    private readonly object _lock = new();
    private List<ConnectDevice> _pairedDevices = [];

    public string LocalDeviceId { get; private set; } = "";
    public string LocalDeviceName { get; private set; } = "QMTUI Linux";
    public string LocalToken { get; private set; } = "";
    public string CurrentPinCode { get; private set; } = "";

    public IReadOnlyList<ConnectDevice> PairedDevices
    {
        get
        {
            lock (_lock)
            {
                return _pairedDevices.ToList();
            }
        }
    }

    public ConnectStorage()
    {
        Load();
        GenerateNewPinCode();
    }

    public string GenerateNewPinCode()
    {
        CurrentPinCode = Random.Shared.Next(100000, 1000000).ToString("D6");
        return CurrentPinCode;
    }

    public ConnectDevice GetLocalDevice(string host = "", int port = 8765)
    {
        return new ConnectDevice(
            Id: LocalDeviceId,
            Name: LocalDeviceName,
            Type: ConnectDeviceType.TV,
            Host: host,
            Port: port,
            Token: LocalToken
        );
    }

    public bool IsDeviceTrusted(string deviceId, string token)
    {
        if (string.IsNullOrEmpty(deviceId) || string.IsNullOrEmpty(token)) return false;
        lock (_lock)
        {
            return _pairedDevices.Any(d => d.Id == deviceId && d.Token == token);
        }
    }

    public void SavePairedDevice(ConnectDevice device)
    {
        lock (_lock)
        {
            _pairedDevices.RemoveAll(d => d.Id == device.Id);
            _pairedDevices.Add(device);
            Save();
        }
    }

    public void RemovePairedDevice(string deviceId)
    {
        lock (_lock)
        {
            _pairedDevices.RemoveAll(d => d.Id == deviceId);
            Save();
        }
    }

    public void ClearAll()
    {
        lock (_lock)
        {
            _pairedDevices.Clear();
            Save();
        }
    }

    private void Load()
    {
        try
        {
            if (File.Exists(s_configPath))
            {
                var json = File.ReadAllText(s_configPath);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (root.TryGetProperty("local_id", out var idProp)) LocalDeviceId = idProp.GetString() ?? "";
                if (root.TryGetProperty("local_name", out var nameProp)) LocalDeviceName = nameProp.GetString() ?? "QMTUI Linux";
                if (root.TryGetProperty("local_token", out var tokenProp)) LocalToken = tokenProp.GetString() ?? "";

                if (root.TryGetProperty("paired_devices", out var devicesProp) && devicesProp.ValueKind == JsonValueKind.Array)
                {
                    var list = new List<ConnectDevice>();
                    foreach (var elem in devicesProp.EnumerateArray())
                    {
                        var dev = JsonSerializer.Deserialize(elem.GetRawText(), ConnectJsonContext.Default.ConnectDevice);
                        if (dev != null) list.Add(dev);
                    }
                    _pairedDevices = list;
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("ConnectStorage", "Failed to load connect.json", ex);
        }

        if (string.IsNullOrEmpty(LocalDeviceId))
        {
            LocalDeviceId = Guid.NewGuid().ToString("N");
            LocalToken = Guid.NewGuid().ToString("N");
            Save();
        }
        if (string.IsNullOrEmpty(LocalToken))
        {
            LocalToken = Guid.NewGuid().ToString("N");
            Save();
        }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(s_configDir);
            using var stream = File.Create(s_configPath);
            using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });

            writer.WriteStartObject();
            writer.WriteString("local_id", LocalDeviceId);
            writer.WriteString("local_name", LocalDeviceName);
            writer.WriteString("local_token", LocalToken);

            writer.WriteStartArray("paired_devices");
            foreach (var dev in _pairedDevices)
            {
                JsonSerializer.Serialize(writer, dev, ConnectJsonContext.Default.ConnectDevice);
            }
            writer.WriteEndArray();

            writer.WriteEndObject();
        }
        catch (Exception ex)
        {
            AppLogger.Error("ConnectStorage", "Failed to save connect.json", ex);
        }
    }
}
