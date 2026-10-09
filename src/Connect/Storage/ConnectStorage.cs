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

    /// <summary>
    /// 生成并更新随机 6 位数字连接配对 PIN 码。
    /// </summary>
    /// <returns>新生成的 6 位数字 PIN 码。</returns>
    public string GenerateNewPinCode()
    {
        CurrentPinCode = Random.Shared.Next(100000, 1000000).ToString("D6");
        return CurrentPinCode;
    }

    /// <summary>
    /// 获取当前主机的互联设备模型。
    /// </summary>
    /// <param name="host">绑定的主机 IP 或域名。</param>
    /// <param name="port">监听端口号。</param>
    /// <returns>本机互联设备信息实例。</returns>
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

    /// <summary>
    /// 校验远程设备是否已通过验证并属于受信任配对列表。
    /// </summary>
    /// <param name="deviceId">远程设备唯一标识。</param>
    /// <param name="token">远程设备持有的安全令牌。</param>
    /// <returns>若设备在信任列表中且令牌匹配返回 <see langword="true"/>；否则返回 <see langword="false"/>。</returns>
    public bool IsDeviceTrusted(string deviceId, string token)
    {
        if (string.IsNullOrEmpty(deviceId) || string.IsNullOrEmpty(token)) return false;
        lock (_lock)
        {
            return _pairedDevices.Any(d => d.Id == deviceId && d.Token == token);
        }
    }

    /// <summary>
    /// 保存或更新已成功配对的互联设备信息。
    /// </summary>
    /// <param name="device">待记录的互联设备模型。</param>
    public void SavePairedDevice(ConnectDevice device)
    {
        lock (_lock)
        {
            _pairedDevices.RemoveAll(d => d.Id == device.Id);
            _pairedDevices.Add(device);
            Save();
        }
    }

    /// <summary>
    /// 从已配对列表中移除指定设备。
    /// </summary>
    /// <param name="deviceId">目标设备唯一标识。</param>
    public void RemovePairedDevice(string deviceId)
    {
        lock (_lock)
        {
            _pairedDevices.RemoveAll(d => d.Id == deviceId);
            Save();
        }
    }

    /// <summary>
    /// 清空所有已保存的互联设备配对信息。
    /// </summary>
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
