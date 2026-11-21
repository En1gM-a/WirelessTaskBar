# WirelessTaskBar

See the battery level of your wireless mice, keyboards and headsets on the Windows taskbar.

Each device gets its own tray icon: the device's shape with a battery bar under it. Hover an icon for the device name and exact %. No vendor software needed.

## What the icons mean

| Icon | Meaning |
|---|---|
| mouse, keyboard or headset shape | which device it is (anything else shows a generic icon) |
| bar under the shape | battery level: the fuller the bar, the more charge |
| green / red / blue bar | normal / 20 % or less / charging |
| gray icon | no recent reading: the device is asleep or off |
| gray Bluetooth logo, no bar | no device is reporting battery right now |

Right-click any icon → **Exit** closes the app. On Windows 11 the icons pin themselves to the taskbar, so they never hide behind the **^** arrow. On Windows 10, choose them in *Settings → Personalization → Taskbar → Select which icons appear on the taskbar*.

## Supported devices

| Devices | Connection | Tested on real hardware |
|---|---|---|
| Anything Windows shows a battery % for in *Settings → Bluetooth & devices* (headsets, earbuds, mice, keyboards…) | Bluetooth | not yet |
| Attack Shark mice | 2.4 GHz receiver `1D57:FA60` | yes |
| Aula keyboards | 2.4 GHz receiver `3554:FA09` | yes |
| Logitech mice, keyboards and headsets (HID++ 2.0) | Unifying, Bolt or Lightspeed receiver, or cable | not yet |
| Razer mice, keyboards and headsets | receiver or cable | not yet |
| Any other USB device that reports the standard HID battery level | USB | not yet |

Not supported yet:
- SteelSeries, Corsair, HyperX and other brands that use a different protocol for each model
- older Logitech devices that only speak HID++ 1.0
- game controllers, which get the generic icon

See [Adding a device](#adding-a-device).

## Install and run

You need Windows 10 or 11 and the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).

```powershell
git clone https://github.com/En1gM-a/WirelessTaskBar.git
cd WirelessTaskBar
dotnet build -c Release
.\bin\Release\net10.0-windows\WirelessTaskBar.exe
```

- **Start with Windows:** press **Win + R**, type `shell:startup`, press Enter, and put a shortcut to `WirelessTaskBar.exe` in the folder that opens. Delete the shortcut to stop it.
- **Rebuilding:** exit the app first. The running `.exe` locks the build output.

## Device not showing?

- Check that it's in [Supported devices](#supported-devices) and switched on.
- **2.4 GHz devices:** the receiver must be plugged in and the device set to 2.4 GHz mode, not Bluetooth or cable.
- **Bluetooth devices:** if *Settings → Bluetooth & devices* shows no battery % for it, the device doesn't report one, so the app can't show one either.
- **Aula keyboards** only answer while awake. After a keyboard has slept for a while the app asks less often, at most once an hour. To see it straight away, press a key and restart the app.

## Self-test

```powershell
(Start-Process .\bin\Release\net10.0-windows\WirelessTaskBar.exe -ArgumentList '--selftest' -Wait -PassThru).ExitCode
```

`0` means every reader's parsing checks pass, including real replies captured from an Attack Shark mouse and an Aula keyboard.

## How it works

Plain Win32 calls (`cfgmgr32`, `hid`, `kernel32`, `user32`) and no NuGet packages.

| File | What it reads |
|---|---|
| [Program.cs](Program.cs) | Bluetooth (through Windows) and Attack Shark; also draws the tray icons |
| [Aula.cs](Aula.cs) | Aula receiver |
| [Logitech.cs](Logitech.cs) | Logitech HID++ 2.0 |
| [Razer.cs](Razer.cs) | Razer |
| [GenericHid.cs](GenericHid.cs) | the standard HID battery field |

- **Bluetooth:** every 30 s the app reads the battery value Windows keeps for each device, the device property `{104EA319-6EE2-4701-BD47-8DDBF425BBE5} 2`.
  - A device with its connected flag (`{83DA6326-97A6-4088-9453-A1923F573B29} 15`) set to false is hidden.
  - A headset shows up in Windows as several parts (audio, hands-free, remote control). The app combines them into one icon named after the Bluetooth entry, and picks the shape from those parts.
- **Attack Shark:** the receiver sends `03 xx 40 <state> <level>` about every 2 s (state `03` = charging). The app only listens.
- **Aula:** once a minute the app sends the vendor apps' battery request, `13 4A …` plus a checksum, and reads the % from byte 5 and charging from byte 6 of the reply.
  - A dozing keyboard ignores the first request, so the app resends it up to 3 times.
  - If the keyboard stops answering, the app waits 5, then 10, 20, 40 and 60 minutes between tries, so it doesn't keep waking it.
- **Logitech:** HID++ 2.0 on the long HID++ channel. The app looks up the battery feature (`0x1004`, `0x1000` or `0x1001`) and the name/type feature (`0x0005`). Devices behind a receiver are asked in slots 1–6.
- **Razer:** a 91-byte feature report with command class `07`: `80` for battery, `84` for charging. The app tries the transaction IDs that different models answer to.
- **Standard HID:** any other USB device whose report descriptor declares *Battery Strength* (usage page `0x06`, usage `0x20`).

## Adding a device

1. Find the receiver's USB VID/PID and HID collections (Device Manager → the device → Details → Hardware Ids).
2. Find its battery protocol: an open-source project for the device, or what the vendor's own app sends and receives. Prefer listening, and only ever send a packet the vendor app also sends.
3. Add a file like [Aula.cs](Aula.cs): a `partial class Program` with a loop that calls `Report(...)`, a small pure parser, and a `--selftest` case built from a real report captured from the device.

[BatteryHub's drivers](https://github.com/krisgfx9/Wireless-Device-Battery-Monitor/tree/main/src/drivers) document the protocols of about 100 brands and are the best place to start. BatteryHub is GPL-3.0, so take the protocol facts, not the code.

## Credits

- Attack Shark battery format: [Attack Shark Battery KDE](https://gitlab.com/gabriel17166/Attack-Shark-Battery-KDE) (MIT) by Gabriel Marques Ferrarezi.
- Bluetooth property notes: [nikvoronin/WmiPnp](https://github.com/nikvoronin/WmiPnp).
- Aula, Logitech and Razer protocol facts: [BatteryHub](https://github.com/krisgfx9/Wireless-Device-Battery-Monitor) (GPL-3.0; facts only, no code taken). BatteryHub in turn credits:
  - [PyFlat/Device-Battery-Info](https://github.com/PyFlat/Device-Battery-Info) and [womier-l65-linux](https://github.com/deepan-alve/womier-l65-linux) for Aula
  - [Solaar](https://github.com/pwr-Solaar/Solaar) for Logitech
  - [OpenRazer](https://github.com/openrazer/openrazer) for Razer
