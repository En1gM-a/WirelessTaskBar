using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

// WirelessTaskBar: one tray icon per wireless device: its shape (mouse, keyboard, headset) with a battery bar.
// Sources: (1) the battery level Windows itself keeps for Bluetooth devices, headsets included (same number as Settings > Bluetooth & devices),
//          (2) the Attack Shark 2.4G receiver's vendor HID channel, (3) the Aula 2.4G receiver (Aula.cs),
//          (4) Logitech HID++ (Logitech.cs), Razer (Razer.cs) and the standard HID battery usage (GenericHid.cs).
// shortcut: SteelSeries, Corsair, HyperX and others use a private protocol per model; add a reader once a device is there to test with.
// shortcut: the Bluetooth path was written on a PC without Bluetooth; check names and the connected flag on a real headset.
static partial class Program
{
    record Reading(string Name, string Kind, int Percent, bool Charging, DateTime Seen, TimeSpan Fresh); // Fresh: how long it counts as current
    record Node(string Container, string Name, Guid Class, bool Disconnected, int Battery); // one Windows device node

    static readonly Dictionary<string, Reading> Readings = new(); // written by the reader thread and the poll; lock before use
    static readonly Dictionary<string, (NotifyIcon Icon, string Shown)> Icons = new();
    static int pinTicks; // ticks left to run PinToTaskbar after a new icon appears; it reads every tray app's settings key, too slow for every tick

    static void Report(string key, string name, string kind, int percent, bool charging, TimeSpan fresh)
    {
        lock (Readings) Readings[key] = new(name, kind, percent, charging, DateTime.Now, fresh);
    }

    static void Forget(string key) { lock (Readings) Readings.Remove(key); }

    static readonly DevPropKey BatteryKey = new(new("104EA319-6EE2-4701-BD47-8DDBF425BBE5"), 2);   // byte, 0-100
    static readonly DevPropKey ContainerKey = new(new("8C7ED206-3F8A-4827-B3AB-AE9E1FAEFC6C"), 2); // DEVPKEY_Device_ContainerId
    static readonly DevPropKey NameKey = new(new("B725F130-47EF-101A-A5F1-02608C9EEBAC"), 10);     // DEVPKEY_NAME
    static readonly DevPropKey ClassKey = new(new("A45C254E-DF1C-4EFD-8020-67D146A850E0"), 10);    // DEVPKEY_Device_ClassGuid
    static readonly DevPropKey ConnectedKey = new(new("83DA6326-97A6-4088-9453-A1923F573B29"), 15); // bool, false = disconnected (community-documented, nikvoronin/WmiPnp)
    static readonly Guid BluetoothClass = new("E0CBF06C-CD8B-4647-BB8A-263B43F0F974"), AudioClass = new("C166523C-FE0C-4A94-A586-F1A80CFBBF3E"),
        MediaClass = new("4D36E96C-E325-11CE-BFC1-08002BE10318"), MouseClass = new("4D36E96F-E325-11CE-BFC1-08002BE10318"),
        KeyboardClass = new("4D36E96B-E325-11CE-BFC1-08002BE10318");

    // What a Bluetooth device is, from the classes of its nodes, checked in this order (mice often add a keyboard node for their buttons).
    static readonly (Guid Class, string Kind)[] Kinds = [(AudioClass, "headset"), (MediaClass, "headset"), (MouseClass, "mouse"), (KeyboardClass, "keyboard")];
    static readonly Dictionary<string, string> Glyphs = new() { ["device"] = "\uE88E", ["mouse"] = "\uE962", ["keyboard"] = "\uE765", ["headset"] = "\uE95B", ["bluetooth"] = "\uE702" };
    static readonly string IconFont = FontFamily.Families.Any(f => f.Name == "Segoe Fluent Icons") ? "Segoe Fluent Icons" : "Segoe MDL2 Assets"; // Windows 11 : 10

