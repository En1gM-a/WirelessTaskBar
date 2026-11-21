using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;

// Any other USB HID device that exposes the standard Battery Strength value (usage page 0x06, usage 0x20) in a feature or input report.
// Attack Shark, Aula, Logitech and Razer (1D57, 3554, 046D, 1532) have their own readers and are skipped here.
// shortcut: untested on real hardware with this usage (none on this PC); charging is never reported because the usage has no such flag.
static partial class Program
{
    static void ReadGenericHid()
    {
        var shown = new HashSet<string>();
        while (true)
        {
            var paths = Native.HidPaths().Where(p => UsbVid(p) is { } v && v is not ("1d57" or "3554" or "046d" or "1532")).ToList();
            foreach (var path in paths)
            {
                using var h = Native.CreateFile(path, 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
                if (h.IsInvalid || !Native.HidD_GetPreparsedData(h, out var pp)) continue;
                var percent = HidBattery(h, pp, Native.Caps(h));
                Native.HidD_FreePreparsedData(pp);
                if (percent is not { } pct) continue;
                var key = "hid:" + PathBase(path);
                Report(key, HidName(h, "HID device"), "device", pct, false, TimeSpan.FromMinutes(3));
                shown.Add(key);
            }
            ForgetGone(shown, "hid:", paths.Select(PathBase));
            Thread.Sleep(60000);
        }
    }

    // Feature reports first, then input reports: the first Battery Strength value that reads back wins.
    static int? HidBattery(SafeFileHandle h, IntPtr pp, HidpCaps caps)
    {
        foreach (var (type, count, len) in new[] { (2, (int)caps.NumberFeatureValueCaps, (int)caps.FeatureReportByteLength), (0, caps.NumberInputValueCaps, caps.InputReportByteLength) })
        {
            if (count == 0 || len == 0) continue;
            var found = new HidpValueCaps[count];
            var n = (ushort)count;
            if (Native.HidP_GetValueCaps(type, found, ref n, pp) != 0x110000) continue; // HIDP_STATUS_SUCCESS
            foreach (var c in found.Take(n).Where(IsHidBattery))
            {
                var buf = new byte[len];
                buf[0] = c.ReportID;
                if ((type == 2 ? Native.HidD_GetFeature(h, buf, len) : Native.HidD_GetInputReport(h, buf, len))
                    && Native.HidP_GetUsageValue(type, 6, c.LinkCollection, 0x20, out var value, pp, buf, len) == 0x110000)
                    return HidPercent(value, c.LogicalMin, c.LogicalMax);
            }
        }
        return null;
    }

    static bool IsHidBattery(HidpValueCaps c) =>
        c.UsagePage == 6 && c.LogicalMax > c.LogicalMin && (c.IsRange != 0 ? c.UsageMin <= 0x20 && 0x20 <= c.UsageMax : c.UsageMin == 0x20);

    static int HidPercent(uint value, int min, int max) => Math.Clamp((int)(((long)value - min) * 100 / (max - min)), 0, 100);

    // The USB vendor id (lowercase hex) of a HID path, or null for non-USB paths (Bluetooth is covered by Windows' own battery property).
    static string? UsbVid(string path) =>
        path.StartsWith(@"\\?\hid#vid_", StringComparison.OrdinalIgnoreCase) ? path.Substring(12, 4).ToLowerInvariant() : null;

    // "vid_xxxx&pid_xxxx" of a USB HID path: the same for every interface of one device.
    static string UsbIds(string path) => path.Substring(8, 17).ToLowerInvariant();

    // One device has one path per collection ("&col01", "&col02"); drop that part so they share a key.
    static string PathBase(string path) => Regex.Replace(path, "&col[0-9]+", "", RegexOptions.IgnoreCase);

    static string HidName(SafeFileHandle h, string fallback)
    {
        var buf = new byte[256];
        var name = Native.HidD_GetProductString(h, buf, buf.Length) ? Encoding.Unicode.GetString(buf).TrimEnd('\0') : "";
        return name.Length > 0 ? name : fallback;
    }

    // Readings stay (and grey out once stale) until their device leaves the HID list, so a sleeping device doesn't vanish between polls.
    static void ForgetGone(HashSet<string> shown, string prefix, IEnumerable<string> bases)
    {
        var live = bases.ToList();
        foreach (var key in shown.Where(k => !live.Any(b => k.StartsWith(prefix + b, StringComparison.Ordinal))).ToList())
        {
            Forget(key);
            shown.Remove(key);
        }
    }
}

// HIDP_VALUE_CAPS (72 bytes), flattened so it stays blittable.
[StructLayout(LayoutKind.Sequential)]
struct HidpValueCaps
{
    public ushort UsagePage;
    public byte ReportID, IsAlias;
    public ushort BitField, LinkCollection, LinkUsage, LinkUsagePage;
    public byte IsRange, IsStringRange, IsDesignatorRange, IsAbsolute, HasNull, Reserved;
    public ushort BitSize, ReportCount, Reserved2a, Reserved2b, Reserved2c, Reserved2d, Reserved2e;
    public uint UnitsExp, Units;
    public int LogicalMin, LogicalMax, PhysicalMin, PhysicalMax;
    public ushort UsageMin, UsageMax, StringMin, StringMax, DesignatorMin, DesignatorMax, DataIndexMin, DataIndexMax; // a single usage sits in UsageMin
}
