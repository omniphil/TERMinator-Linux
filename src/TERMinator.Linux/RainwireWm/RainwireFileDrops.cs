using System.IO;
using Avalonia;
using Avalonia.Threading;
using TERMinator.Linux.Controls;

namespace TERMinator.Linux.RainwireWm;

/// <summary>
/// Rainwire file drops (files=1; Rainwire docs/WINDOWS.md "File drops", docs/FILES_GFX.md "Step 3d"), the
/// terminal's side, for both places files can be dropped:
///   - desk windows (checklist item 13): OS files dropped on a Rainwire window or the desktop;
///   - the Files TRACE module (module id rwfiles): OS files dropped on its picture;
///   - Files as a Rainwire scene (scenes version 5, Controls/RainwireSceneFiles.cs): files dropped on a P_DROP block
///     go the module's way, module=rwfiles with scene=&lt;block&gt; added, the block lit while they're over it.
///
/// In: one FileOffer per file; the paths are kept by n. FileGo names the ones to send: the next ZMODEM upload the
/// board starts (its ZRINIT after Suspend) sends exactly those, in that order, with no file picker. FileNo shows its
/// reason. A batch nobody answers is forgotten after 2 minutes. One batch at a time (FileGo carries no batch id).
///
/// Out (dragging a file row onto the OS) is Windows-only for now: Avalonia 11's X11 backend has no XDnD drag source,
/// so on Linux a drag that leaves the window ends as it always did and nothing is sent (no DropOut). A FileGet that
/// comes anyway gets the normal download dialog; a FileNo for a DropOut still shows its reason. A scene's P_DRAG row
/// still drags inside the window (DragOut, the board's DragFile label by the pointer), but never becomes an OS drag.
/// Ported from Windows' RainwireWm/RainwireFileDrops.cs 2026-10-09 (the OS drop events come from X11FileDropTarget).
/// </summary>
public sealed class RainwireFileDrops
{
    public const string FilesModule = "rwfiles";
    private static readonly TimeSpan BatchLife = TimeSpan.FromMinutes(2);

    private readonly WmSession _session;
    private readonly RainwireDeskLayer _desk;
    private readonly Func<string, EngineHostLayer?> _moduleLayer;
    private readonly Action<string> _toast;
    private readonly Action<string[]> _runSend;

    private sealed class Batch
    {
        public required string[] Paths;
        public readonly HashSet<int> Answered = new();
        public DateTime At = DateTime.UtcNow;
    }
    private Batch? _batch;
    private string[]? _pendingSend;
    private DateTime _pendingSendAt;

    private readonly List<(string Text, DateTime At)> _reasons = new();
    private RainwireMenu? _scene;   // Rainwire's own menus and scenes (Files as a scene)
    private readonly DispatcherTimer _tidy;

    /// <param name="session">The window protocol (its file verbs come in and out of windowed mode).</param>
    /// <param name="desk">The desk layer, for drop targets.</param>
    /// <param name="moduleLayer">The running TRACE module's layer by id, or null.</param>
    /// <param name="toast">Shows a short message (FileNo reasons).</param>
    /// <param name="runSend">Runs the ZMODEM send of these paths now (the board's ZRINIT has arrived).</param>
    public RainwireFileDrops(WmSession session, RainwireDeskLayer desk, Func<string, EngineHostLayer?> moduleLayer,
        Action<string> toast, Action<string[]> runSend)
    {
        _session = session;
        _desk = desk;
        _moduleLayer = moduleLayer;
        _toast = toast;
        _runSend = runSend;

        session.FileGo += OnFileGo;
        session.FileNo += OnFileNo;

        _tidy = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _tidy.Tick += (_, _) => Tidy();
        _tidy.Start();
    }

    /// <summary>Handles the window protocol messages queued so far (RainwireDesk.PollNow).</summary>
    public Action? DrainMessages { get; set; }

    /// <summary>Files as a scene: its drop targets take files the module's way; a dragged row's DragFile answer shows
    /// its label (no OS drag on Linux).</summary>
    public void AttachScene(RainwireMenu menu)
    {
        _scene = menu;
        _session.DragFileScene += (row, ok, label, hint) => menu.SceneDragAnswer((ushort)row, ok, label, hint);
    }

    /// <summary>The OS drag left TERMinator, or ended: the scene's drop target goes dark.</summary>
    public void DragLeft() => _scene?.SceneDropHover(0);

    /// <summary>The connection went: forget everything.</summary>
    public void Reset()
    {
        _batch = null;
        _pendingSend = null;
    }

    private void Tidy()
    {
        var now = DateTime.UtcNow;
        if (_batch != null && now - _batch.At > BatchLife)
            _batch = null;
        if (_pendingSend != null && now - _pendingSendAt > BatchLife)
            _pendingSend = null;
    }

    private void ShowReason(string reason)
    {
        if (reason.Length == 0)
            return;
        var now = DateTime.UtcNow;
        _reasons.RemoveAll(r => now - r.At > TimeSpan.FromSeconds(6));
        _reasons.Add((reason, now));
        _toast(string.Join("\n", _reasons.TakeLast(4).Select(r => r.Text)));
    }

