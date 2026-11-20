using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using TERMinator.Linux.Models;

namespace TERMinator.Linux.Services;

/// <summary>
/// Import and export of the phonebook, in the same shape the macOS client writes.
///
/// A .zip holds <c>connections.xml</c> plus a <c>snapshots/</c> folder of PNGs named after each
/// connection id; a bare .xml is the same document with the snapshots inlined as base64. Both
/// are accepted on import, so a phonebook moves between the two clients.
///
/// ⚠️ Passwords are NEVER exported. They live in the Windows credential store, not in the
/// phonebook, and an export is a file the user is likely to mail to themselves. The macOS
/// client omits them for the same reason — do not "complete" the format by adding them.
///
/// ⚠️ The font is the one field that does NOT interchange. Windows stores a font NAME while
/// macOS stores a conio font INDEX, so this writes <c>fontName</c> as an extension tag and a
/// file from macOS falls back to the default font. Everything else round-trips both ways.
/// </summary>
public static class PhonebookImportExportService
{
    private const string XmlFileName = "connections.xml";
    private const string SnapshotFolder = "snapshots";

    // ================================================================
    // Export
    // ================================================================

    /// <summary>
    /// Write every saved connection to <paramref name="destination"/>. A .zip destination gets
    /// the archive layout, anything else gets the single-file XML. Returns how many connections
    /// were written.
    /// </summary>
    public static int Export(PhonebookService phonebook, string destination)
    {
        var entries = phonebook.Connections.ToList();
        if (entries.Count == 0)
            return 0;

        bool asZip = destination.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);
        if (asZip)
            ExportZip(entries, destination);
        else
            File.WriteAllText(destination, BuildXml(entries, inlineSnapshots: true), new UTF8Encoding(false));

