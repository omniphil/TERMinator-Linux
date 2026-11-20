using System.IO;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Threading;
using TERMinator.Linux.Models;
using TERMinator.Linux.Services;
using TERMinator.Linux.Views;

namespace TERMinator.Linux;

public partial class App : Application
{
    // Singleton services accessible throughout the application
    public static ConnectionManager ConnectionManager { get; private set; } = null!;
    public static PhonebookService PhonebookService { get; private set; } = null!;
    public static CredentialService CredentialService { get; private set; } = null!;
    public static SettingsService SettingsService { get; private set; } = null!;
    public static AudioService AudioService { get; private set; } = null!;
    public static BellService BellService { get; private set; } = null!;
    public static SessionLogger SessionLogger { get; private set; } = null!;
    public static SnapshotService SnapshotService { get; private set; } = null!;
    public static CallStatsService CallStatsService { get; private set; } = null!;
    public static KeyClickService KeyClickService { get; private set; } = null!;

    /// <summary>Set while the Boss Hide spreadsheet covers the window; bells stay quiet meanwhile.</summary>
    public static bool BossKeyActive { get; set; }

    /// <summary>
    /// Each BBS gets a window, and a process, of its own: the terminal engine (libsyncterm-native.so) holds exactly one
    /// session per process. The main window starts one with <c>--session &lt;connection&gt;</c> (MainWindow.LaunchSession)
    /// and this is that connection; null in the main window.
    /// </summary>
    public static SavedConnection? SessionConnection { get; private set; }

    /// <summary>The main window's process, for a session: it signals its sessions to close when it's closed.</summary>
    public static int ParentProcessId { get; private set; }

    /// <summary>Where a session opens when its board has no remembered window: left, top, right, bottom in the
    /// screen's physical pixels (the same shape as AppSettings.WindowBounds).</summary>
    public static int[]? SessionCascade { get; private set; }

    /// <summary>A session that opens without taking the focus (--focus 0: a board's linked window sent focus=0).</summary>
    public static bool SessionNoActivate { get; private set; }

    /// <summary>
    /// The arguments the main window starts a session with:
    ///   --session &lt;base64 JSON of the SavedConnection&gt;   (the whole entry, so a board typed in the command line works too)
    ///   --parent &lt;pid&gt;
    ///   --cascade &lt;left&gt;,&lt;top&gt;,&lt;right&gt;,&lt;bottom&gt;
    ///   --focus 0                                         (open without activating; 12.73 linked windows)
    /// </summary>
    private static void ReadArguments(string[] args)
    {
        for (int i = 0; i + 1 < args.Length; i++)
        {
            try
            {
                switch (args[i])
                {
                    case "--session":
                        SessionConnection = JsonSerializer.Deserialize<SavedConnection>(
                            Encoding.UTF8.GetString(Convert.FromBase64String(args[++i])));
                        break;
                    case "--parent":
                        ParentProcessId = int.Parse(args[++i]);
                        break;
                    case "--focus":
                        SessionNoActivate = args[++i] == "0";
                        break;
                    case "--cascade":
                        var r = args[++i].Split(',').Select(int.Parse).ToArray();
                        if (r.Length == 4)
                            SessionCascade = r;
                        break;
                }
            }
            catch
            {
                // a malformed argument is ignored: without --session this is simply the main window
            }
        }
    }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            ReadArguments(desktop.Args ?? []);

            string? pendingErrorTitle = null;
            string? pendingErrorMessage = null;
            bool pendingFatal = false;

