# Aeroscope desktop app

C# / WPF (.NET 10) desktop client for the Aeroscope 100 Bluetooth LE oscilloscope.
Target `net10.0-windows10.0.19041.0` everywhere (WinRT Bluetooth API).

## Layout

- `src/Aeroscope.Core`: BLE adapters (`Bluetooth/`), protocol encode/decode (`Protocol/`), capture files (`Diagnostics/`). No UI.
- `src/Aeroscope.App`: WPF app, MVVM with CommunityToolkit.Mvvm (partial-property `[ObservableProperty]`). `MainViewModel` owns the connection; `AcquisitionViewModel` owns settings, run control and the waveform; `Controls/WaveformView` draws the screen.
- `src/Aeroscope.Probe`: console tool to drive the scope and record/decode captures. Run without args for usage.
- `tests/Aeroscope.Tests`: xUnit tests for all projects; fixtures in `Captures/` were recorded from the real scope.
- `docs/`: reference only. Original Qt/Android app and `AeroscopeProtocol/Aeroscope_BTLE_Protocol_Specification.pdf` (read it with `pdftotext -layout`).

## Commands

```
dotnet build
dotnet test                                              # hardware tests skipped
AEROSCOPE_ADDRESS=C6:A7:C8:D1:1A:B1 dotnet test          # also runs against the real scope
dotnet run --project src/Aeroscope.Probe -- scan
dotnet run --project src/Aeroscope.Probe -- session --out cap.txt QP until:PF settings R frames:5 S X
dotnet run --project src/Aeroscope.Probe -- decode cap.txt --plot
```

The scope is in range of this PC's Bluetooth adapter. It accepts one connection at a time, so close the WPF app before using the probe or hardware tests.

## Conventions

- Test everything. Base protocol tests on real captures: record with the probe, save to `tests/Aeroscope.Tests/Captures/`, describe the file in `Captures.cs`. Use synthetic packets only for what the hardware can't produce, and say so in the test.
- Code that needs a live connection goes through `IScopeLink` / `IScopeScanner`, so tests can use the fakes in `Fakes.cs`.
- BLE events arrive on thread-pool threads. The view model marshals them through its injected `post` action.
- Projects using `UseWPF` don't get `System.IO` as an implicit using; add it explicitly.
- Format numbers in logs and capture files with the invariant culture. Tests that check formatted output use `[UseInvariantCulture]`.

## Protocol facts (verified on firmware rev 5)

- Service `F9541234-91B3-BD9A-F077-80F2A6E57D00`. Characteristics use the Bluetooth base UUID: Data `0x1235` (notify), In `0x1236` (write), State `0x1237` (write), Out `0x1239` (notify).
- In/State support write-with-response only. All packets are 20 bytes, zero padded.
- The scope sends `PF` (FPGA ready) by itself after connecting. Don't start acquisition before it.
- Frames: the first packet is `[size code][subtrigger][18 samples]`, the following ones are `[0x00][19 samples]`. A 512-sample frame is exactly 27 packets.
- State register block = `ScopeRegisters.ToPacket()` (Android app defaults). `R` then streams about 4 frames/s; `S` + `X` stops and cancels an in-flight frame.
- Read depth 9 (with read start 0) makes `R` stream full 4096-sample frames (216 packets, about 0.7 s each). Use this instead of `L`.
- The 512-sample read window starts at the read-start register (default 0x700) within the 4096-sample memory.
- Scaling (`ScopeSettings`, `Waveform`): 8 divisions span ADC codes 0-255; ranges and DAC factors from the Android app hold (checked at 1 V and 100 mV/div). Raising the offset DAC moves the trace down, so the offset is the voltage at the screen centre. t = 0 (trigger) is sample 256 of a 512 frame; a real rising edge crosses there.
- Settings written while running apply from the next frame (one stale frame follows each write).
- Unconnected input reads about +0.36 V (1 V/div) / +0.26 V (100 mV/div): uncalibrated zero error.
- Roll mode: sampler codes 0xE7/0xEF/0xF7/0xFF (500 ms-5 s/div). `R` streams one-packet 16-sample frames (size code 1) back to back in real time, whatever the trigger settings. After a roll `S` + `X`, the next `R` first delivers 2-3 leftover 16-sample frames at once (the last one partly zeros when leaving roll mode); the app drops frames whose size doesn't match the time base.
- Device button sends `BD` on Scope Out (not `BP`). The app toggles run/stop, ignoring `BD`s less than 1 s apart.
- A good test signal: touching the probe tip gives 50 Hz hum (codes ~100-175 at 1 V/div). Use 5 ms/div to see whole cycles.

## Open questions

- `F` (single) and `L` (full frame) almost never return a frame, whatever the trigger mode or command order; only one capture (`07-...`) worked. The app's Single is `R`, first frame, then `S` + `X` (verified on hardware).
- `QC` answers `C` + zeros, not the spec's `CB` + seven 16-bit offsets.
- Not yet supported: timebases faster than 500 ns/div (software zoom in the Android app), 4096-sample display.
- Whether a held button repeats `BD` (every 0.3-0.5 s in capture 11) or those were separate presses is unverified.
- Whether the scope re-sends `PF` after an automatic reconnect (the app relies on it to resume) is unverified.
