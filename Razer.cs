using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;

// Razer devices (USB VID 1532): the 91-byte feature report of the OpenRazer protocol (github.com/openrazer/openrazer, GPL-2.0; facts only):
// [1] status, [2] transaction id, [6] data size, [7] command class 0x07, [8] command id (0x80 battery level 0-255, 0x84 charging), [10] first argument, [89] XOR of [3..88].
// shortcut: untested on real hardware (none on this PC); the transaction ids below are the ones different Razer models answer to.
static partial class Program
{
    static readonly byte[] RazerTransactions = [0x1F, 0x3F, 0xFF, 0x08, 0x9F];

    static void ReadRazer()
    {
        var shown = new HashSet<string>();
        while (true)
        {
            var paths = Native.HidPaths().Where(p => UsbVid(p) == "1532").ToList();
            var answered = new HashSet<string>(); // one icon per device, though several of its interfaces may answer
            foreach (var path in paths)
            {
                var device = UsbIds(path);
                if (answered.Contains(device)) continue;
                using var h = Native.CreateFile(path, 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
                if (h.IsInvalid || Native.Caps(h).FeatureReportByteLength != 91) continue;
                foreach (var tid in RazerTransactions)
                    if (RazerAsk(h, tid, 0x80) is { } level)
                    {
                        var name = HidName(h, "Razer device");
                        var key = "razer:" + device;
                        Report(key, name, RazerKind(name), RazerPercent(level[10]), RazerAsk(h, tid, 0x84) is { } c && c[10] == 1, TimeSpan.FromMinutes(3));
                        shown.Add(key);
                        answered.Add(device);
                        break;
                    }
            }
            ForgetGone(shown, "razer:", paths.Select(UsbIds));
            Thread.Sleep(60000);
        }
    }

    // null = no valid answer; a busy device (status 1) is asked again, up to 3 times.
    static byte[]? RazerAsk(SafeFileHandle h, byte tid, byte id)
    {
        for (var tries = 0; tries < 3; tries++)
        {
            if (!Native.HidD_SetFeature(h, RazerReq(tid, id), 91)) return null;
            Thread.Sleep(40);
            var r = new byte[91];
            if (!Native.HidD_GetFeature(h, r, 91)) return null;
            if (r[1] != 1) return r[1] == 2 && r[2] == tid && r[7] == 7 && r[8] == id ? r : null;
        }
        return null;
    }

    static byte[] RazerReq(byte tid, byte id)
    {
        var b = new byte[91];
        b[2] = tid; b[6] = 2; b[7] = 7; b[8] = id;
        b[89] = RazerXor(b);
        return b;
    }

    static byte RazerXor(byte[] b) { byte x = 0; for (var i = 3; i <= 88; i++) x ^= b[i]; return x; }

    static int RazerPercent(byte raw) => (raw * 100 + 127) / 255;

    static string RazerKind(string name) =>
        Regex.IsMatch(name, "blackwidow|huntsman|ornata|cynosa|deathstalker|keyboard", RegexOptions.IgnoreCase) ? "keyboard"
        : Regex.IsMatch(name, "kraken|barracuda|blackshark|nari", RegexOptions.IgnoreCase) ? "headset" : "mouse";
}
