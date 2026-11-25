using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace HongguoRemote
{
    public sealed class WirelessService
    {
        public string Name;
        public string Endpoint;
        public bool Pairing;
        public override string ToString() { return (Pairing ? "待配对手机" : "无线手机") + " · " + Name.Replace("_adb-tls-pairing._tcp", "").Replace("_adb-tls-connect._tcp", "").TrimEnd('.'); }
    }

    public sealed class Device
    {
        public string Serial;
        public string State;
        public string Model;
        public bool Ready { get { return State == "device"; } }
        public bool Wireless { get { return Serial.Contains(":") || Serial.Contains("_adb-tls-"); } }
        public override string ToString()
        {
            string status = Ready ? (Wireless ? "无线" : "USB") : State == "unauthorized" ? "请在手机授权" : State == "offline" ? "离线" : State;
            return (String.IsNullOrEmpty(Model) ? Serial : Model.Replace('_', ' ')) + "  ·  " + status + "  [" + Serial + "]";
        }
    }

    public sealed class CommandResult
    {
        public int ExitCode;
        public string Output;
        public string Error;
        public string Combined { get { return (Output + "\n" + Error).Trim(); } }
        public string RequireSuccess()
        {
            if (ExitCode != 0) throw new InvalidOperationException(Combined.Length == 0 ? "命令失败：" + ExitCode : Combined);
            return Combined;
        }
    }

    public static class ProcessRunner
    {
        // Windows 命令行参数转义；不经过 cmd.exe，也不拼接用户提供的 shell 命令。
        public static string Quote(string value)
        {
            if (value == null || value.IndexOf('\0') >= 0) throw new ArgumentException("无效参数");
            var b = new StringBuilder("\"");
            int slashes = 0;
            foreach (char c in value)
            {
                if (c == '\\') { slashes++; continue; }
                if (c == '"') b.Append('\\', slashes * 2 + 1);
                else b.Append('\\', slashes);
                b.Append(c); slashes = 0;
            }
            return b.Append('\\', slashes * 2).Append('"').ToString();
        }

        public static ProcessStartInfo StartInfo(string exe, IEnumerable<string> args)
        {
            return new ProcessStartInfo(exe, String.Join(" ", args.Select(Quote))) {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
                WorkingDirectory = Path.GetDirectoryName(exe)
            };
        }

        public static async Task<CommandResult> Run(string exe, int timeoutMs, params string[] args)
        {
            using (var p = new Process { StartInfo = StartInfo(exe, args) })
            {
                p.Start();
                Task<string> stdout = p.StandardOutput.ReadToEndAsync();
                Task<string> stderr = p.StandardError.ReadToEndAsync();
                bool done = await Task.Run(() => p.WaitForExit(timeoutMs));
                if (!done)
                {
                    try { p.Kill(); } catch (InvalidOperationException) { }
                    throw new TimeoutException("设备操作超时，请检查手机授权、连接地址和网络。可以重试。");
                }
                return new CommandResult { ExitCode = p.ExitCode, Output = await stdout, Error = await stderr };
            }
        }
    }

    public sealed class DeviceBridge
    {
        public readonly string ToolsDirectory;
        public string Adb { get { return Path.Combine(ToolsDirectory, "adb.exe"); } }
        public string Scrcpy { get { return Path.Combine(ToolsDirectory, "scrcpy.exe"); } }
        public bool Available { get { return File.Exists(Adb) && File.Exists(Scrcpy) && File.Exists(Path.Combine(ToolsDirectory, "scrcpy-server")); } }
        public DeviceBridge(string directory) { ToolsDirectory = directory; }

        public static List<WirelessService> ParseServices(string text)
        {
            var services = new List<WirelessService>();
            foreach (string line in text.Split('\n'))
            {
                var match = Regex.Match(line.Trim(), @"^(\S+)\s+_adb-tls-(pairing|connect)\._tcp\.?\s+(\d{1,3}(?:\.\d{1,3}){3}:\d{1,5})\s*$");
                if (!match.Success) continue;
                try
                {
                    string endpoint = ValidateEndpoint(match.Groups[3].Value);
                    bool pairing = match.Groups[2].Value == "pairing";
                    if (!services.Any(x => x.Endpoint == endpoint && x.Pairing == pairing))
                        services.Add(new WirelessService { Name = match.Groups[1].Value, Endpoint = endpoint, Pairing = pairing });
                }
                catch (ArgumentException) { }
            }
            return services;
        }

        public static List<string> WifiGateways()
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211)
                .SelectMany(n => n.GetIPProperties().GatewayAddresses)
                .Where(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any))
                .Select(g => g.Address.ToString()).Distinct().ToList();
        }

        public async Task<List<WirelessService>> Discover()
        {
            return ParseServices((await ProcessRunner.Run(Adb, 6000, "mdns", "services")).RequireSuccess());
        }

        public static async Task<bool> HasLegacyPort(string ip)
        {
            // 只探测当前 Wi-Fi 网关的标准 ADB 端口，不扫描局域网或随机端口。
            ValidateEndpoint(ip + ":5555");
            using (var socket = new TcpClient())
            {
                try
                {
                    Task connection = socket.ConnectAsync(ip, 5555);
                    if (await Task.WhenAny(connection, Task.Delay(1500)) != connection)
                    {
                        ObserveConnectionFailure(connection);
                        return false;
                    }
                    await connection;
                    return socket.Connected;
                }
                catch (SocketException) { return false; }
            }
        }

        static void ObserveConnectionFailure(Task connection)
        {
            connection.ContinueWith(t => { var ignored = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
        }

        public static List<Device> ParseDevices(string text)
        {
            var result = new List<Device>();
            foreach (string line in text.Split('\n'))
            {
                var m = Regex.Match(line.Trim(), @"^(\S+)\s+(device|offline|unauthorized|recovery|sideload|bootloader)\b(.*)$");
                if (!m.Success) continue;
                var model = Regex.Match(m.Groups[3].Value, @"\bmodel:(\S+)");
                result.Add(new Device { Serial = m.Groups[1].Value, State = m.Groups[2].Value, Model = model.Success ? model.Groups[1].Value : "" });
            }
            return result;
        }

        public static string ValidateEndpoint(string value)
        {
            value = value.Trim();
            var m = Regex.Match(value, @"^(\d{1,3}(?:\.\d{1,3}){3}):(\d{1,5})$");
            IPAddress ip; int port;
            if (!m.Success || !IPAddress.TryParse(m.Groups[1].Value, out ip) ||
                m.Groups[1].Value.Split('.').Any(x => Int32.Parse(x) > 255) ||
                !Int32.TryParse(m.Groups[2].Value, out port) || port < 1 || port > 65535 ||
                ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.Broadcast))
                throw new ArgumentException("请输入手机显示的 IPv4 地址和端口，例如 192.168.1.8:37123。");
            return value;
        }

        public async Task<List<Device>> Devices()
        {
            return ParseDevices((await ProcessRunner.Run(Adb, 12000, "devices", "-l")).RequireSuccess());
        }
        public async Task<string> Pair(string endpoint, string code)
        {
            endpoint = ValidateEndpoint(endpoint);
            if (!Regex.IsMatch(code, @"^\d{6}$")) throw new ArgumentException("请输入手机显示的 6 位配对码。");
            string output = (await ProcessRunner.Run(Adb, 25000, "pair", endpoint, code)).RequireSuccess();
            if (output.IndexOf("Successfully paired", StringComparison.OrdinalIgnoreCase) < 0)
                throw new InvalidOperationException(output);
            return "无线配对成功。请使用无线调试主页面的连接端口进行连接。";
        }
        public async Task<string> Connect(string endpoint)
        {
            endpoint = ValidateEndpoint(endpoint);
            string output = (await ProcessRunner.Run(Adb, 15000, "connect", endpoint)).RequireSuccess();
            // adb connect 有些失败仍返回 0，必须检查实际消息。
            if (!Regex.IsMatch(output, @"(?im)^(already connected to|connected to) "))
                throw new InvalidOperationException(output);
            return output;
        }
        public async Task<string> EnableTcp(string serial)
        {
            return (await ProcessRunner.Run(Adb, 12000, "-s", serial, "tcpip", "5555")).RequireSuccess();
        }
        public async Task<string> RestoreUsb(string serial)
        {
            return (await ProcessRunner.Run(Adb, 12000, "-s", serial, "usb")).RequireSuccess();
        }
        public async Task<string> Shell(string serial, params string[] args)
        {
            return (await ProcessRunner.Run(Adb, 10000, new[] { "-s", serial, "shell" }.Concat(args).ToArray())).RequireSuccess();
        }
        public async Task Key(string serial, int key)
        {
            await Shell(serial, "input", "keyevent", key.ToString());
        }

        public static int[] ParseScreen(string wm, string input)
        {
            // 优先采用当前显示视口，能同时覆盖横竖屏及分辨率覆盖设置。
            var view = Regex.Match(input, @"Viewport[^\r\n]*displayId=0[^\r\n]*logicalFrame=\[0,\s*0,\s*(\d+),\s*(\d+)\]", RegexOptions.IgnoreCase);
            if (view.Success) return new[] { Int32.Parse(view.Groups[1].Value), Int32.Parse(view.Groups[2].Value) };
            var sizes = Regex.Matches(wm, @"(?:Physical|Override) size:\s*(\d+)x(\d+)");
            if (sizes.Count == 0) throw new InvalidOperationException("无法读取手机屏幕尺寸，请直接用鼠标在画面内滑动。");
            var last = sizes[sizes.Count - 1];
            int w = Int32.Parse(last.Groups[1].Value), h = Int32.Parse(last.Groups[2].Value);
            var orientation = Regex.Match(input, @"SurfaceOrientation:\s*([0-3])");
            if (!orientation.Success) orientation = Regex.Match(input, @"Viewport[^\r\n]*displayId=0[^\r\n]*orientation=([0-3])", RegexOptions.IgnoreCase);
            if (!orientation.Success) throw new InvalidOperationException("无法确认屏幕方向，请直接用鼠标拖动画面。");
            if (orientation.Groups[1].Value == "1" || orientation.Groups[1].Value == "3") return new[] { h, w };
            return new[] { w, h };
        }

        public async Task Swipe(string serial, bool next)
        {
            var wm = Shell(serial, "wm", "size");
            var display = Shell(serial, "dumpsys", "input");
            await Task.WhenAll(wm, display);
            var size = ParseScreen(wm.Result, display.Result);
            if (size[0] < 1 || size[1] < 1) throw new InvalidOperationException("手机屏幕尺寸无效。");
            int x = size[0] / 2, top = size[1] * 28 / 100, bottom = size[1] * 72 / 100;
            await Shell(serial, "input", "swipe", x.ToString(), (next ? bottom : top).ToString(), x.ToString(), (next ? top : bottom).ToString(), "280");
        }

        public Process StartMirror(string serial, bool clear, bool audio, string title, Action<string> log)
        {
            var args = new List<string> { "--serial", serial, "--window-title", title, "--window-width=430", "--window-height=760",
                "--max-size=" + (clear ? "1920" : "1280"), "--video-bit-rate=" + (clear ? "8M" : "4M"),
                "--max-fps=60", "--render-driver=software", "--no-clipboard-autosync", "--shortcut-mod=lalt" };
            if (!audio) args.Add("--no-audio");
            var info = ProcessRunner.StartInfo(Scrcpy, args);
            info.EnvironmentVariables["ADB"] = Adb;
            info.EnvironmentVariables["SCRCPY_SERVER_PATH"] = Path.Combine(ToolsDirectory, "scrcpy-server");
            var p = new Process { StartInfo = info };
            p.OutputDataReceived += (s, e) => { if (e.Data != null) log(e.Data); };
            p.ErrorDataReceived += (s, e) => { if (e.Data != null) log(e.Data); };
            p.Start(); p.BeginOutputReadLine(); p.BeginErrorReadLine();
            return p;
        }
    }
}
