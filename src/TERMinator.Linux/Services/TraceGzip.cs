using System.IO;
using System.IO.Compression;

namespace TERMinator.Linux.Services;

/// <summary>
/// Gzip uploads (TRACE Put ... ;enc=gz, offered with gz=1 in the Query reply): a door that sees gz=1 may send a module
/// or asset gzip-compressed. The Put offsets then count the compressed stream; the Open/Asset declaration keeps the
/// real (decompressed) size and SHA-256, so the cache and its checks are unchanged. A fonts pack of 596 KB goes as
/// 231 KB. Kept apart from TraceSessionManager so tools/TraceGzipTest can test it on its own.
/// </summary>
public static class TraceGzip
{
    /// <summary>
    /// The most a gzip stream for a file of <paramref name="size"/> bytes may be: gzip of data that won't compress
    /// is a little bigger than the data (5 bytes per 64 KB stored block, 18 of header and trailer).
    /// </summary>
    public static long MaxCompressed(int size) => (long)size + size / 64 + 1024;

    /// <summary>
    /// The file a gzip upload carries: exactly <paramref name="size"/> bytes (the size the door declared, already
    /// held to MaxModuleBytes / MaxAssetBytes), or null when the stream is broken, comes out shorter, or would come
    /// out longer. Inflating stops one byte past the declared size, so a zip bomb never gets further than that.
    /// </summary>
    public static byte[]? Gunzip(byte[] gz, int size)
    {
        if (size <= 0)
            return null;
        try
        {
            using var input = new GZipStream(new MemoryStream(gz, writable: false), CompressionMode.Decompress);
            var output = new byte[size];
            int got = 0, n;
            while (got < size && (n = input.Read(output, got, size - got)) > 0)
                got += n;
            if (got != size || input.ReadByte() >= 0)
                return null;
            return output;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or NotSupportedException)
        {
            return null;
        }
    }
}
