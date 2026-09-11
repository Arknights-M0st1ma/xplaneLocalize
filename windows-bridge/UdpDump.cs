using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;

namespace XPlaneEfbBridge;

// A WinExe has no console attached, so the command line switches would print
// into nowhere when started from a terminal.
internal static class ConsoleHost
{
    private const int AttachParentProcess = -1;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AllocConsole();

    public static void Attach()
    {
        try
        {
            if (!AttachConsole(AttachParentProcess)) AllocConsole();
        }
        catch { }
    }
}

// Diagnostic: listens to the configured UDP ports and reports, per X-Plane data
// row, which of the eight value slots actually carries data. Used to settle
// questions like "why is V/S always -999" without guessing.
//
//   XPlaneEfbBridge.exe --dump-udp "%LOCALAPPDATA%\XPlaneEfbBridge\bridge-config.json" 20
internal static class UdpDump
{
    public static int Run(string configPath, int seconds)
    {
        Console.OutputEncoding = Encoding.UTF8;
        ConfigSnapshot snapshot;
        try { snapshot = ConfigStore.Load(configPath); }
        catch (Exception error) { Console.WriteLine($"无法读取配置：{error.Message}"); return 1; }

        var config = snapshot.Config;
        seconds = Math.Clamp(seconds, 3, 300);
        var sockets = new List<UdpClient>();
        foreach (var port in config.UdpPorts.Distinct())
        {
            try
            {
                var udp = new UdpClient();
                udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                udp.Client.Bind(new IPEndPoint(IPAddress.Any, port));
                sockets.Add(udp);
            }
            catch (Exception error) { Console.WriteLine($"UDP {port} 不可用：{error.Message}"); }
        }
        if (sockets.Count == 0) { Console.WriteLine("没有可监听的 UDP 端口。"); return 1; }
        if (config.SourceIp.Length > 0) Console.WriteLine($"（只接受来源 {config.SourceIp}）");
        Console.WriteLine($"监听 UDP {string.Join(", ", config.UdpPorts.Distinct())}，采样 {seconds} 秒…请在 X-Plane 里进入飞行。");

        var rows = new SortedDictionary<int, RowStats>();
        var finished = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        var listeners = sockets.Select(socket => Task.Run(async () =>
        {
            while (!finished.IsCancellationRequested)
            {
                UdpReceiveResult packet;
                try { packet = await socket.ReceiveAsync(finished.Token); }
                catch { return; }
                lock (rows) Accumulate(packet.Buffer, rows);
            }
        })).ToArray();
        try { Task.WaitAll(listeners, TimeSpan.FromSeconds(seconds + 2)); } catch { }
        finished.Cancel();
        foreach (var socket in sockets) socket.Dispose();

        var text = new List<string>
        {
            $"采样时间：{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} · {seconds} 秒 · UDP {string.Join(", ", config.UdpPorts.Distinct())}"
        };
        if (rows.Count == 0)
        {
            text.Add("没有收到任何 DATA 包：检查 X-Plane 的 Data Output 勾选、目标 IP 与端口、防火墙。");
        }
        foreach (var (index, stats) in rows)
        {
            text.Add($"");
            text.Add($"数据行 {index}（单包 {36 + 5} 字节，共 {stats.Packets} 包）");
            for (var slot = 0; slot < 8; slot++)
            {
                var values = stats.Slots[slot];
                if (values.Count == 0) { text.Add($"  槽 {slot}：无数据"); continue; }
                var finite = values.Where(double.IsFinite).ToArray();
                if (finite.Length == 0) { text.Add($"  槽 {slot}：全部为非数字"); continue; }
                var sentinels = finite.Count(value => Math.Abs(value + 999) < 1);
                var meaningful = finite.Where(value => Math.Abs(value + 999) >= 1).ToArray();
                var last = finite[^1].ToString("0.###", CultureInfo.InvariantCulture);
                text.Add(meaningful.Length == 0
                    ? $"  槽 {slot}：恒为 -999（{sentinels}/{finite.Length}）——X-Plane 表示“无数据”"
                    : $"  槽 {slot}：最小 {meaningful.Min().ToString("0.###", CultureInfo.InvariantCulture)}  最大 {meaningful.Max().ToString("0.###", CultureInfo.InvariantCulture)}  最后 {last}  有效 {meaningful.Length}/{finite.Length}");
            }
        }
        var report = string.Join(Environment.NewLine, text);
        Console.WriteLine(report);
        try
        {
            var path = Path.Combine(ConfigStore.DirectoryFor(configPath), "udp-dump.txt");
            File.WriteAllText(path, report, new UTF8Encoding(false));
            Console.WriteLine($"{Environment.NewLine}已写入 {path}");
        }
        catch (Exception error) { Console.WriteLine($"无法写入报告：{error.Message}"); }
        return 0;
    }

    private static void Accumulate(byte[] packet, SortedDictionary<int, RowStats> rows)
    {
        if (packet.Length < 41 || Encoding.ASCII.GetString(packet, 0, 4) != "DATA") return;
        for (var offset = 5; offset + 36 <= packet.Length; offset += 36)
        {
            var index = BinaryPrimitives.ReadInt32LittleEndian(packet.AsSpan(offset, 4));
            if (index is < 0 or > 200) continue;
            if (!rows.TryGetValue(index, out var stats)) rows[index] = stats = new RowStats();
            stats.Packets++;
            for (var slot = 0; slot < 8; slot++)
            {
                var bits = BinaryPrimitives.ReadInt32LittleEndian(packet.AsSpan(offset + 4 + slot * 4, 4));
                var value = BitConverter.Int32BitsToSingle(bits);
                var keep = float.IsFinite(value) && Math.Abs(value) < 1e20;
                var bucket = stats.Slots[slot];
                bucket.Add(keep ? value : double.NaN);
                if (bucket.Count > 400) bucket.RemoveAt(0);
            }
        }
    }

    private sealed class RowStats
    {
        public long Packets;
        public List<double>[] Slots { get; } = [.. Enumerable.Range(0, 8).Select(_ => new List<double>())];
    }
}
