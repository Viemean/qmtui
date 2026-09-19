using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using QmTui.Connect.Storage;
using QmTui.Utils;

namespace QmTui.Connect.Discovery;

public sealed class ConnectMdnsService : IDisposable
{
    private const string ServiceType = "_melodist-connect._tcp.local";
    private readonly ConnectStorage _storage;
    private readonly int _port;
    private CancellationTokenSource? _cts;
    private UdpClient? _udpClient;
    private Process? _avahiProcess;

    public ConnectMdnsService(ConnectStorage storage, int port = 8765)
    {
        _storage = storage;
        _port = port;
    }

    public static string GetBestLocalIpAddress()
    {
        try
        {
            var bestScore = -1;
            string bestIp = "127.0.0.1";

            foreach (var iface in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (iface.OperationalStatus != OperationalStatus.Up) continue;
                if (iface.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

                var name = iface.Name.ToLowerInvariant();
                if (name.Contains("docker") || name.Contains("virbr") || name.Contains("vbox") ||
                    name.Contains("tun") || name.Contains("tap") || name.Contains("tailscale") ||
                    name.Contains("wireguard") || name.Contains("wg") || name.Contains("zt") ||
                    name.Contains("clash") || name.Contains("mihomo"))
                {
                    continue;
                }

                int ifaceScore = (name.StartsWith("wlan") || name.StartsWith("eth") || name.StartsWith("en") || name.StartsWith("wl")) ? 50 : 10;

                var ipProps = iface.GetIPProperties();
                foreach (var addr in ipProps.UnicastAddresses)
                {
                    if (addr.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    var ipStr = addr.Address.ToString();
                    if (ipStr.StartsWith("127.") || ipStr.StartsWith("169.254.") || ipStr.StartsWith("198.18.")) continue;

                    int score = ifaceScore;
                    if (ipStr.StartsWith("192.168.")) score += 40;
                    else if (ipStr.StartsWith("172.")) score += 30;
                    else if (ipStr.StartsWith("10.")) score += 20;

                    if (score > bestScore)
                    {
                        bestScore = score;
                        bestIp = ipStr;
                    }
                }
            }

            return bestIp;
        }
        catch
        {
            return "127.0.0.1";
        }
    }

    public void Start()
    {
        if (_cts != null) return;
        _cts = new CancellationTokenSource();

        var localIp = GetBestLocalIpAddress();
        var suffix = _storage.LocalDeviceId[^Math.Min(4, _storage.LocalDeviceId.Length)..];
        var instanceName = $"Melodist-TV-{suffix}";

        // 1. 优先尝试拉起 Linux 本地 avahi-publish-service 进行系统级 mDNS 发布
        TryStartAvahiPublish(instanceName, localIp);

        // 2. 启动原生 UDP 组播 5353 监听与周期性宣告
        Task.Run(async () =>
        {
            try
            {
                var multicastAddress = IPAddress.Parse("224.0.0.251");
                _udpClient = new UdpClient();
                _udpClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                try
                {
                    _udpClient.Client.Bind(new IPEndPoint(IPAddress.Any, 5353));
                    _udpClient.JoinMulticastGroup(multicastAddress);
                }
                catch (Exception bindEx)
                {
                    AppLogger.Debug("ConnectMdns", $"UDP 5353 bind note: {bindEx.Message}");
                }

                AppLogger.Info("ConnectMdns", $"mDNS service discovery active for {instanceName} on {localIp}:{_port}");

                // 启动周期性主动宣告协程 (每 4 秒向组播组推送一次)
                _ = Task.Run(async () =>
                {
                    while (!_cts.Token.IsCancellationRequested)
                    {
                        try
                        {
                            var packet = BuildCompleteDnsSdPacket(instanceName, localIp, _port, _storage.LocalDeviceId, _storage.LocalDeviceName, _storage.LocalToken, _storage.CurrentPinCode);
                            _udpClient?.SendAsync(packet, packet.Length, new IPEndPoint(multicastAddress, 5353));
                        }
                        catch {}

                        await Task.Delay(4000, _cts.Token).ConfigureAwait(false);
                    }
                }, _cts.Token);

                // 监听传入查询
                while (!_cts.Token.IsCancellationRequested)
                {
                    try
                    {
                        var result = await _udpClient.ReceiveAsync(_cts.Token).ConfigureAwait(false);
                        HandleMdnsQuery(result.Buffer, result.RemoteEndPoint, instanceName, localIp);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Debug("ConnectMdns", $"Packet handle note: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                AppLogger.Warn("ConnectMdns", $"ConnectMdnsService task error: {ex.Message}");
            }
        }, _cts.Token);
    }

    private void TryStartAvahiPublish(string instanceName, string localIp)
    {
        try
        {
            StopAvahiPublish();

            var psi = new ProcessStartInfo
            {
                FileName = "avahi-publish-service",
                Arguments = $"\"{instanceName}\" \"_melodist-connect._tcp\" {_port} \"id={_storage.LocalDeviceId}\" \"name={_storage.LocalDeviceName}\" \"token={_storage.LocalToken}\" \"pin={_storage.CurrentPinCode}\" \"host={localIp}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            _avahiProcess = Process.Start(psi);
            AppLogger.Info("ConnectMdns", $"Spawned avahi-publish-service for {instanceName}");
        }
        catch (Exception ex)
        {
            AppLogger.Debug("ConnectMdns", $"avahi-publish-service not available: {ex.Message}");
        }
    }

    private void StopAvahiPublish()
    {
        try
        {
            if (_avahiProcess != null && !_avahiProcess.HasExited)
            {
                _avahiProcess.Kill(entireProcessTree: true);
                _avahiProcess.Dispose();
            }
        }
        catch {}
        _avahiProcess = null;
    }

    private void HandleMdnsQuery(byte[] data, IPEndPoint remoteEp, string instanceName, string localIp)
    {
        if (data.Length < 12) return;
        var queryText = Encoding.ASCII.GetString(data);
        if (!queryText.Contains("melodist-connect", StringComparison.OrdinalIgnoreCase) &&
            !queryText.Contains(instanceName, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            var response = BuildCompleteDnsSdPacket(instanceName, localIp, _port, _storage.LocalDeviceId, _storage.LocalDeviceName, _storage.LocalToken, _storage.CurrentPinCode);
            _udpClient?.SendAsync(response, response.Length, new IPEndPoint(IPAddress.Parse("224.0.0.251"), 5353));
        }
        catch (Exception ex)
        {
            AppLogger.Debug("ConnectMdns", $"Failed to send mDNS answer: {ex.Message}");
        }
    }

    private static byte[] BuildCompleteDnsSdPacket(
        string instanceName,
        string hostIp,
        int port,
        string deviceId,
        string deviceName,
        string token,
        string pinCode)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);

        string serviceInstanceFqdn = $"{instanceName}.{ServiceType}";
        string hostTargetFqdn = $"{instanceName.ToLowerInvariant()}.local";

        // Header: ID=0, Flags=0x8400 (Response, Authoritative), QD=0, AN=1 (PTR), NS=0, AR=3 (SRV, TXT, A)
        bw.Write((ushort)0);
        bw.Write((byte)0x84);
        bw.Write((byte)0x00);
        bw.Write((byte)0x00); bw.Write((byte)0x00); // QDCOUNT = 0
        bw.Write((byte)0x00); bw.Write((byte)0x01); // ANCOUNT = 1
        bw.Write((byte)0x00); bw.Write((byte)0x00); // NSCOUNT = 0
        bw.Write((byte)0x00); bw.Write((byte)0x03); // ARCOUNT = 3

        // 1. Answer: PTR Record (_melodist-connect._tcp.local -> serviceInstanceFqdn)
        WriteDnsName(bw, ServiceType);
        bw.Write((byte)0x00); bw.Write((byte)0x0C); // TYPE: PTR (12)
        bw.Write((byte)0x00); bw.Write((byte)0x01); // CLASS: IN (1)
        bw.Write((byte)0x00); bw.Write((byte)0x00); bw.Write((byte)0x00); bw.Write((byte)0x78); // TTL: 120s

        using (var rdataMs = new MemoryStream())
        using (var rdataBw = new BinaryWriter(rdataMs))
        {
            WriteDnsName(rdataBw, serviceInstanceFqdn);
            var rdata = rdataMs.ToArray();
            bw.Write((byte)(rdata.Length >> 8));
            bw.Write((byte)(rdata.Length & 0xFF));
            bw.Write(rdata);
        }

        // 2. Additional: SRV Record (serviceInstanceFqdn -> Priority 0, Weight 0, Port, hostTargetFqdn)
        WriteDnsName(bw, serviceInstanceFqdn);
        bw.Write((byte)0x00); bw.Write((byte)0x21); // TYPE: SRV (33)
        bw.Write((byte)0x80); bw.Write((byte)0x01); // CLASS: IN (1) + Cache Flush
        bw.Write((byte)0x00); bw.Write((byte)0x00); bw.Write((byte)0x00); bw.Write((byte)0x78); // TTL: 120s

        using (var rdataMs = new MemoryStream())
        using (var rdataBw = new BinaryWriter(rdataMs))
        {
            rdataBw.Write((ushort)0); // Priority
            rdataBw.Write((ushort)0); // Weight
            rdataBw.Write((byte)(port >> 8));
            rdataBw.Write((byte)(port & 0xFF));
            WriteDnsName(rdataBw, hostTargetFqdn);
            var rdata = rdataMs.ToArray();
            bw.Write((byte)(rdata.Length >> 8));
            bw.Write((byte)(rdata.Length & 0xFF));
            bw.Write(rdata);
        }

        // 3. Additional: TXT Record (serviceInstanceFqdn -> id, name, token, pin, host)
        WriteDnsName(bw, serviceInstanceFqdn);
        bw.Write((byte)0x00); bw.Write((byte)0x10); // TYPE: TXT (16)
        bw.Write((byte)0x80); bw.Write((byte)0x01); // CLASS: IN (1) + Cache Flush
        bw.Write((byte)0x00); bw.Write((byte)0x00); bw.Write((byte)0x00); bw.Write((byte)0x78); // TTL: 120s

        using (var rdataMs = new MemoryStream())
        using (var rdataBw = new BinaryWriter(rdataMs))
        {
            string[] txtEntries =
            [
                $"id={deviceId}",
                $"name={deviceName}",
                $"token={token}",
                $"pin={pinCode}",
                $"host={hostIp}"
            ];

            foreach (var entry in txtEntries)
            {
                var entryBytes = Encoding.UTF8.GetBytes(entry);
                rdataBw.Write((byte)entryBytes.Length);
                rdataBw.Write(entryBytes);
            }

            var rdata = rdataMs.ToArray();
            bw.Write((byte)(rdata.Length >> 8));
            bw.Write((byte)(rdata.Length & 0xFF));
            bw.Write(rdata);
        }

        // 4. Additional: A Record (hostTargetFqdn -> IPv4)
        WriteDnsName(bw, hostTargetFqdn);
        bw.Write((byte)0x00); bw.Write((byte)0x01); // TYPE: A (1)
        bw.Write((byte)0x80); bw.Write((byte)0x01); // CLASS: IN (1) + Cache Flush
        bw.Write((byte)0x00); bw.Write((byte)0x00); bw.Write((byte)0x00); bw.Write((byte)0x78); // TTL: 120s

        if (IPAddress.TryParse(hostIp, out var ipObj) && ipObj.AddressFamily == AddressFamily.InterNetwork)
        {
            var ipBytes = ipObj.GetAddressBytes();
            bw.Write((byte)0x00);
            bw.Write((byte)0x04);
            bw.Write(ipBytes);
        }
        else
        {
            bw.Write((byte)0x00);
            bw.Write((byte)0x04);
            bw.Write((byte)127); bw.Write((byte)0); bw.Write((byte)0); bw.Write((byte)1);
        }

        return ms.ToArray();
    }

    private static void WriteDnsName(BinaryWriter bw, string name)
    {
        var parts = name.TrimEnd('.').Split('.');
        foreach (var part in parts)
        {
            var bytes = Encoding.UTF8.GetBytes(part);
            bw.Write((byte)bytes.Length);
            bw.Write(bytes);
        }
        bw.Write((byte)0);
    }

    public void Stop()
    {
        StopAvahiPublish();
        _cts?.Cancel();
        _cts = null;
        try
        {
            _udpClient?.Close();
            _udpClient?.Dispose();
        }
        catch { }
        _udpClient = null;
    }

    public void Dispose()
    {
        Stop();
    }
}
