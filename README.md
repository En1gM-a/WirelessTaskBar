# WirelessTaskBar

A small Windows tray app that shows the battery level of your wireless devices (headsets, mice, keyboards, controllers) in the taskbar. Each device gets its own icon with its battery % on it.

| Icon | Meaning |
|---|---|
| green `77` | battery % |
| red `15` | 20 % or less |
| blue `64` | charging |
| gray `zz` | no reading for 60 s (device asleep or off) |
| gray `–` | no device is reporting battery right now |

Hover an icon to see the device name. Right-click → **Exit** closes the app.

## Supported devices

- **Bluetooth** (headsets, earbuds, mice, keyboards, controllers): anything Windows shows a battery % for in *Settings → Bluetooth & devices*. Paired devices that are switched off are hidden.
- **2.4 GHz receivers**: Attack Shark mice that use the `1D57:FA60` receiver.

Other 2.4 GHz brands (Logitech, Razer, SteelSeries, Corsair, HyperX…) each use their own private protocol and aren't supported yet. See [Adding a 2.4 GHz device](#adding-a-24-ghz-device).

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

Windows 11 hides new tray icons at first. Click **^** next to the clock and drag the icon onto the taskbar, or turn it on in *Settings → Personalization → Taskbar → Other system tray icons*.

Exit the app before rebuilding: the running `.exe` locks the build output.

### Self-test

```powershell
(Start-Process .\bin\Release\net10.0-windows\WirelessTaskBar.exe -ArgumentList '--selftest' -Wait -PassThru).ExitCode
```

`0` means the Attack Shark parser and the Bluetooth device checks passed.

## Start with Windows

Press **Win + R**, type `shell:startup`, press Enter, and put a shortcut to `WirelessTaskBar.exe` in the folder that opens. Delete the shortcut to stop it starting with Windows.

## How it works

Everything is in [Program.cs](Program.cs): plain Win32 calls (`cfgmgr32`, `hid`, `kernel32`, `user32`) and no NuGet packages.

- **Bluetooth**: every 30 s it reads the battery value Windows keeps for each device (device property `{104EA319-6EE2-4701-BD47-8DDBF425BBE5} 2`, the number shown in Settings). One physical device has several device nodes (a headset has audio, hands-free and remote-control nodes), so the app shows one icon per device, named after its Bluetooth entry. Devices whose connected flag (`{83DA6326-97A6-4088-9453-A1923F573B29} 15`) is false are hidden.
- **Attack Shark 2.4 GHz**: the receiver sends a 5-byte report about every 2 s on its vendor HID interface: `03 xx 40 <state> <level>` (state `03` = charging, level = %). The app only listens and never sends anything to the device.

## Adding a 2.4 GHz device

1. Find the receiver's USB VID/PID and HID interfaces (Device Manager → the device → Details → Hardware Ids).
2. Find its battery protocol: an open-source project for that device, or what the vendor's own app sends and receives. Prefer listening, and only ever send a packet the vendor app also sends.
3. Add a reader like `ReadAttackShark` plus a parser, and a `--selftest` case built from a real report captured from the device.

## Limitations

- The Bluetooth part hasn't been tested on real hardware yet (the development PC has no Bluetooth radio). Both device properties above are community-documented, not official.
- Some headsets never send their battery level to Windows; those won't appear.

## Credits

- Attack Shark battery format: [Attack Shark Battery KDE](https://gitlab.com/gabriel17166/Attack-Shark-Battery-KDE) (MIT) by Gabriel Marques Ferrarezi.
- Bluetooth property notes: [nikvoronin/WmiPnp](https://github.com/nikvoronin/WmiPnp).
