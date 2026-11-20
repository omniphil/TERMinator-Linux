using System.IO;
using System.Threading;
using TERMinator.Linux.Models;

namespace TERMinator.Linux.Services;

/// <summary>
/// One lock shared by every TERMinator process, around each read-modify-write of the files in ~/.config/TERMinator
/// (settings, phonebook, call log). Each BBS runs in a process of its own (App.SessionConnection), so the main window
/// and any number of sessions may save the same file at once; every save re-reads the file under this lock and merges
/// its own changes into it, instead of writing out a copy loaded minutes ago.
///
/// Linux: named mutexes aren't shared between processes in .NET here (the Windows client uses
/// Local\TERMinator-AppData), so the lock is an OS file lock instead: appdata.lock in the app data folder, opened with
/// FileShare.None, which .NET turns into flock(LOCK_EX). The kernel drops it when a process dies holding it. Within
/// one process it's re-entrant, like the Windows mutex.
/// </summary>
public static class AppDataLock
{
    private static readonly object s_gate = new();
    private static FileStream? s_file;
    private static int s_depth;

    private static readonly string s_lockPath = Path.Combine(AppSettings.GetAppDataDir(), "appdata.lock");

    /// <summary>Held until disposed. Waits up to 5 s; a lock held longer than that (a hung process) is ignored rather
    /// than hanging this one.</summary>
    public static IDisposable Acquire()
    {
        if (!Monitor.TryEnter(s_gate, 5000))
            return new Releaser(false);

        if (++s_depth == 1)
        {
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (true)
            {
                try
                {
                    s_file = new FileStream(s_lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                    break;
                }
                catch (IOException) when (DateTime.UtcNow < deadline)
                {
                    Thread.Sleep(15);   // another process holds it
                }
                catch
                {
                    break;              // held too long, or the folder can't be written: go ahead without it
                }
            }
        }
        return new Releaser(true);
    }

    /// <summary>Writes the file whole or not at all: to a temporary file beside it, then swapped into place.</summary>
    public static void WriteAtomic(string path, string text)
    {
        var dir = Path.GetDirectoryName(path);
        if (dir != null)
            Directory.CreateDirectory(dir);
        string temp = path + ".tmp";
        File.WriteAllText(temp, text);
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>When the file last changed, or MinValue when it doesn't exist.</summary>
    public static DateTime Stamp(string path)
    {
        try
        {
            return File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
        }
        catch
        {
            return DateTime.MinValue;
        }
    }

    private sealed class Releaser(bool held) : IDisposable
    {
        private bool _held = held;

        public void Dispose()
        {
            if (!_held)
                return;
            _held = false;
            if (--s_depth == 0)
            {
                s_file?.Dispose();
                s_file = null;
            }
            Monitor.Exit(s_gate);
        }
    }
}
