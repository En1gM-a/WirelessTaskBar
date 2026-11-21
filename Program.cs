using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

// WirelessTaskBar: one tray icon per wireless device, showing its battery %.
// Sources: (1) the battery level Windows itself keeps for Bluetooth devices, headsets included (same number as Settings > Bluetooth & devices),
//          (2) the Attack Shark 2.4G receiver's vendor HID channel.
// shortcut: other 2.4G brands (Logitech HID++, Razer, SteelSeries, ...) each use a private protocol; add one reader per brand once a device is there to test with.
// shortcut: the Bluetooth path was written on a PC without Bluetooth; check names and the connected flag on a real headset.
static class Program
{
    record Reading(string Name, int Percent, bool Charging, DateTime Seen);
    record Node(string Container, string Name, bool IsBluetooth, bool Disconnected, int Battery); // one Windows device node

    static readonly Dictionary<string, Reading> Readings = new(); // written by the reader thread and the poll; lock before use
    static readonly Dictionary<string, (NotifyIcon Icon, string Shown)> Icons = new();

    static readonly DevPropKey BatteryKey = new(new("104EA319-6EE2-4701-BD47-8DDBF425BBE5"), 2);   // byte, 0-100
    static readonly DevPropKey ContainerKey = new(new("8C7ED206-3F8A-4827-B3AB-AE9E1FAEFC6C"), 2); // DEVPKEY_Device_ContainerId
    static readonly DevPropKey NameKey = new(new("B725F130-47EF-101A-A5F1-02608C9EEBAC"), 10);     // DEVPKEY_NAME
    static readonly DevPropKey ClassKey = new(new("A45C254E-DF1C-4EFD-8020-67D146A850E0"), 10);    // DEVPKEY_Device_ClassGuid
    static readonly DevPropKey ConnectedKey = new(new("83DA6326-97A6-4088-9453-A1923F573B29"), 15); // bool, false = disconnected (community-documented, nikvoronin/WmiPnp)
    static readonly Guid BluetoothClass = new("E0CBF06C-CD8B-4647-BB8A-263B43F0F974");

