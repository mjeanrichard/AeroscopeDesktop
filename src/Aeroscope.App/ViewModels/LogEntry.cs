namespace Aeroscope.App.ViewModels;

public enum LogKind { Info, Error, Out, Data, In, State }

/// <summary>One line in the traffic log. Packets show as hex plus printable ASCII.</summary>
public sealed record LogEntry(DateTime Time, LogKind Kind, string Text)
{
    public string TimeText => Time.ToString("HH:mm:ss.fff");

    public string KindText => Kind.ToString().ToUpperInvariant();

    public static LogEntry Message(LogKind kind, string text) => new(DateTime.Now, kind, text);

    public static LogEntry Packet(LogKind kind, byte[] bytes)
    {
        var hex = BitConverter.ToString(bytes).Replace('-', ' ');
        var ascii = new string(bytes.Select(b => b is >= 0x20 and < 0x7F ? (char)b : '.').ToArray());
        return new(DateTime.Now, kind, $"{hex}  |{ascii}|");
    }
}