    [STAThread]
    static int Main(string[] args)
    {
        if (args.Contains("--selftest")) return SelfTest();
        ApplicationConfiguration.Initialize();
        new Thread(ReadAttackShark) { IsBackground = true }.Start();
        new Thread(ReadAula) { IsBackground = true }.Start();
        new Thread(ReadLogitech) { IsBackground = true }.Start();
        new Thread(ReadRazer) { IsBackground = true }.Start();
        new Thread(ReadGenericHid) { IsBackground = true }.Start();
        var menu = new ContextMenuStrip();
        menu.Items.Add("Exit", null, (_, _) => Application.Exit());
        var ticks = 0;
        var timer = new System.Windows.Forms.Timer { Interval = 2000 };
        timer.Tick += (_, _) => { if (ticks++ % 15 == 0) PollWindowsBattery(); Sync(menu); };
        timer.Start();
        Application.ApplicationExit += (_, _) => { foreach (var (icon, _) in Icons.Values) icon.Dispose(); };
        Application.Run();
        return 0;
    }

    // Make the tray match Readings: one icon per device, redrawn only when what it shows or the taskbar theme changes.
    static void Sync(ContextMenuStrip menu)
    {
        Dictionary<string, Reading> now;
        lock (Readings) now = new(Readings);
        var light = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "SystemUsesLightTheme", 0) is 1;
        var want = now.ToDictionary(kv => kv.Key, kv => Describe(kv.Value));
        if (want.Count == 0) want["none"] = ("bluetooth", -1, Color.Gray, "WirelessTaskBar: no device is reporting battery"); // keeps Exit reachable
        foreach (var key in Icons.Keys.Except(want.Keys).ToList())
        {
            Native.DestroyIcon(Icons[key].Icon.Icon!.Handle);
            Icons[key].Icon.Dispose();
            Icons.Remove(key);
        }
        foreach (var (key, (kind, percent, bar, tip)) in want)
        {
            var look = $"{kind} {percent} {bar.Name} {light} {tip}";
            Icons.TryGetValue(key, out var shown);
            if (shown.Shown == look) continue;
            var icon = shown.Icon ?? new NotifyIcon { ContextMenuStrip = menu };
            if (shown.Icon == null) pinTicks = 15; // Windows may file a new icon's settings key a moment after it first shows, so keep trying for 30 s
            Draw(icon, Glyphs[kind], percent, bar, light);
            icon.Text = tip.Length > 127 ? tip[..127] : tip; // shell limit
            icon.Visible = true;
            Icons[key] = (icon, look);
        }
        if (pinTicks > 0) { pinTicks--; PinToTaskbar(); }
    }

    // Bar: red at 20 % or less, blue while charging; the whole icon turns gray once the reading is older than its Fresh time.
    static (string Kind, int Percent, Color Bar, string Tip) Describe(Reading r) =>
        DateTime.Now - r.Seen > r.Fresh
            ? (r.Kind, r.Percent, Color.Gray, $"{r.Name}: asleep (last {r.Percent}%)")
            : (r.Kind, r.Percent, r.Charging ? Color.DodgerBlue : r.Percent <= 20 ? Color.Red : Color.LimeGreen,
               $"{r.Name}: {r.Percent}%" + (r.Charging ? ", charging" : ""));

    // The device's glyph in the taskbar's text color, plus a battery bar (none when percent < 0). Like Windows' own tray icons, the glyph
    // keeps the icon font's size and grid (16 px at 100 %), so its 1 px lines land on whole pixels instead of smearing gray.
    static void Draw(NotifyIcon icon, string glyph, int percent, Color bar, bool light)
    {
        var s = SystemInformation.SmallIconSize.Width;
        var h = Math.Max(2, s / 8); // bar thickness, and the gap between bar and glyph
        using var family = new FontFamily(IconFont);
        using var path = new GraphicsPath();
        path.AddString(glyph, family, 0, s, PointF.Empty, StringFormat.GenericTypographic);
        var (under, dx, dy) = percent >= 0 ? Fit(path.GetBounds(), s, h) : (false, 0, 0);
        using var bmp = new Bitmap(s, s);
        using (var g = Graphics.FromImage(bmp))
        using (var ink = new SolidBrush(bar == Color.Gray ? Color.Gray : light ? Color.Black : Color.White))
        using (var track = new SolidBrush(Color.FromArgb(120, Color.Gray)))
        using (var fill = new SolidBrush(bar))
        {
            g.PixelOffsetMode = PixelOffsetMode.Half; // whole-number coordinates are pixel edges, so the font's grid meets the pixel grid
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TranslateTransform(dx, dy);
            g.FillPath(ink, path);
            g.ResetTransform();
            g.SmoothingMode = SmoothingMode.None;
            if (percent >= 0)
            {
                var level = Math.Max(1, s * percent / 100);
                if (under) { g.FillRectangle(track, 0, s - h, s, h); g.FillRectangle(fill, 0, s - h, level, h); }
                else { g.FillRectangle(track, s - h, 0, h, s); g.FillRectangle(fill, s - h, s - level, h, level); }
            }
        }
        var old = icon.Icon;
        icon.Icon = Icon.FromHandle(bmp.GetHicon());
        if (old != null) Native.DestroyIcon(old.Handle); // FromHandle icons never free their HICON
    }

    // Where the bar goes: under a low glyph (keyboard), else beside it (mouse, headset, which fill the full height). The glyph moves
    // by whole pixels only, so it stays sharp, and just far enough to leave a bar-wide gap.
    static (bool Under, int Dx, int Dy) Fit(RectangleF glyph, int s, int h) =>
        glyph.Height <= s - 2 * h
            ? (true, 0, Math.Min(0, (int)Math.Floor(s - 2 * h - glyph.Bottom)))
            : (false, Math.Min(0, (int)Math.Floor(s - 2 * h - glyph.Right)), 0);

    // Windows 11 tucks new tray icons behind the ^ arrow. Flip ours to "show on taskbar": the same switch as
    // Settings > Personalization > Taskbar > Other system tray icons. Windows 10 has no such key, so this does nothing there.
    static void PinToTaskbar()
    {
        using var root = Registry.CurrentUser.OpenSubKey(@"Control Panel\NotifyIconSettings");
        foreach (var name in root?.GetSubKeyNames() ?? [])
        {
            using var key = root!.OpenSubKey(name);
            if (key?.GetValue("ExecutablePath") is string path && path.EndsWith(@"\WirelessTaskBar.exe", StringComparison.OrdinalIgnoreCase) && key.GetValue("IsPromoted") is not 1)
                using (var w = root.OpenSubKey(name, writable: true)) w?.SetValue("IsPromoted", 1, RegistryValueKind.DWord);
        }
    }

    // Attack Shark receiver (USB 1D57:FA60) pushes "03 xx 40 <state> <level>" on interface 2 every ~2 s. We only listen, never write.
    // Format from gitlab.com/gabriel17166/Attack-Shark-Battery-KDE (MIT); byte 1 differs per mouse (E5 there, B1 here), so it is not checked.
    static void ReadAttackShark()
    {
        while (true)
        {
            foreach (var path in Native.HidPaths().Where(p => p.Contains("vid_1d57&pid_fa60&mi_02", StringComparison.OrdinalIgnoreCase)))
            {
                using var h = Native.CreateFile(path, 0x80000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero); // GENERIC_READ, shared, OPEN_EXISTING
                var len = h.IsInvalid ? 0 : Native.Caps(h).InputReportByteLength;
                if (len < 5) continue;
                var buf = new byte[len];
                while (Native.ReadFile(h, buf, len, out var n, IntPtr.Zero)) // blocks until the next report, fails once unplugged
                    if (ParseShark(buf, n) is (int percent, bool charging))
                        Report("shark", "Attack Shark mouse (2.4G)", "mouse", percent, charging, TimeSpan.FromSeconds(60)); // reports every ~2 s while awake
            }
            Forget("shark");
            Thread.Sleep(5000);
        }
    }

    static (int Percent, bool Charging)? ParseShark(byte[] f, int n) =>
        n >= 5 && f[0] == 0x03 && f[2] == 0x40 && f[4] <= 100 ? (f[4], f[3] == 0x03) : null;

    static void PollWindowsBattery()
    {
        // Only Bluetooth devices carry this battery value: with none present, skip reading every device on the PC.
        if ((Native.DeviceIds(BluetoothClass) is [] ? [] : Native.DeviceIds()) is not { } ids) return; // null: list changed mid-read; the next poll retries
        var nodes = new List<Node>();
        foreach (var id in ids)
        {
            if (Native.CM_Locate_DevNode(out var dev, id, 0) != 0) continue;
            var battery = Native.Prop(dev, BatteryKey) is [var b] && b <= 100 ? b : -1;
            var cls = Native.Prop(dev, ClassKey) is { Length: 16 } g ? new Guid(g) : Guid.Empty;
            if (battery < 0 && cls != BluetoothClass && !Kinds.Any(k => k.Class == cls)) continue;
            var container = Native.Prop(dev, ContainerKey) is { Length: 16 } c ? new Guid(c).ToString() : id;
            var name = Native.Prop(dev, NameKey) is { } n ? Encoding.Unicode.GetString(n).TrimEnd('\0') : id;
            nodes.Add(new(container, name, cls, Native.Prop(dev, ConnectedKey) is [0], battery));
        }
        var found = PickBluetooth(nodes, DateTime.Now);
        lock (Readings)
        {
            foreach (var key in Readings.Keys.Where(k => k.StartsWith("win:")).ToList()) Readings.Remove(key);
            foreach (var (key, r) in found) Readings[key] = r;
        }
    }

    // A device has several nodes in one container (a headset: audio, hands-free, remote control...). Keep one reading per container,
    // named after its Bluetooth node rather than e.g. "... Hands-Free AG", typed by its other nodes, and drop devices Windows marks
    // as disconnected so a switched-off headset doesn't keep showing its last %.
    static Dictionary<string, Reading> PickBluetooth(List<Node> nodes, DateTime now)
    {
        var bt = nodes.Where(n => n.Class == BluetoothClass).DistinctBy(n => n.Container).ToDictionary(n => n.Container);
        return nodes.Where(n => n.Battery >= 0).DistinctBy(n => n.Container)
            .Where(n => !(bt.GetValueOrDefault(n.Container)?.Disconnected ?? n.Disconnected))
            .ToDictionary(n => "win:" + n.Container, n => new Reading(
                bt.GetValueOrDefault(n.Container)?.Name ?? n.Name,
                Kinds.FirstOrDefault(k => nodes.Any(x => x.Container == n.Container && x.Class == k.Class)).Kind ?? "bluetooth",
                n.Battery, false, now, TimeSpan.FromMinutes(2)));
    }

    // "WirelessTaskBar.exe --selftest" exits 0 when every reader's parsing and the Bluetooth device picking pass these cases.
    static int SelfTest()
    {
        List<Node> headset = [new("c1", "WH-1000XM4", BluetoothClass, false, -1), new("c1", "WH-1000XM4 Hands-Free AG", MediaClass, false, 80), new("c1", "Headphones", AudioClass, false, -1)];
        List<Node> headsetOff = [new("c1", "WH-1000XM4", BluetoothClass, true, -1), new("c1", "WH-1000XM4 Hands-Free AG", MediaClass, false, 80)];
        List<Node> mouseWithKeys = [new("c2", "MX Master 3", BluetoothClass, false, 60), new("c2", "HID Keyboard Device", KeyboardClass, false, -1), new("c2", "HID-compliant mouse", MouseClass, false, -1)];
        List<Node> other = [new("c3", "Pen", BluetoothClass, false, 50)];
        return (ParseShark([0x03, 0xB1, 0x40, 0x01, 0x4E], 5) is (78, false)    // real frame from this PC's receiver
             && ParseShark([0x03, 0xE5, 0x40, 0x03, 0x64], 5) is (100, true) // charging, at 100 %
             && ParseShark([0x03, 0xE5, 0x01, 0x00, 0x00], 5) is null        // another message type
             && ParseShark([0x03, 0xB1, 0x40, 0x01, 0x65], 5) is null        // 101 % is not a level
             && ParseShark([0x03, 0xB1, 0x40], 3) is null
             && PickBluetooth(headset, DateTime.Now) is { Count: 1 } shown && shown["win:c1"] is { Name: "WH-1000XM4", Kind: "headset", Percent: 80 }
             && PickBluetooth(headsetOff, DateTime.Now).Count == 0
             && PickBluetooth(mouseWithKeys, DateTime.Now)["win:c2"] is { Kind: "mouse", Percent: 60 }
             && PickBluetooth(other, DateTime.Now)["win:c3"] is { Kind: "bluetooth" }
             && IsAulaReply(Convert.FromHexString("134A0100026401" + new string('0', 24) + "C5"), 20)   // real reply from this PC's Aula: 100 %, on battery
             && !IsAulaReply(Convert.FromHexString("134A00" + new string('0', 32) + "5D"), 20)          // our own request echoed back
             && !IsAulaReply(Convert.FromHexString("134A0100026401" + new string('0', 24) + "C6"), 20)  // bad checksum
             && ParseLogiBattery(0x1004, [0x11, 0xFF, 5, 0x1D, 73, 0, 1]) is (73, true)    // 0x1004: 73 %, charging
             && ParseLogiBattery(0x1004, [0x11, 0xFF, 5, 0x1D, 0, 4, 0]) is (50, false)    // level flag "good"
             && ParseLogiBattery(0x1000, [0x11, 0xFF, 5, 0x0D, 40, 0, 4]) is (40, true)
             && ParseLogiBattery(0x1001, [0x11, 0xFF, 5, 0x0D, 0x10, 0x68, 0x80]) is (100, true) // 4200 mV
             && LogiVoltage(4200) == 100 && LogiVoltage(3400) == 0
             && LogiKind(0) == "keyboard" && LogiKind(4) == "mouse" && LogiKind(8) == "headset" && LogiKind(1) == "device"
             && IsLogiReply([0x11, 0xFF, 5, 0x1D, 1, 0, 0], [0x11, 0xFF, 5, 0x1D]) && !IsLogiReply([0x11, 0x01, 5, 0x1D, 1, 0, 0], [0x11, 0xFF, 5, 0x1D])
             && RazerReq(0x1F, 0x80)[89] == 0x85 && RazerXor(RazerReq(0x3F, 0x84)) == RazerReq(0x3F, 0x84)[89]
             && RazerPercent(255) == 100 && RazerPercent(128) == 50
             && RazerKind("Razer BlackWidow V3") == "keyboard" && RazerKind("Razer Kraken") == "headset" && RazerKind("Razer DeathAdder") == "mouse"
             && HidPercent(50, 0, 100) == 50 && HidPercent(200, 0, 255) == 78 && HidPercent(300, 0, 100) == 100 && HidPercent(0, 1, 5) == 0
             && Marshal.SizeOf<HidpValueCaps>() == 72
             && Fit(new(2, 0, 11, 16), 16, 2) == (false, -1, 0) && Fit(new(0, 2, 16, 11), 16, 2) == (true, 0, -1) // mouse, keyboard glyph bounds at 16 px
             && PathBase(@"\\?\hid#vid_046d&pid_c52b&mi_02&col02#8&1#{x}") == @"\\?\hid#vid_046d&pid_c52b&mi_02#8&1#{x}")
            ? 0 : 1;
    }
}

