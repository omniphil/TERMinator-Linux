namespace TERMinator.Linux.Services;

/// <summary>
/// Single source of truth for converting TAP+ terminal cell coordinates
/// to pixel positions. All TAP+ overlays (image, video, logo, visualizer)
/// use this mapper so coordinates are consistent across the app.
///
/// All cell coordinates are 1-based (row 1, col 1 = top-left cell).
/// Supports fractional (double) values for sub-cell precision.
/// </summary>
public static class TapCoordinateMapper
{
    /// <summary>
    /// Pixel-space rectangle: top-left corner + size.
    /// </summary>
    public record PixelRect(double Left, double Top, double Width, double Height);

    /// <summary>
    /// Convert 1-based cell coordinates to a pixel rectangle.
    /// </summary>
    /// <param name="row">1-based row (top-left of the region)</param>
    /// <param name="col">1-based column (top-left of the region)</param>
    /// <param name="cellW">Width in cells</param>
    /// <param name="cellH">Height in cells</param>
    /// <param name="cellPixelWidth">Pixel width of one cell</param>
    /// <param name="cellPixelHeight">Pixel height of one cell</param>
    public static PixelRect CellsToPixels(
        double row, double col,
        double cellW, double cellH,
        double cellPixelWidth, double cellPixelHeight)
    {
        double left = (col - 1.0) * cellPixelWidth;
        double top = (row - 1.0) * cellPixelHeight;
        double width = cellW * cellPixelWidth;
        double height = cellH * cellPixelHeight;
        return new PixelRect(left, top, width, height);
    }
}
