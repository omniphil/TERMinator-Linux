using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Media.Imaging;
using TERMinator.Linux.Controls;
using TERMinator.Linux.Models;

namespace TERMinator.Linux.Services;

/// <summary>
/// Captures a snapshot of the live BBS terminal canvas and saves it as the
/// thumbnail for the connection in the phonebook. Mirrors the Android
/// captureTerminalSnapshot() flow.
/// </summary>
public class SnapshotService
{
    private const int MaxThumbnailWidth = 800;

    private readonly PhonebookService _phonebook;

    public SnapshotService(PhonebookService phonebook)
    {
        _phonebook = phonebook;
    }

    public static string GetSnapshotsDir()
    {
        var dir = Path.Combine(AppSettings.GetAppDataDir(), "snapshots");
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>
    /// Capture the canvas, write a PNG under AppData, and update the
    /// connection's SnapshotPath. Returns the saved file path on success.
    /// </summary>
    public string? CaptureAndSave(TerminalCanvas canvas, SavedConnection connection)
    {
        var bitmap = canvas.CaptureSnapshot();
        if (bitmap == null) return null;

        Bitmap? decoded = null;
        Bitmap? scaled = null;
        try
        {
            Bitmap source = bitmap;
            if (source.PixelSize.Width > MaxThumbnailWidth)
            {
                double scale = (double)MaxThumbnailWidth / source.PixelSize.Width;
                int newW = MaxThumbnailWidth;
                int newH = Math.Max(1, (int)Math.Round(source.PixelSize.Height * scale));
                // CreateScaledBitmap only takes a decoded bitmap: on the RenderTargetBitmap the capture
                // returns, Skia throws "Invalid source bitmap type". Round-trip through PNG first.
                using (var ms = new MemoryStream())
                {
                    source.Save(ms);
                    ms.Position = 0;
                    decoded = new Bitmap(ms);
                }
                scaled = decoded.CreateScaledBitmap(new PixelSize(newW, newH));
                source = scaled;
            }

            // Mirror TERMinator-Android (TerminalActivity.updateConnectionThumbnail): the picture
            // belongs to the BOARD, so it lands on the first phonebook entry with this host+port
            // rather than on whichever entry object we happened to connect from.
            var target = _phonebook.Connections.FirstOrDefault(
                c => string.Equals(c.Host, connection.Host, StringComparison.OrdinalIgnoreCase)
                     && c.Port == connection.Port) ?? connection;

            var filePath = Path.Combine(GetSnapshotsDir(), $"snapshot_{target.Id}.png");
            source.Save(filePath);

            target.SnapshotPath = filePath;
            _phonebook.Update(target);

            return filePath;
        }
        finally
        {
            scaled?.Dispose();
            decoded?.Dispose();
            bitmap.Dispose();
        }
    }
}