static class Native
{
    public static string[] HidPaths()
    {
        var hid = new Guid("4D1E55B2-F16F-11CF-88CB-001111000030");
        if (CM_Get_Device_Interface_List_Size(out var len, ref hid, null, 0) != 0) return [];
        var buf = new char[len];
        return CM_Get_Device_Interface_List(ref hid, null, buf, len, 0) == 0 ? Split(buf) : [];
    }

    // Present devices, or only those of one setup class.
    public static string[]? DeviceIds(Guid? cls = null)
    {
        var (filter, flags) = cls is { } c ? (c.ToString("B"), 0x300) : (null, 0x100); // CM_GETIDLIST_FILTER_PRESENT, | _CLASS
        if (CM_Get_Device_ID_List_Size(out var len, filter, flags) != 0) return null;
        var buf = new char[len];
        return CM_Get_Device_ID_List(filter, buf, len, flags) == 0 ? Split(buf) : null;
    }

    public static byte[]? Prop(int dev, DevPropKey key)
    {
        var size = 0;
        CM_Get_DevNode_Property(dev, ref key, out _, null, ref size, 0);
        var buf = new byte[size];
        return size > 0 && CM_Get_DevNode_Property(dev, ref key, out _, buf, ref size, 0) == 0 ? buf : null;
    }

