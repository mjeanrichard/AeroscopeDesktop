using Aeroscope.Probe;

const string Usage = """
    Aeroscope probe: talk to the scope from the command line and record its traffic.

    Usage:
      probe scan [seconds]
          List BLE devices; Aeroscopes are marked with *. Default 5 s.

      probe session [--address AA:BB:CC:DD:EE:FF] [--out capture.txt] [--raw] [step ...]
          Connect (to the first Aeroscope found if no address), run the steps in order,
          print every packet decoded, and optionally save a capture file.
          Steps:
            QTI, QP, R, ...       ASCII command to Scope In
            in:<hex>              raw bytes to Scope In
            settings[:k=v,...]    FPGA registers, Android defaults unless overridden:
                                  trig, level, fe, ts, tpos, rstart, wdepth, rdepth, dac
            state:<hex>           raw bytes to Scope State
            wait:<seconds>        pause
            until:<text>[:<s>]    wait for a Scope Out packet starting with text (default 10 s)
            frames:<n>[:<s>]      wait until n more frames have arrived (default 10 s)
          Default steps: QTI QP until:PF QVR wait:1

      probe decode <capture.txt> [--samples] [--plot]
          Replay a capture file through the decoders.
    """;

try
{
    return args switch
    {
        ["scan", .. var rest] => await ScanCommand.RunAsync(rest),
        ["session", .. var rest] => await SessionCommand.RunAsync(rest),
        ["decode", .. var rest] => DecodeCommand.Run(rest),
        _ => PrintUsage(),
    };
}
catch (Exception ex) when (ex is ArgumentException or FormatException or TimeoutException or Aeroscope.Core.Bluetooth.ScopeConnectionException)
{
    Console.Error.WriteLine($"error: {ex.Message}");
    return 1;
}

int PrintUsage()
{
    Console.WriteLine(Usage);
    return 2;
}
