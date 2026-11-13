# Tartarus Studio

A Windows 11 configuration app for the original Razer Tartarus (RZ07-0103, USB `1532:0201`):
visual key mapping, macros, per-game profiles with automatic switching, up to 8 keymaps per
profile with the indicator LEDs, and backlight control. It installs no keyboard filter driver
and does not touch Razer Synapse 4.

**Status: early, but working.** Confirmed on a real Tartarus: key mapping in Direct mode
(WinUSB set up with Zadig), profiles, macros, and lighting (backlight, pulsate). Hook mode was
reworked after a first hardware test and has not been re-tested since. The Log on the Settings
tab shows what the input engine is doing.

## Run it

Build `TartarusStudio.exe` as described under [Build from source](#build-from-source), then run
it. Settings live in `%AppData%\TartarusStudio\config.json`. Closing the window keeps the app
running in the tray; exit from the tray icon.

## How it reads the keypad

Out of the box the Tartarus is just a keyboard that types Tab, Q, W, E, R and so on. To remap
it without also remapping those keys on your main keyboard, the app must know which device a
keystroke came from. Two engines are included (Settings → Input engine):

### Direct mode (recommended)

The keypad's keyboard interface is switched to WinUSB, the generic USB driver that ships with
Windows. Windows then no longer sees it as a keyboard, and the app reads the key reports itself
and sends the mapped keys. This is exact and works the same in every game.

One-time setup:

1. Download [Zadig](https://zadig.akeo.ie) and run it with the keypad plugged in.
2. *Options → List All Devices*.
3. Select the Tartarus entry ending in **(Interface 0)**; the USB ID must read `1532 0201 00`.
4. Choose **WinUSB** and press *Replace Driver*.
5. In the app the status line should read "Direct mode: connected".

One of the two "HID Keyboard Device" entries in Device Manager disappears; that is expected.
If keys do nothing, the Log shows which interface is being read; repeat the steps for
"(Interface 1)" if needed. Leave Interface 2 alone, lighting uses it.

Trade-off: while the app is not running, the keypad types nothing.

Alternative without Zadig: in Device Manager, find the *USB Input Device* whose hardware ID
contains `VID_1532&PID_0201&MI_00`, choose *Update driver → Browse my computer → Let me pick*,
untick *Show compatible hardware*, and select *Universal Serial Bus devices → WinUsb Device*.
Windows may not offer this for a device that already has a driver.

Undo: Device Manager → Universal Serial Bus devices → the Tartarus entry → *Uninstall device*,
tick *Attempt to remove the driver for this device*, replug.

### Hook mode (no setup, best effort)

Leaves drivers alone. A keyboard hook holds back every key the keypad could have produced
(Tab, Q, W, E, R and so on, from any keyboard) until Raw Input reports which device sent it.
Keypad keys are then mapped; keys from other keyboards are replayed unchanged, a few
milliseconds late. The status line counts keypad, other-keyboard and unidentified keystrokes.
If Windows never reports the device, hook mode switches itself off and says so.

## Good to know

- Games running as administrator ignore input from non-elevated programs. Run Tartarus Studio
  as administrator for those.
- Mapped keys are injected input. Almost every game accepts it; a few anti-cheat systems may not.
- Key 15 is assumed to send V. If any key on the diagram answers to the wrong physical key, run
  Settings → Key calibration.
- Lighting commands follow the OpenRazer project's protocol notes for this device.

## Build from source

### Prerequisites

- Windows 10 or 11, x64
- The [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) or newer (the SDK, not just
  the runtime). Check with `dotnet --version`.

No Visual Studio is required. NuGet packages (Avalonia, xUnit) are downloaded automatically on
the first build.

### Build and run

From the repository root, in PowerShell or a terminal:

```
git clone https://github.com/LeBored/TartarusStudio.git
cd TartarusStudio
dotnet test
dotnet run --project src/TartarusStudio.App
```

`dotnet test` runs the unit tests for the mapping engine; `dotnet run` builds and starts the app.

### Make a standalone exe

Self-contained, single file, no .NET needed on the target PC (about 45 MB):

```
dotnet publish src/TartarusStudio.App -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o publish
```

Smaller variant that needs the .NET 8 runtime installed (about 27 MB):

```
dotnet publish src/TartarusStudio.App -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish
```

Either way the result is `publish\TartarusStudio.exe`.

The project also opens in Visual Studio 2022 or JetBrains Rider through `TartarusStudio.sln`.
The UI builds and starts on Linux and macOS too (useful for UI work), but the input engine and
lighting are Windows-only.

### Layout

- `src/TartarusStudio.Core` – key tables, profiles, mapping engine, macro player, HID report
  parsing, Razer LED protocol. Portable and unit-tested.
- `src/TartarusStudio.App` – Avalonia UI, SendInput output, the WinUSB and hook input backends,
  lighting control.
- `tests/TartarusStudio.Core.Tests` – xUnit tests for the core.
