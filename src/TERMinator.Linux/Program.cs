using Avalonia;
using Avalonia.X11;

namespace TERMinator.Linux;

public class Program
{
    [STAThread]
    public static void Main(string[] args) => BuildAvaloniaApp()
        .StartWithClassicDesktopLifetime(args);

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            // ⚠️ Ask for a GPU surface explicitly, GLX first, and keep Software only as the
            // last resort. Without this, platform detection picks the software rasterizer on
            // systems that can perfectly well do GL — WSLg with the Mesa d3d12 driver is one,
            // measured 2026-09-12 — and the CRT post-process is then silently unavailable,
            // because an SkSL runtime effect cannot run on Skia's CPU backend at all.
            // Software stays in the list: no GL means no CRT, never a failure to start.
            .With(new X11PlatformOptions
            {
                RenderingMode = new[]
                {
                    X11RenderingMode.Glx,
                    X11RenderingMode.Egl,
                    X11RenderingMode.Software
                }
            })
            .WithInterFont()
            .LogToTrace();
}
