# WirelessTaskBar

A small Windows tray app that shows the battery level of your wireless devices (headsets, mice, keyboards, controllers) in the taskbar. Each device gets its own icon: the device's shape with a battery bar under it.

| Icon | Meaning |
|---|---|
| mouse, keyboard or headset shape | which device it is; other Bluetooth devices show the Bluetooth logo |
| bar under the shape | battery level: the fuller the bar, the more charge |
| green / red / blue bar | normal / 20 % or less / charging |
| gray icon | no reading for 60 s (device asleep or off) |
| gray Bluetooth logo, no bar | no device is reporting battery right now |

Hover an icon to see the device name and exact %. Right-click → **Exit** closes the app.

## Supported devices

- **Bluetooth** (headsets, earbuds, mice, keyboards, controllers): anything Windows shows a battery % for in *Settings → Bluetooth & devices*. Paired devices that are switched off are hidden.
- **2.4 GHz receivers and USB**:
  - Attack Shark mice on the `1D57:FA60` receiver
  - Aula keyboards on the `3554:FA09` receiver
  - Logitech mice, keyboards and headsets that speak HID++ 2.0 (Unifying, Bolt and Lightspeed receivers, or a cable)
  - Razer mice, keyboards and headsets
  - any other USB device that reports the standard HID battery level

SteelSeries, Corsair, HyperX and most other brands use a different private protocol per model and aren't supported yet. See [Adding a 2.4 GHz device](#adding-a-24-ghz-device).

## Requirements

- Windows 10 or 11
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) to build it

## Build and run

```powershell
git clone https://github.com/En1gM-a/WirelessTaskBar.git
cd WirelessTaskBar
dotnet build -c Release
.\bin\Release\net10.0-windows\WirelessTaskBar.exe
```

On Windows 11 the app pins its icons to the taskbar itself (the same switch as *Settings → Personalization → Taskbar → Other system tray icons*), so they never hide behind the **^** arrow. On Windows 10, pick them in *Settings → Personalization → Taskbar → Select which icons appear on the taskbar*.

Exit the app before rebuilding: the running `.exe` locks the build output.

### Self-test

```powershell
(Start-Process .\bin\Release\net10.0-windows\WirelessTaskBar.exe -ArgumentList '--selftest' -Wait -PassThru).ExitCode
```

`0` means the Attack Shark parser and the Bluetooth device checks passed.

## Start with Windows

Press **Win + R**, type `shell:startup`, press Enter, and put a shortcut to `WirelessTaskBar.exe` in the folder that opens. Delete the shortcut to stop it starting with Windows.

## How it works

The app is [Program.cs](Program.cs) plus one file per brand ([Aula.cs](Aula.cs), [Logitech.cs](Logitech.cs), [Razer.cs](Razer.cs), [GenericHid.cs](GenericHid.cs)): plain Win32 calls (`cfgmgr32`, `hid`, `kernel32`, `user32`) and no NuGet packages.

- **Bluetooth**: every 30 s it reads the battery value Windows keeps for each device (device property `{104EA319-6EE2-4701-BD47-8DDBF425BBE5} 2`, the number shown in Settings). One physical device has several device nodes (a headset has audio, hands-free and remote-control nodes), so the app shows one icon per device, named after its Bluetooth entry. Devices whose connected flag (`{83DA6326-97A6-4088-9453-A1923F573B29} 15`) is false are hidden.
- **Attack Shark 2.4 GHz**: the receiver sends a 5-byte report about every 2 s on its vendor HID interface: `03 xx 40 <state> <level>` (state `03` = charging, level = %). The app only listens and never sends anything to the device.
- **Aula 2.4 GHz**: once a minute the app sends the battery request the vendor apps use, `13 4A …` plus a checksum, on the receiver's vendor channel. The reply carries the % in byte 5 and charging in byte 6. A dozing keyboard ignores the first request, so it's resent up to 3 times. If the keyboard stops answering, the app waits 5, then 10, 20, 40 and 60 minutes between tries so it doesn't keep waking it.
- **Logitech**: HID++ 2.0 over the receiver's long HID++ channel. The app looks up the battery feature (0x1004, 0x1000 or 0x1001) and the name/type feature (0x0005), then asks for the level.
- **Razer**: a 91-byte feature report (command class `07`, battery `80`, charging `84`), trying the transaction IDs different models use.
- **Any other USB device**: if its HID report descriptor declares the standard *Battery Strength* field (usage page `0x06`, usage `0x20`), the app reads that value.- **Icons**: a device's type comes from the Windows device classes of its parts (audio → headset, then mouse, then keyboard). The shape is drawn with Windows' built-in Segoe Fluent Icons font in the taskbar's text colour.

## Adding a 2.4 GHz device

1. Find the receiver's USB VID/PID and HID interfaces (Device Manager → the device → Details → Hardware Ids).
2. Find its battery protocol: an open-source project for that device, or what the vendor's own app sends and receives. Prefer listening, and only ever send a packet the vendor app also sends.
3. Add a reader file like [Aula.cs](Aula.cs): a `partial class Program` with its own loop that calls `Report(...)`, a small pure parser, and a `--selftest` case built from a real report captured from the device.

[BatteryHub's `src/drivers` folder](https://github.com/krisgfx9/Wireless-Device-Battery-Monitor/tree/main/src/drivers) documents the protocols of about 100 brands and is the best place to start. It's GPL-3.0, so take the protocol facts, not the code.

## Limitations

- Tested on real hardware: Attack Shark and Aula. Not tested yet: Bluetooth (the development PC has no Bluetooth radio), Logitech, Razer and the generic USB reader. The two Bluetooth device properties are community-documented, not official.
- Older Logitech devices that only speak HID++ 1.0 aren't read.
- Some headsets never send their battery level to Windows; those won't appear.
- Game controllers show the Bluetooth logo; there's no controller shape yet.

## Credits

- Attack Shark battery format: [Attack Shark Battery KDE](https://gitlab.com/gabriel17166/Attack-Shark-Battery-KDE) (MIT) by Gabriel Marques Ferrarezi.
- Bluetooth property notes: [nikvoronin/WmiPnp](https://github.com/nikvoronin/WmiPnp).
- Aula, Logitech and Razer protocol facts: [BatteryHub](https://github.com/krisgfx9/Wireless-Device-Battery-Monitor) (GPL-3.0; facts only, no code taken), which credits [PyFlat/Device-Battery-Info](https://github.com/PyFlat/Device-Battery-Info) and [womier-l65-linux](https://github.com/deepan-alve/womier-l65-linux) for Aula, [Solaar](https://github.com/pwr-Solaar/Solaar) for Logitech HID++ and [OpenRazer](https://github.com/openrazer/openrazer) for Razer.