    public static HidpCaps Caps(SafeFileHandle h)
    {
        if (!HidD_GetPreparsedData(h, out var pp)) return default;
        HidP_GetCaps(pp, out var caps);
        HidD_FreePreparsedData(pp);
        return caps;
    }

    static string[] Split(char[] multiSz) => new string(multiSz).Split('\0', StringSplitOptions.RemoveEmptyEntries);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)] static extern int CM_Get_Device_Interface_List_Size(out int len, ref Guid cls, string? devId, int flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)] static extern int CM_Get_Device_Interface_List(ref Guid cls, string? devId, char[] buf, int len, int flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)] static extern int CM_Get_Device_ID_List_Size(out int len, string? filter, int flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)] static extern int CM_Get_Device_ID_List(string? filter, char[] buf, int len, int flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)] public static extern int CM_Locate_DevNode(out int dev, string id, int flags);
    [DllImport("cfgmgr32.dll", EntryPoint = "CM_Get_DevNode_PropertyW")] static extern int CM_Get_DevNode_Property(int dev, ref DevPropKey key, out int type, byte[]? buf, ref int size, int flags);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] public static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr sa, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool ReadFile(SafeFileHandle h, byte[] buf, int len, out int read, IntPtr overlapped);
    [DllImport("kernel32.dll", SetLastError = true)] public static extern bool WriteFile(SafeFileHandle h, byte[] buf, int len, out int written, IntPtr overlapped);
    [DllImport("kernel32.dll")] public static extern bool CancelIoEx(SafeFileHandle h, IntPtr overlapped);
    [DllImport("hid.dll")] public static extern bool HidD_GetPreparsedData(SafeFileHandle h, out IntPtr pp);
    [DllImport("hid.dll")] public static extern bool HidD_FreePreparsedData(IntPtr pp);
    [DllImport("hid.dll")] public static extern bool HidD_SetFeature(SafeFileHandle h, byte[] buf, int len);
    [DllImport("hid.dll")] public static extern bool HidD_GetFeature(SafeFileHandle h, byte[] buf, int len);
    [DllImport("hid.dll")] public static extern bool HidD_GetInputReport(SafeFileHandle h, byte[] buf, int len);
    [DllImport("hid.dll")] public static extern bool HidD_GetProductString(SafeFileHandle h, byte[] buf, int len);
    [DllImport("hid.dll")] public static extern int HidP_GetValueCaps(int type, [Out] HidpValueCaps[] caps, ref ushort len, IntPtr pp);
    [DllImport("hid.dll")] public static extern int HidP_GetUsageValue(int type, ushort page, ushort link, ushort usage, out uint value, IntPtr pp, byte[] report, int len);
    [DllImport("hid.dll")] static extern int HidP_GetCaps(IntPtr pp, out HidpCaps caps);
    [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr icon);
}

[StructLayout(LayoutKind.Sequential)]
record struct DevPropKey(Guid Fmtid, uint Pid);

[StructLayout(LayoutKind.Sequential)]
struct HidpCaps
{
    public ushort Usage, UsagePage, InputReportByteLength, OutputReportByteLength, FeatureReportByteLength;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
    public ushort NumberLinkCollectionNodes, NumberInputButtonCaps, NumberInputValueCaps, NumberInputDataIndices,
        NumberOutputButtonCaps, NumberOutputValueCaps, NumberOutputDataIndices,
        NumberFeatureButtonCaps, NumberFeatureValueCaps, NumberFeatureDataIndices;
}
