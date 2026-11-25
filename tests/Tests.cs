using System;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using HongguoRemote;

class Tests
{
    static int count;
    static void Check(bool value, string label)
    {
        if (!value) throw new Exception("FAIL: " + label);
        count++; Console.WriteLine("PASS: " + label);
    }
    static bool Rejects(Action action)
    {
        try { action(); return false; } catch (ArgumentException) { return true; }
    }
    static int Main(string[] args)
    {
        Console.OutputEncoding = new System.Text.UTF8Encoding(false);
        if (args.Length > 0 && args[0] == "-hide_banner") return VideoTests.RunStalledDecoder();
        if (args.Length > 0 && args[0] == "--echo") { Console.Write(args[1]); return 0; }
        if (args.Length > 0 && args[0] == "--delay") { Thread.Sleep(5000); return 0; }
        if (args.Length > 0 && args[0] == "--fail") { Console.Error.Write("expected failure"); return 7; }
        try
        {
            if (args.Length == 1 && args[0] == "--video-only") { VideoTests.Run(); return 0; }
            var devices = DeviceBridge.ParseDevices("List of devices attached\r\nabc device product:x model:Redmi_K70 transport_id:1\r\ndef unauthorized usb:2\r\n192.168.1.8:5555 offline\r\nadb-x._adb-tls-connect._tcp device model:Phone\r\n* daemon started successfully *\r\n");
            Check(devices.Count == 4 && devices[0].Ready && devices[0].Model == "Redmi_K70", "device list parsing");
            Check(!devices[1].Ready && !devices[2].Ready, "unauthorized and offline never ready");
            Check(devices[2].Wireless && devices[3].Wireless && !devices[0].Wireless, "wireless identification");
            var services = DeviceBridge.ParseServices("List of discovered mdns services\nphone-one _adb-tls-pairing._tcp 10.48.67.185:37123\nphone-one _adb-tls-pairing._tcp. 10.48.67.185:37123\nphone-two\t_adb-tls-connect._tcp.\t10.48.67.185:40567\nnoise _other._tcp 10.48.67.185:1234\nbad _adb-tls-connect._tcp 999.1.1.1:80\n");
            Check(services.Count == 2, "mDNS ignores unrelated, invalid and duplicate services");
            Check(services[0].Pairing && !services[1].Pairing, "mDNS distinguishes pairing and connection ports");
            Check(services[0].Endpoint == "10.48.67.185:37123" && services[1].Endpoint == "10.48.67.185:40567", "mDNS preserves discovered endpoints");
            Check(DeviceBridge.ParseServices("List of discovered mdns services\n").Count == 0, "empty discovery does not invent an endpoint");
            Check(DeviceBridge.ValidateEndpoint(" 192.168.1.8:5555 ") == "192.168.1.8:5555", "endpoint normalization");
            foreach (var bad in new[] { "1.2.3.4:0", "1.2.3.4:65536", "999.2.3.4:80", "1.2.3.4:5555 & calc", "hostname:5555", "127.1:5555", "0.0.0.0:5555" })
                Check(Rejects(() => DeviceBridge.ValidateEndpoint(bad)), "reject invalid endpoint " + bad);
            Check(DeviceBridge.ParseScreen("Physical size: 1440x3200\nOverride size: 1080x2400", "SurfaceOrientation: 0").SequenceEqual(new[] {1080,2400}), "overridden portrait dimensions");
            Check(DeviceBridge.ParseScreen("Physical size: 1440x3200", "SurfaceOrientation: 1").SequenceEqual(new[] {3200,1440}), "landscape dimensions");
            Check(DeviceBridge.ParseScreen("", "Viewport INTERNAL: displayId=0, orientation=1, logicalFrame=[0, 0, 3200, 1440]").SequenceEqual(new[] {3200,1440}), "logical viewport dimensions");
            bool unknown = false;
            try { DeviceBridge.ParseScreen("Physical size: 1440x3200", "unknown"); } catch (InvalidOperationException) { unknown = true; }
            Check(unknown, "unknown orientation does not send guessed gesture");
            string exe = Assembly.GetExecutingAssembly().Location;
            foreach (string input in new[] { "", "a b", "中文搜索", "a\"b", @"C:\path with space\", "x & calc | echo" })
            {
                var result = ProcessRunner.Run(exe, 3000, "--echo", input).GetAwaiter().GetResult();
                Check(result.Output == input && result.ExitCode == 0, "argument roundtrip: " + input);
            }
            var failed = ProcessRunner.Run(exe, 3000, "--fail").GetAwaiter().GetResult();
            Check(failed.ExitCode == 7 && failed.Error == "expected failure", "stderr and exit code captured");
            bool timedout = false;
            var time = Stopwatch.StartNew();
            try { ProcessRunner.Run(exe, 150, "--delay").GetAwaiter().GetResult(); } catch (TimeoutException) { timedout = true; }
            Check(timedout && time.ElapsedMilliseconds < 3000, "hung child is bounded by timeout");
            Console.WriteLine("Passed " + count + " checks.");
            CompanionTests.Run();
            VideoTests.Run();
            return 0;
        }
        catch (Exception e) { Console.Error.WriteLine(e); return 1; }
    }
}
