# Aeroscope Desktop

A Windows desktop app for the **Aeroscope 100**, a pocket Bluetooth LE oscilloscope. It connects to the scope directly over Bluetooth, with no dongle or phone needed.

## Download

Get `Aeroscope-x.y.z.exe` from the [releases](https://github.com/mjeanrichard/AeroscopeDesktop/releases). It is a single file with .NET built in: copy it anywhere and run it, no installer needed.

- Windows 10 (version 2004 or later) or Windows 11, 64-bit, with Bluetooth LE.
- The exe isn't signed, so Windows SmartScreen may warn on the first run: choose *More info* → *Run anyway*.
- Settings are stored in `%LocalAppData%\Aeroscope\preferences.json`.

## Features

- **Connection**: scans for scopes, and reconnects automatically to the last one when it is in range.
- **Acquisition**: run, stop and single shot, also from the scope's button. Time base from 500 ns/div, plus roll mode from 500 ms/div. Frames of 512 to 4096 samples. Edge trigger (rising, falling or both) with auto trigger.
- **Screen**:
  - drag the ground marker to move the offset, and the trigger line to set the level;
  - zoom in down to 50 ns/div (Ctrl+wheel);
  - click to place cursors that show the measured value;
  - measurements over the whole frame.
- **History**: everything received while running is kept, so you can scroll back through it with the mouse wheel.
- **Calibration**: zero offset calibration. The app also remembers it, in case the scope loses its own.
- **Power saving**:
  - a deep sleep button, plus an option to put the scope to sleep when disconnecting or closing the app;
  - after 5 minutes stopped, the scope's FPGA turns off; it turns on again when you press Run.
- **Diagnostics**: the scope's telemetry and a log of the Bluetooth traffic.

The scope accepts only one connection at a time. Close the app (or disconnect) before connecting with your phone.

## Building

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download) on Windows.

```
dotnet build
dotnet test
dotnet run --project src/Aeroscope.App
dotnet publish src/Aeroscope.App -p:PublishProfile=SelfContained   # publish/Aeroscope.App.exe
```

The hardware tests are skipped unless you set `AEROSCOPE_ADDRESS` to your scope's Bluetooth address, e.g. `C6:A7:C8:D1:1A:B1`.

| Folder | Contents |
|---|---|
| `src/Aeroscope.Core` | Bluetooth connection and the scope's protocol |
| `src/Aeroscope.App` | The WPF app |
| `src/Aeroscope.Probe` | A console tool to send commands to the scope and record and decode its traffic; run it without arguments for help |
| `tests/Aeroscope.Tests` | Unit tests, many based on traffic recorded from a real scope |
| `docs` | The protocol specification and the original Android app by Aeroscope Labs, for reference: git submodules of [aeroscope/AeroscopeProtocol](https://github.com/aeroscope/AeroscopeProtocol) and [aeroscope/Aeroscope-Android](https://github.com/aeroscope/Aeroscope-Android), fetched with `git submodule update --init` |

[AGENTS.md](AGENTS.md) has more detail on the code and the protocol behaviour verified on the hardware.

## Releases

Commit messages follow [Conventional Commits](https://www.conventionalcommits.org/). To release, run the **Release** workflow by hand from the Actions tab. It tests the code and picks the next version from the commits since the last release:

| Commits include | Version bump |
|---|---|
| a breaking change (`type!:` or a `BREAKING CHANGE:` footer) | major |
| `feat:` | minor |
| `fix:` or `perf:` | patch |

It then tags the commit `vX.Y.Z` and publishes the exe as a GitHub release. Any other commit types don't produce a release.

Each exe comes with a signed build provenance attestation; see [SECURITY.md](SECURITY.md) for how to verify a download, and how to report a vulnerability.

## License

MIT, see [LICENSE](LICENSE). The released exe also contains the .NET runtime and CommunityToolkit.Mvvm; their notices are in [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt), which comes with each release.

The Aeroscope 100, its protocol specification and the original Android app belong to Aeroscope Labs. This project isn't affiliated with them.