            try
            {
                // Initialize services
                SettingsService = new SettingsService();
                // A modem speed that's no longer listed (57600 / 115200) moves to the nearest one
                SettingsService.Settings.EmulatedBaud = ModemSpeeds.Normalize(SettingsService.Settings.EmulatedBaud);
                PhonebookService = new PhonebookService();
                CredentialService = new CredentialService();
                AudioService = new AudioService(SettingsService);
                BellService = new BellService();
                SessionLogger = new SessionLogger();
                ConnectionManager = new ConnectionManager();
                SnapshotService = new SnapshotService(PhonebookService);
                CallStatsService = new CallStatsService();
                KeyClickService = new KeyClickService(SettingsService);

                // Initialize the audio bridge (OpenAL Soft)
                if (NativeBridge.audio_bridge_init() == 0)
                {
                    Console.Error.WriteLine(
                        "Warning: audio_bridge_init() failed. Audio will be unavailable.\n" +
                        "Ensure libopenal-dev (OpenAL Soft) is installed.");
                }
                else
                {
                    AudioService.AudioBridgeReady = true;
                    // No BellService.WarmUp here (Windows has one): the bridge opens the OpenAL device on the first
                    // sound, and warming the bell channel would open it at launch -- under WSLg that made an audible
                    // sound. Key-click clips are only parsed and held natively until then.
                    if (SettingsService.Settings.KeyClicksEnabled)
                        KeyClickService.WarmUp(); // upload the click clips before the first keystroke
                }

                // Initialize the native bridge
                if (!ConnectionManager.Initialize())
                {
                    pendingErrorTitle = "TERMinator - Initialization Error";
                    pendingErrorMessage =
                        "Failed to initialize the native terminal engine.\n\n" +
                        "syncterm_init() returned failure.\n" +
                        $"Library search path: {AppContext.BaseDirectory}";
                    pendingFatal = true;
                }
            }
            catch (DllNotFoundException ex)
            {
                pendingErrorTitle = "TERMinator - Missing Native Library";
                pendingErrorMessage =
                    $"Could not load native library:\n\n{ex.Message}\n\n" +
                    $"Looked in: {AppContext.BaseDirectory}\n" +
                    $"Library exists: {File.Exists(Path.Combine(AppContext.BaseDirectory, "libsyncterm-native.so"))}";
                pendingFatal = true;
            }
            catch (BadImageFormatException ex)
            {
                pendingErrorTitle = "TERMinator - Library Error";
                pendingErrorMessage =
                    $"Native library architecture mismatch:\n\n{ex.Message}\n\n" +
                    $"Ensure the library is built for this CPU ({System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}).";
                pendingFatal = true;
            }
            catch (Exception ex)
            {
                pendingErrorTitle = "TERMinator - Startup Error";
                pendingErrorMessage = $"Startup error:\n\n{ex}";
                pendingFatal = true;
            }

            desktop.MainWindow = new MainWindow();

            // Surface any startup error in a dialog after MainWindow is up.
            // For fatal errors, exit after the user acknowledges.
            if (pendingErrorTitle != null && pendingErrorMessage != null)
            {
                Console.Error.WriteLine($"[{pendingErrorTitle}] {pendingErrorMessage}");
                var title = pendingErrorTitle;
                var msg = pendingErrorMessage;
                var fatal = pendingFatal;
                Dispatcher.UIThread.Post(async () =>
                {
                    await ShowStartupErrorDialog(desktop.MainWindow!, title, msg);
                    if (fatal) desktop.Shutdown(1);
                });
            }

            desktop.ShutdownRequested += (_, _) =>
            {
                // Dispose services in reverse order
                SessionLogger?.Dispose();
                BellService?.Dispose();
                AudioService?.Dispose();
                ConnectionManager?.Dispose();

                // Shutdown audio bridge (OpenAL Soft)
                try { NativeBridge.audio_bridge_shutdown(); }
                catch { /* ignore if native lib not loaded */ }
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static Task ShowStartupErrorDialog(Window owner, string title, string message)
    {
        var dlg = new Window
        {
            Title = title,
            Width = 520,
            Height = 320,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            ShowInTaskbar = false,
        };

        var ok = new Button
        {
            Content = "OK",
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0),
            Padding = new Thickness(20, 4, 20, 4),
            IsDefault = true,
        };
        ok.Click += (_, _) => dlg.Close();

        var content = new TextBox
        {
            Text = message,
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Stretch,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
        };

        var panel = new DockPanel { Margin = new Thickness(16) };
        DockPanel.SetDock(ok, Dock.Bottom);
        panel.Children.Add(ok);
        panel.Children.Add(content);

        dlg.Content = panel;
        return dlg.ShowDialog(owner);
    }
}
