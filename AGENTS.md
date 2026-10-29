# Aeroscope desktop app

C# / WPF (.NET 10) desktop client for the Aeroscope 100 Bluetooth LE oscilloscope.
Target `net10.0-windows10.0.19041.0` everywhere (WinRT Bluetooth API).

## Layout

- `src/Aeroscope.Core`: BLE adapters (`Bluetooth/`), protocol encode/decode (`Protocol/`), capture files (`Diagnostics/`). No UI.
- `src/Aeroscope.App`: WPF app, MVVM with CommunityToolkit.Mvvm (partial-property `[ObservableProperty]`). `MainViewModel` owns the connection; `AcquisitionViewModel` owns settings, run control and what the screen shows; its `History` part keeps everything received while running (`FrameHistory` for triggered frames, stepped through one at a time since they are separate snapshots; `RollHistory` segments of raw codes for roll mode, which scrolls smoothly; both in memory) for scrolling back. `Controls/WaveformView` draws the screen.
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
- State register block = `ScopeRegisters.ToPacket()` (Android app defaults). `R` then streams frames back to back; `S` + `X` stops and cancels an in-flight frame.
- Read depth 9 (with read start 0, write depth 9) makes `R` stream full 4096-sample frames (216 packets, about 0.7 s each). Use this instead of `L`.
- Write depth sets the record captured (code n = 2^(n+3) samples: 6 = 512 ... 9 = 4096), trigger position the trigger's place in it, read start the first sample sent. The Android defaults capture 4096 with the trigger at 0x800 and send 512 from 0x700. The app (`ScopeSettings.ToRegisters`) captures and sends just the frame size (512-4096, all verified, captures 23 and 24) with the trigger in the middle, read from 0. Roll mode keeps the Android layout. Frames wider than 512 are shown whole and can be zoomed and panned.
- Scaling (`ScopeSettings`, `Waveform`): 8 divisions span ADC codes 0-255; ranges and DAC factors from the Android app hold (checked at 1 V and 100 mV/div). Raising the offset DAC moves the trace down, so the offset is the voltage at the screen centre. t = 0 (trigger) is sample 256 of a 512 frame; a real rising edge crosses there.
- Settings written while running: a frame keeps the old settings if it started arriving before the write was acknowledged, or if the write was acknowledged after the previous frame and less than one connection event (~37 ms) before it (captures 16, 20; `SettingsTimeline`). Frames are recorded with the settings they were taken with. A write restarts a capture in progress.
- The scope captures the next frame after sending the previous one (or from R, or from a settings write), so a frame takes capture time (record length x sample interval) plus transfer time (27 packets per 512 samples, 0.1-0.3 s depending on the connection). With a 4096 record that is 1.65 s at 20 ms/div and 16.5 s at 200 ms/div (captures 21, 22); with 512, 0.2 s and 2.05 s (capture 23). The first packet comes 0.07 s before to 0.35 s after the capture time, whether the edge triggers at once or the auto trigger fires (captures 21-23, 25, 26); `CaptureProgress` uses this for the progress bar. Without auto trigger and an edge the scope waits indefinitely; lowering the level then gives a frame after only the post-trigger half (capture 25).
- Unconnected input reads about +0.36 V (1 V/div) / +0.26 V (100 mV/div): uncalibrated zero error.
- Roll mode: sampler codes 0xE7/0xEF/0xF7/0xFF (500 ms-5 s/div). `R` streams one-packet 16-sample frames (size code 1) back to back in real time, whatever the trigger settings. After a roll `S` + `X`, the next `R` first delivers 2-3 leftover 16-sample frames at once (the last one partly zeros when leaving roll mode): `RollHistory` drops frames under 30 ms apart at a segment start, and the app drops frames whose size doesn't match the time base. Sample times = arrival of a segment's first frame minus its duration, plus count x interval; this matches arrival times to within 30 ms (capture 17). Stamp packets on arrival, before they queue for the UI thread.
- Device button sends one `BD` on Scope Out per press (not `BP`); holding it doesn't repeat (capture 19). The app toggles run/stop on each.
- A good test signal: touching the probe tip gives 50 Hz hum (codes ~100-175 at 1 V/div). Use 5 ms/div to see whole cycles.

## Open questions

- `F` (single) and `L` (full frame) almost never return a frame, whatever the trigger mode or command order; only one capture (`07-...`) worked. The app's Single is `R`, first frame, then `S` + `X` (verified on hardware).
- `QC` answers `C` + zeros, not the spec's `CB` + seven 16-bit offsets.
- Not yet supported: timebases faster than 500 ns/div (software zoom in the Android app), saving/loading the history.
- Whether the scope re-sends `PF` after an automatic reconnect (the app relies on it to resume) is unverified.
