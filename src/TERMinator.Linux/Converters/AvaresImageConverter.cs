using System.Globalization;
using System.IO;
using Avalonia.Data.Converters;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace TERMinator.Linux.Converters;

/// <summary>
/// Converts an image source string into a Bitmap for Image.Source bindings.
/// Accepts avares:// URIs (bundled assets) and absolute filesystem paths
/// (user-captured snapshots stored under AppData).
/// </summary>
public class AvaresImageConverter : IValueConverter
{
    /// <summary>Shown when a board has no picture, or its snapshot file has gone missing behind the app's back.</summary>
    private const string NoSnapshot = "avares://TERMinator/Assets/snapshots/no_snapshot.png";

    public static readonly AvaresImageConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        string? path = value as string;

        // A snapshot whose file has been deleted falls back to the bundled placeholder rather than a blank tile,
        // the same way the Windows client's SnapshotPathToImageConverter does.
        if (string.IsNullOrEmpty(path)
            || (!path.StartsWith("avares://", StringComparison.OrdinalIgnoreCase) && !File.Exists(path)))
            path = NoSnapshot;

        try
        {
            if (path.StartsWith("avares://", StringComparison.OrdinalIgnoreCase))
            {
                using var stream = AssetLoader.Open(new Uri(path));
                return new Bitmap(stream);
            }

            return new Bitmap(path);
        }
        catch
        {
            return null;
        }
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