        return entries.Count;
    }

    private static void ExportZip(List<SavedConnection> entries, string destination)
    {
        // Build in a temp folder and move into place, so a failure part-way through cannot
        // leave a half-written archive sitting where the user asked for their backup.
        string staging = Path.Combine(Path.GetTempPath(), "TERMinator_export_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        string tempZip = destination + ".tmp";
        try
        {
            File.WriteAllText(Path.Combine(staging, XmlFileName),
                              BuildXml(entries, inlineSnapshots: false), new UTF8Encoding(false));

            string shots = Path.Combine(staging, SnapshotFolder);
            Directory.CreateDirectory(shots);
            foreach (var e in entries)
            {
                if (string.IsNullOrEmpty(e.SnapshotPath) || !File.Exists(e.SnapshotPath))
                    continue;
                File.Copy(e.SnapshotPath, Path.Combine(shots, e.Id + ".png"), overwrite: true);
            }

            if (File.Exists(tempZip)) File.Delete(tempZip);
            ZipFile.CreateFromDirectory(staging, tempZip);
            if (File.Exists(destination)) File.Delete(destination);
            File.Move(tempZip, destination);
        }
        finally
        {
            TryDeleteDirectory(staging);
            if (File.Exists(tempZip)) TryDelete(tempZip);
        }
    }

    private static string BuildXml(List<SavedConnection> entries, bool inlineSnapshots)
    {
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n");
        sb.Append("<connections>\n");

        foreach (var e in entries)
        {
            sb.Append("  <connection>\n");
            Tag(sb, "id", e.Id);
            Tag(sb, "name", e.Name);
            Tag(sb, "host", e.Host);
            Tag(sb, "port", e.Port.ToString());
            // The NATIVE connection type, which is what macOS stores too — the C# enum's own
            // ordinal is a different numbering and would import as the wrong protocol.
            Tag(sb, "protocol", e.GetNativeProtocol().ToString());
            Tag(sb, "username", e.Username ?? "");
            Tag(sb, "screenMode", e.ScreenMode.ToString());
            // macOS writes this too, so the flag survives a round trip between clients.
            Tag(sb, "showStatusBar", e.ShowStatusBar ? "true" : "false");
            if (!string.IsNullOrEmpty(e.FontName))
                Tag(sb, "fontName", e.FontName);

            if (inlineSnapshots
                && !string.IsNullOrEmpty(e.SnapshotPath)
                && File.Exists(e.SnapshotPath))
            {
                try
                {
                    Tag(sb, "snapshot", Convert.ToBase64String(File.ReadAllBytes(e.SnapshotPath)));
                }
                catch (IOException)
                {
                    // A missing or locked snapshot is not worth failing the whole export over.
                }
            }

            sb.Append("  </connection>\n");
        }

        sb.Append("</connections>\n");
        return sb.ToString();
    }

    private static void Tag(StringBuilder sb, string name, string value) =>
        sb.Append("    <").Append(name).Append('>')
          .Append(Escape(value))
          .Append("</").Append(name).Append(">\n");

    private static string Escape(string s) => s
        .Replace("&", "&amp;")
        .Replace("<", "&lt;")
        .Replace(">", "&gt;")
        .Replace("\"", "&quot;")
        .Replace("'", "&apos;");

    // ================================================================
    // Import
    // ================================================================

    /// <summary>
    /// Read connections from a .zip or .xml at <paramref name="source"/> and add them to the
    /// phonebook. With <paramref name="replace"/> the existing phonebook is cleared first;
    /// otherwise the imported entries are appended and any whose id already exists is updated
    /// in place, so re-importing the same file twice does not duplicate the list.
    /// Returns how many connections were imported.
    /// </summary>
    public static int Import(PhonebookService phonebook, string source, bool replace)
    {
        var parsed = source.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
            ? ParseZip(source)
            : ParseXml(File.ReadAllText(source), snapshotBytes: null);

        if (parsed.Count == 0)
            return 0;

        if (replace)
        {
            foreach (var existing in phonebook.Connections.Select(c => c.Id).ToList())
                phonebook.Remove(existing);
        }

        foreach (var entry in parsed)
        {
            if (!replace && phonebook.GetById(entry.Id) != null)
                phonebook.Update(entry);
            else
                phonebook.Add(entry);
        }

        return parsed.Count;
    }

    private static List<SavedConnection> ParseZip(string source)
    {
        using var archive = ZipFile.OpenRead(source);

        // The macOS export zips the staging FOLDER, so entries can be either "connections.xml"
        // or "<something>/connections.xml". Match on the file name rather than the full path.
        var xmlEntry = archive.Entries.FirstOrDefault(
            e => string.Equals(Path.GetFileName(e.FullName), XmlFileName, StringComparison.OrdinalIgnoreCase));
        if (xmlEntry == null)
            return new List<SavedConnection>();

        string xml;
        using (var reader = new StreamReader(xmlEntry.Open(), Encoding.UTF8))
            xml = reader.ReadToEnd();

        // Pull the snapshot PNGs out by connection id so ParseXml can write them to disk.
        var shots = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in archive.Entries)
        {
            if (!e.FullName.Replace('\\', '/').Contains(SnapshotFolder + "/", StringComparison.OrdinalIgnoreCase))
                continue;
            if (!e.FullName.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                continue;
            try
            {
                using var ms = new MemoryStream();
                using (var s = e.Open()) s.CopyTo(ms);
                shots[Path.GetFileNameWithoutExtension(e.FullName)] = ms.ToArray();
            }
            catch (InvalidDataException)
            {
                // Skip a corrupt member rather than abandoning the whole import.
            }
        }

        return ParseXml(xml, shots);
    }

    private static List<SavedConnection> ParseXml(string xml, Dictionary<string, byte[]>? snapshotBytes)
    {
        var result = new List<SavedConnection>();

        XDocument doc;
        try
        {
            doc = XDocument.Parse(xml);
        }
        catch (System.Xml.XmlException)
        {
            return result;
        }

        foreach (var node in doc.Descendants("connection"))
        {
            string name = Value(node, "name");
            string host = Value(node, "host");
            if (string.IsNullOrWhiteSpace(host))
                continue;                   // a connection with nowhere to go is not a connection

            string id = Value(node, "id");
            if (string.IsNullOrWhiteSpace(id))
                id = Guid.NewGuid().ToString();

            var conn = new SavedConnection
            {
                Id = id,
                Name = string.IsNullOrWhiteSpace(name) ? host : name,
                Host = host,
                Port = int.TryParse(Value(node, "port"), out int port) && port > 0 ? port : 23,
                Protocol = FromNativeProtocol(Value(node, "protocol")),
                Username = Value(node, "username"),
                ScreenMode = int.TryParse(Value(node, "screenMode"), out int mode) ? mode : 0,
                ShowStatusBar = string.Equals(Value(node, "showStatusBar"), "true",
                                              StringComparison.OrdinalIgnoreCase),
                FontName = Value(node, "fontName") is { Length: > 0 } f ? f : null
            };

            byte[]? png = null;
            if (snapshotBytes != null && snapshotBytes.TryGetValue(conn.Id, out var fromZip))
            {
                png = fromZip;
            }
            else
            {
                string b64 = Value(node, "snapshot");
                if (b64.Length > 0)
                {
                    try { png = Convert.FromBase64String(b64); }
                    catch (FormatException) { }
                }
            }

            if (png is { Length: > 0 })
                conn.SnapshotPath = WriteSnapshot(conn.Id, png);

            result.Add(conn);
        }

        return result;
    }

    private static string Value(XElement parent, string tag) =>
        parent.Element(tag)?.Value.Trim() ?? "";

    /// <summary>
    /// Native connection type back to the C# enum. The export writes the native number so the
    /// file interchanges with macOS; the enum's own ordinals are a different numbering.
    /// </summary>
    private static ConnectionProtocol FromNativeProtocol(string raw)
    {
        if (!int.TryParse(raw, out int native))
            return ConnectionProtocol.Telnet;

        if (native == NativeBridge.CONN_TYPE_RAW) return ConnectionProtocol.Raw;
        if (native == NativeBridge.CONN_TYPE_SSH) return ConnectionProtocol.SSH;
        if (native == NativeBridge.CONN_TYPE_TELNETS) return ConnectionProtocol.TelnetS;
        if (native == NativeBridge.CONN_TYPE_WEBSOCKET) return ConnectionProtocol.WebSocket;
        if (native == NativeBridge.CONN_TYPE_WEBSOCKETS) return ConnectionProtocol.SecureWebSocket;
        return ConnectionProtocol.Telnet;
    }

    private static string? WriteSnapshot(string id, byte[] png)
    {
        try
        {
            // Same naming SnapshotService.CaptureAndSave uses, so an imported snapshot is
            // overwritten in place the next time the user grabs a fresh one.
            string path = Path.Combine(SnapshotService.GetSnapshotsDir(), $"snapshot_{id}.png");
            File.WriteAllBytes(path, png);
            return path;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
