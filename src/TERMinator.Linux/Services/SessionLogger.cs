using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace TERMinator.Linux.Services;

/// <summary>
/// Logs terminal session data to a file, with ANSI escape code stripping.
/// </summary>
public partial class SessionLogger : IDisposable
{
    private StreamWriter? _writer;
    private bool _enabled;
    private readonly byte[] _logBuffer = new byte[64 * 1024];

    public bool IsEnabled => _enabled;

    public void Start(string filePath)
    {
        Stop();
        var dir = Path.GetDirectoryName(filePath);
        if (dir != null) Directory.CreateDirectory(dir);
        _writer = new StreamWriter(filePath, append: true, Encoding.UTF8);
        _writer.AutoFlush = true;
        try { NativeBridge.syncterm_set_logging_enabled(1); }
        catch (DllNotFoundException) { }
        _enabled = true;
    }

    public void Stop()
    {
        if (!_enabled) return;
        _enabled = false;
        try { NativeBridge.syncterm_set_logging_enabled(0); }
        catch (DllNotFoundException) { }
        Flush();
        _writer?.Dispose();
        _writer = null;
    }

    public void Flush()
    {
        if (_writer == null) return;

        int len;
        try { len = NativeBridge.syncterm_get_logged_data(_logBuffer, _logBuffer.Length); }
        catch (DllNotFoundException) { return; }
        if (len <= 0) return;

        var raw = Encoding.Latin1.GetString(_logBuffer, 0, len);
        var stripped = StripAnsiEscapes(raw);
        _writer.Write(stripped);
    }

    private static string StripAnsiEscapes(string input)
    {
        return AnsiEscapeRegex().Replace(input, string.Empty);
    }

    [GeneratedRegex(@"\x1b\[[0-9;]*[A-Za-z]|\x1b\][^\x07]*\x07|\x1b[()][0-9A-B]")]
    private static partial Regex AnsiEscapeRegex();

    public void Dispose()
    {
        Stop();
        GC.SuppressFinalize(this);
    }
}
