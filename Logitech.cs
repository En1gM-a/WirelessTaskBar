using System.Collections.Concurrent;
using System.Text;
using Microsoft.Win32.SafeHandles;

// Logitech HID++ 2.0 over USB (VID 046D): direct devices (index 0xFF) and devices behind a Unifying/Bolt/Lightspeed receiver (index 1..6).
// Requests go to the long collection (usage page FF00, usage 2, 20 bytes): "11 <idx> <feature index> <function<<4 | 0x0D> <params>".
// Feature IDs and layouts: Logitech's public HID++ 2.0 feature specs (0x0005 device name/type, 0x1000 battery level,
// 0x1001 battery voltage, 0x1004 unified battery) as implemented by Solaar (github.com/pwr-Solaar/Solaar, GPL-3.0; facts only, no code taken).
// shortcut: untested on real hardware (none on this PC); layouts follow the spec only.
static partial class Program
{
    // Single-cell Li-ion discharge steps in mV for feature 0x1001, after the table in Solaar's hidpp20.py (from memory, not checked against a device).
    static readonly (int mV, int Pct)[] LiIon = [(4186, 100), (4067, 90), (3989, 80), (3922, 70), (3859, 60), (3811, 50), (3778, 40), (3751, 30), (3717, 20), (3671, 10), (3646, 5), (3579, 2)];

    static void ReadLogitech()
    {
        var shown = new HashSet<string>();
        while (true)
        {
            var paths = Native.HidPaths().Where(p => UsbVid(p) == "046d").ToList();
            foreach (var path in paths) PollLogi(path, shown);
            ForgetGone(shown, "logi:", paths.Select(PathBase));
            Thread.Sleep(60000);
        }
    }

    static void PollLogi(string path, HashSet<string> shown)
    {
        using var reader = Native.CreateFile(path, 0xC0000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (reader.IsInvalid || Native.Caps(reader) is not { UsagePage: 0xFF00, Usage: 2, OutputReportByteLength: 20, InputReportByteLength: >= 20 } caps) return;
        using var writer = Native.CreateFile(path, 0xC0000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero); // a send must not queue behind the blocking read
        if (writer.IsInvalid) return;
        var inbox = new BlockingCollection<byte[]>();
        var done = false;
        var pump = new Thread(() =>
        {
            var buf = new byte[caps.InputReportByteLength];
            while (!done && Native.ReadFile(reader, buf, buf.Length, out var n, IntPtr.Zero)) inbox.Add(buf[..n]);
        }) { IsBackground = true };
        pump.Start();
        try
        {
            byte[]? Ask(byte idx, int feature, int fn, params byte[] args)
            {
                var req = new byte[20];
                req[0] = 0x11; req[1] = idx; req[2] = (byte)feature; req[3] = (byte)(fn << 4 | 0x0D);
                args.CopyTo(req, 4);
                if (!Native.WriteFile(writer, req, req.Length, out _, IntPtr.Zero)) return null;
                var end = DateTime.UtcNow.AddSeconds(1);
                while (inbox.TryTake(out var r, Math.Max(0, (int)(end - DateTime.UtcNow).TotalMilliseconds)))
                {
                    if (IsLogiReply(r, req)) return r;
                    if (r.Length >= 7 && r[0] == 0x11 && r[1] == idx && r[2] == 0xFF && r[3] == req[2]) return null; // HID++ error
                }
                return null;
            }
            int Feature(byte idx, int id) => Ask(idx, 0, 0, (byte)(id >> 8), (byte)id) is { } r ? r[4] : 0; // 0 = absent

            bool Ping(byte idx) => Ask(idx, 0, 1, 0, 0, 0x5A) is { } r && r[4] >= 2; // HID++ 1.0 devices answer with an error
            bool ReadDevice(byte idx) // true when a battery reading was stored
            {
                (int Percent, bool Charging)? battery = null;
                foreach (var id in new[] { 0x1004, 0x1000, 0x1001 })
                    if (Feature(idx, id) is not 0 and var fi)
                    {
                        battery = Ask(idx, fi, id == 0x1004 ? 1 : 0) is { } r ? ParseLogiBattery(id, r) : null;
                        break;
                    }
                if (battery is not (int percent, bool charging)) return false;
                string name = "Logitech device", kind = "device";
                if (Feature(idx, 0x0005) is not 0 and var ni)
                {
                    if (Ask(idx, ni, 0) is { } len)
                    {
                        var text = new StringBuilder();
                        for (var i = 0; i < len[4] && Ask(idx, ni, 1, (byte)i) is { } part; i += 16)
                            text.Append(Encoding.ASCII.GetString(part, 4, Math.Min(16, len[4] - i)));
                        if (text.ToString().TrimEnd('\0') is { Length: > 0 } s) name = s;
                    }
                    if (Ask(idx, ni, 2) is { } type) kind = LogiKind(type[4]);
                }
                var key = "logi:" + PathBase(path) + ":" + idx;
                Report(key, name, kind, percent, charging, TimeSpan.FromMinutes(3));
                shown.Add(key);
                return true;
            }
            // A receiver can answer the ping at 0xFF itself but has no battery; then ask its paired slots 1..6.
            if (!(Ping(0xFF) && ReadDevice(0xFF)))
                foreach (byte idx in new byte[] { 1, 2, 3, 4, 5, 6 })
                    if (Ping(idx)) ReadDevice(idx);
        }
        finally
        {
            done = true;
            Native.CancelIoEx(reader, IntPtr.Zero); // release the pump before the handles close
            pump.Join();
        }
    }

    // A reply echoes device index, feature index and function/software id of its request.
    static bool IsLogiReply(byte[] r, byte[] req) => r.Length >= 7 && r[0] == 0x11 && r[1] == req[1] && r[2] == req[2] && r[3] == req[3];

    static (int Percent, bool Charging)? ParseLogiBattery(int feature, byte[] r) => feature switch
    {
        0x1004 => (r[4] != 0 ? r[4] : r[5] switch { 8 => 100, 4 => 50, 2 => 20, _ => 5 }, r[6] is >= 1 and <= 3), // % or level flags; charging states
        0x1000 => ((int)r[4], r[6] is >= 1 and <= 4),
        0x1001 => (LogiVoltage(r[4] << 8 | r[5]), (r[6] & 0x80) != 0),
        _ => null,
    };

    static int LogiVoltage(int mv) => LiIon.FirstOrDefault(t => mv >= t.mV).Pct;

    static string LogiKind(int type) => type switch { 0 => "keyboard", 3 or 4 or 5 => "mouse", 8 => "headset", _ => "device" };
}
