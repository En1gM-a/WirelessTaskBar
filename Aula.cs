// Aula keyboards on the 2.4 GHz receiver USB 3554:FA09 (MCHOSE boards share it).
// We send the vendor apps' battery request "13 4A <0|1> 00.. <sum>" on the FF02/FF04:0002 collection and read the
// reply "13 4A .. <level> <state> .. <sum>": byte 5 = %, byte 6 high nibble 1 = charging. A dozing keyboard ignores
// the first request, so it is resent up to 3 times, 1.3 s apart. Frame format as documented by BatteryHub
// (github.com/krisgfx9/Wireless-Device-Battery-Monitor, src/drivers/aula.cpp, GPL-3.0; facts only, no code taken).
static partial class Program
{
    static void ReadAula()
    {
        var misses = 0;
        while (true)
        {
            var answered = PollAula();
            if (answered == null) { Forget("aula"); misses = 0; } // no receiver plugged in
            else misses = answered.Value ? 0 : misses + 1;
            // Every request wakes the keyboard, so back off while it stays silent: 1 min, then 5, 10, 20, 40, 60 min.
            Thread.Sleep(TimeSpan.FromMinutes(misses < 3 ? 1 : Math.Min(5 << Math.Min(misses - 3, 4), 60)));
        }
    }

    // null = no receiver, false = no answer, true = reading stored.
    static bool? PollAula()
    {
        foreach (var path in Native.HidPaths().Where(p => p.Contains("vid_3554&pid_fa09", StringComparison.OrdinalIgnoreCase)))
        {
            using var reader = Native.CreateFile(path, 0xC0000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero); // GENERIC_READ|WRITE, shared, OPEN_EXISTING
            if (reader.IsInvalid || Native.Caps(reader) is not { UsagePage: 0xFF02 or 0xFF04, Usage: 2, InputReportByteLength: >= 20, OutputReportByteLength: >= 20 } caps) continue;
            using var writer = Native.CreateFile(path, 0xC0000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero); // a send must not queue behind the blocking read
            var reply = new TaskCompletionSource<byte[]>();
            new Thread(() =>
            {
                var buf = new byte[caps.InputReportByteLength];
                while (!reply.Task.IsCompleted && Native.ReadFile(reader, buf, buf.Length, out var n, IntPtr.Zero))
                    if (IsAulaReply(buf, n)) reply.TrySetResult(buf[..n]);
            }) { IsBackground = true }.Start();
            for (var sent = 0; sent < 3 && !reply.Task.IsCompleted; sent++)
            {
                var req = new byte[caps.OutputReportByteLength];
                req[0] = 0x13; req[1] = 0x4A; req[2] = (byte)(sent & 1);
                req[19] = AulaSum(req);
                if (!Native.WriteFile(writer, req, req.Length, out _, IntPtr.Zero)) break; // receiver gone
                reply.Task.Wait(1300);
            }
            Native.CancelIoEx(reader, IntPtr.Zero); // release the reader thread before the handles close
            if (!reply.Task.IsCompleted) return false;
            var f = reply.Task.Result;
            Report("aula", "Aula keyboard (2.4G)", "keyboard", f[5], f[6] >> 4 == 1, TimeSpan.FromMinutes(30)); // idle keyboards miss some polls
            return true;
        }
        return null;
    }

    static byte AulaSum(byte[] f) { var s = 0; for (var i = 0; i < 19; i++) s += f[i]; return (byte)s; }

    // The receiver also pushes other 0x13 frames and echoes requests, so match command, payload and checksum
    // (MCHOSE firmware leaves the checksum at 0 on single-packet replies).
    static bool IsAulaReply(byte[] f, int n) =>
        n >= 20 && f[0] == 0x13 && (f[1] & 0x7F) == 0x4A && f[4..19].Any(b => b != 0)
        && (f[19] == AulaSum(f) || f[19] == 0 && f[2] == 1 && f[3] == 0) && f[5] is >= 1 and <= 100;
}