    [STAThread]
    static int Main(string[] args)
    {
        if (args.Contains("--selftest")) return SelfTest();
        ApplicationConfiguration.Initialize();
        new Thread(ReadAttackShark) { IsBackground = true }.Start();
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

    // Make the tray match Readings: one icon per device, redrawn only when its text changes.
    static void Sync(ContextMenuStrip menu)
    {
        Dictionary<string, Reading> now;
        lock (Readings) now = new(Readings);
        var want = now.ToDictionary(kv => kv.Key, kv => Describe(kv.Value));
        if (want.Count == 0) want["none"] = ("–", "WirelessTaskBar: no device is reporting battery", Color.Gray); // keeps Exit reachable
        foreach (var key in Icons.Keys.Except(want.Keys).ToList())
        {
            Native.DestroyIcon(Icons[key].Icon.Icon!.Handle);
            Icons[key].Icon.Dispose();
            Icons.Remove(key);
        }
        foreach (var (key, (label, tip, color)) in want)
        {
            Icons.TryGetValue(key, out var shown);
            if (shown.Shown == label + tip) continue;
            var icon = shown.Icon ?? new NotifyIcon { ContextMenuStrip = menu };
            Draw(icon, label, color);
            icon.Text = tip.Length > 127 ? tip[..127] : tip; // shell limit
            icon.Visible = true;
            Icons[key] = (icon, label + tip);
        }
    }

    static (string Label, string Tip, Color Color) Describe(Reading r) =>
        DateTime.Now - r.Seen > TimeSpan.FromSeconds(60)
            ? ("zz", $"{r.Name}: asleep (last {r.Percent}%)", Color.Gray)
            : (r.Percent.ToString(), $"{r.Name}: {r.Percent}%" + (r.Charging ? ", charging" : ""),
               r.Charging ? Color.RoyalBlue : r.Percent <= 20 ? Color.Firebrick : Color.ForestGreen);

    // Battery % as white text on a colored square, sized for the current DPI.
    static void Draw(NotifyIcon icon, string label, Color background)
    {
        var s = SystemInformation.SmallIconSize.Width;
        using var bmp = new Bitmap(s, s);
        using (var g = Graphics.FromImage(bmp))
        using (var brush = new SolidBrush(background))
        using (var font = new Font("Segoe UI", s * (label.Length > 2 ? 0.5f : 0.72f), FontStyle.Bold, GraphicsUnit.Pixel))
        using (var center = new StringFormat(StringFormat.GenericTypographic) { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
        {
            g.FillRectangle(brush, 0, 0, s, s);
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            g.DrawString(label, font, Brushes.White, new RectangleF(0, 0, s, s), center);
        }
        var old = icon.Icon;
        icon.Icon = Icon.FromHandle(bmp.GetHicon());
        if (old != null) Native.DestroyIcon(old.Handle); // FromHandle icons never free their HICON
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
                var len = h.IsInvalid ? 0 : Native.InputLength(h);
                if (len < 5) continue;
                var buf = new byte[len];
                while (Native.ReadFile(h, buf, len, out var n, IntPtr.Zero)) // blocks until the next report, fails once unplugged
                    if (ParseShark(buf, n) is (int percent, bool charging))
                        lock (Readings) Readings["shark"] = new("Attack Shark mouse (2.4G)", percent, charging, DateTime.Now);
            }
            lock (Readings) Readings.Remove("shark");
            Thread.Sleep(5000);
        }
    }

    static (int Percent, bool Charging)? ParseShark(byte[] f, int n) =>
        n >= 5 && f[0] == 0x03 && f[2] == 0x40 && f[4] <= 100 ? (f[4], f[3] == 0x03) : null;

    static void PollWindowsBattery()
    {
        if (Native.DeviceIds() is not { } ids) return; // device list changed mid-read; the next poll retries
        var nodes = new List<Node>();
        foreach (var id in ids)
        {
            if (Native.CM_Locate_DevNode(out var dev, id, 0) != 0) continue;
            var battery = Native.Prop(dev, BatteryKey) is [var b] && b <= 100 ? b : -1;
            var isBluetooth = Native.Prop(dev, ClassKey) is { Length: 16 } g && new Guid(g) == BluetoothClass;
            if (battery < 0 && !isBluetooth) continue;
            var container = Native.Prop(dev, ContainerKey) is { Length: 16 } c ? new Guid(c).ToString() : id;
            var name = Native.Prop(dev, NameKey) is { } n ? Encoding.Unicode.GetString(n).TrimEnd('\0') : id;
            nodes.Add(new(container, name, isBluetooth, Native.Prop(dev, ConnectedKey) is [0], battery));
        }
        var found = PickBluetooth(nodes, DateTime.Now);
        lock (Readings)
        {
            foreach (var key in Readings.Keys.Where(k => k.StartsWith("win:")).ToList()) Readings.Remove(key);
            foreach (var (key, r) in found) Readings[key] = r;
        }
    }

    // A device has several nodes in one container (a headset: audio, hands-free, remote control...). Keep one reading per container,
    // named after its Bluetooth node rather than e.g. "... Hands-Free AG", and drop devices Windows marks as disconnected
    // so a switched-off headset doesn't keep showing its last %.
    static Dictionary<string, Reading> PickBluetooth(List<Node> nodes, DateTime now)
    {
        var bt = nodes.Where(n => n.IsBluetooth).DistinctBy(n => n.Container).ToDictionary(n => n.Container);
        return nodes.Where(n => n.Battery >= 0).DistinctBy(n => n.Container)
            .Where(n => !(bt.GetValueOrDefault(n.Container)?.Disconnected ?? n.Disconnected))
            .ToDictionary(n => "win:" + n.Container, n => new Reading(bt.GetValueOrDefault(n.Container)?.Name ?? n.Name, n.Battery, false, now));
    }

    // "WirelessTaskBar.exe --selftest" exits 0 when the Attack Shark parser and the Bluetooth device picking pass these cases.
    static int SelfTest()
    {
        List<Node> headset = [new("c1", "WH-1000XM4", true, false, -1), new("c1", "WH-1000XM4 Hands-Free AG", false, false, 80), new("c1", "WH-1000XM4 Avrcp", false, false, 80)];
        List<Node> headsetOff = [new("c1", "WH-1000XM4", true, true, -1), new("c1", "WH-1000XM4 Hands-Free AG", false, false, 80)];
        return (ParseShark([0x03, 0xB1, 0x40, 0x01, 0x4E], 5) is (78, false)    // real frame from this PC's receiver
             && ParseShark([0x03, 0xE5, 0x40, 0x03, 0x64], 5) is (100, true) // charging, at 100 %
             && ParseShark([0x03, 0xE5, 0x01, 0x00, 0x00], 5) is null        // another message type
             && ParseShark([0x03, 0xB1, 0x40, 0x01, 0x65], 5) is null        // 101 % is not a level
             && ParseShark([0x03, 0xB1, 0x40], 3) is null
             && PickBluetooth(headset, DateTime.Now) is { Count: 1 } shown && shown["win:c1"] is { Name: "WH-1000XM4", Percent: 80 }
             && PickBluetooth(headsetOff, DateTime.Now).Count == 0) ? 0 : 1;
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

    public static string[]? DeviceIds()
    {
        if (CM_Get_Device_ID_List_Size(out var len, null, 0x100) != 0) return null; // CM_GETIDLIST_FILTER_PRESENT
        var buf = new char[len];
        return CM_Get_Device_ID_List(null, buf, len, 0x100) == 0 ? Split(buf) : null;
    }

    public static byte[]? Prop(int dev, DevPropKey key)
    {
        var size = 0;
        CM_Get_DevNode_Property(dev, ref key, out _, null, ref size, 0);
        var buf = new byte[size];
        return size > 0 && CM_Get_DevNode_Property(dev, ref key, out _, buf, ref size, 0) == 0 ? buf : null;
    }

    public static int InputLength(SafeFileHandle h)
    {
        if (!HidD_GetPreparsedData(h, out var pp)) return 0;
        HidP_GetCaps(pp, out var caps);
        HidD_FreePreparsedData(pp);
        return caps.InputReportByteLength;
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
    [DllImport("hid.dll")] static extern bool HidD_GetPreparsedData(SafeFileHandle h, out IntPtr pp);
    [DllImport("hid.dll")] static extern bool HidD_FreePreparsedData(IntPtr pp);
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