    // ================================================================
    // In: OS files dropped on a window, the desktop or the Files module
    // ================================================================

    private enum Target { None, Module, Desk, Scene }

    /// <param name="root">The window the point is in.</param>
    /// <param name="p">The pointer, in root's coordinates (device-independent pixels).</param>
    private (Target Kind, int Id, int X, int Y) TargetAt(Visual root, Point p)
    {
        if (_scene != null && _scene.SceneDropTarget(root, p, out var at) is var sid and > 0)   // Files as a scene: a P_DROP block
            return (Target.Scene, sid, (int)at.X, (int)at.Y);
        var layer = _moduleLayer(FilesModule);
        if (layer is { IsEffectivelyVisible: true } && layer.FramePoint(root, p, out int fx, out int fy))
            return (Target.Module, 0, fx, fy);
        if (_session.Active && !_session.Suspended && _desk.IsEffectivelyVisible
            && root.TranslatePoint(p, _desk) is { } dp)
        {
            var (to, x, y) = _desk.DropTarget(dp);
            return (Target.Desk, to, x, y);
        }
        return (Target.None, 0, 0, 0);
    }

    /// <summary>Files dragged over the window: whether they can be dropped here (a scene's target lit while so).</summary>
    public bool CanTake(Visual root, Point p)
    {
        var t = TargetAt(root, p);
        _scene?.SceneDropHover(t.Kind == Target.Scene ? (ushort)t.Id : (ushort)0);
        return t.Kind != Target.None;
    }

    /// <summary>Files dropped from the OS. True when they were ours to take (offered, or refused with a reason).</summary>
    public bool Drop(Visual root, Point p, string[] dropped)
    {
        var t = TargetAt(root, p);
        _scene?.SceneDropHover(0);
        if (t.Kind == Target.None)
            return false;
        if (_batch != null && DateTime.UtcNow - _batch.At < BatchLife)
        {
            _toast("Still waiting on the board for the last files dropped");
            return true;
        }
        if (_pendingSend != null)
        {
            _toast("A transfer is waiting to start: drop the files again after it");
            return true;
        }
        var paths = new List<string>();
        int folders = 0;
        foreach (var path in dropped)
        {
            if (File.Exists(path))
                paths.Add(path);
            else if (Directory.Exists(path))
                folders++;
        }
        if (paths.Count == 0)
        {
            _toast(folders > 0 ? "Folders can't be uploaded: drop the files in them" : "Nothing to upload");
            return true;
        }
        if (folders > 0)
            _toast("Folders were left out: only files can be uploaded");
        _batch = new Batch { Paths = paths.ToArray() };
        string? module = t.Kind is Target.Module or Target.Scene ? FilesModule : null;
        int scene = t.Kind == Target.Scene ? t.Id : 0;
        for (int i = 0; i < paths.Count; i++)
        {
            long size;
            try { size = new FileInfo(paths[i]).Length; }
            catch { size = 0; }
            _session.FileOffer(i + 1, paths.Count, scene > 0 ? 0 : t.Id, module, t.X, t.Y, Path.GetFileName(paths[i]), size, scene);
        }
        return true;
    }

    private void OnFileGo(IReadOnlyList<int> ns)
    {
        var b = _batch;
        if (b == null)
            return;
        var send = new List<string>();
        foreach (int n in ns)
        {
            if (n < 1 || n > b.Paths.Length || !b.Answered.Add(n))
                continue;
            send.Add(b.Paths[n - 1]);
        }
        if (b.Answered.Count >= b.Paths.Length)
            _batch = null;
        if (send.Count == 0)
            return;
        _pendingSend = send.ToArray();
        _pendingSendAt = DateTime.UtcNow;
    }

    private void OnFileNo(int n, int from, string reason)
    {
        if (n > 0 && from < 0)
        {
            if (_batch is { } b && n <= b.Paths.Length)
            {
                b.Answered.Add(n);
                if (b.Answered.Count >= b.Paths.Length)
                    _batch = null;
            }
            ShowReason(reason.Length > 0 ? reason : "A file was refused");
            return;
        }
        // The answer to a DropOut (never sent from Linux yet, but shown should one come)
        ShowReason(reason.Length > 0 ? reason : "That file can't be downloaded");
    }

    // ================================================================
    // ZMODEM: the board's transfer starts; run it with no picker
    // ================================================================

    /// <summary>
    /// ZMODEM auto-detect fired (-100 the board sends, -101 it receives). True when a file drop owns this transfer
    /// (it then runs without asking); false for a normal transfer.
    /// </summary>
    public bool HandleZmodem(int direction)
    {
        DrainMessages?.Invoke();   // FileGo came just before the transfer: take it first
        if (direction == -101 && _pendingSend is { } paths)
        {
            _pendingSend = null;
            _runSend(paths);
            return true;
        }
        return false;
    }
}
