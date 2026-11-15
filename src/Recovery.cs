using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;

namespace ReiksDocument;

/// <summary>Recovery copies: while a document has changes that are not saved, a copy of it is kept in a folder
/// of the program on this PC. The user's own file is not touched. A copy is removed when the document is saved
/// or closed in the normal way; one that is still there when the program starts belongs to a session that ended
/// unexpectedly, and is offered to the user.</summary>
static class Recovery
{
    public static string Folder =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ReiksDocument", "recovery");

    public sealed record Found(string Id, string File, string? Original, DateTime When);

    static string Doc(string id) => Path.Combine(Folder, id + ".docu");
    static string Info(string id) => Path.Combine(Folder, id + ".txt");

    /// <summary>Writes the copy. It goes to a file of its own first and takes the place of the copy before
    /// only when it is complete and on the disk, so that a stop in the middle leaves the older copy whole.</summary>
    public static void Write(string id, Document doc, LayoutEngine eng, string? original, int? process = null)
    {
        Directory.CreateDirectory(Folder);
        string tmp = Path.Combine(Folder, id + ".tmp");
        RksFile.Save(doc, eng, tmp);
        using (var fs = new FileStream(tmp, FileMode.Open, FileAccess.ReadWrite)) fs.Flush(true);
        File.Move(tmp, Doc(id), true);
        File.WriteAllLines(Info(id), new[] { (process ?? Environment.ProcessId).ToString(CultureInfo.InvariantCulture), original ?? "" });
    }

    // The copy of a long document is written beside the typing: what goes into it is taken from the document at once,
    // the packing and the writing happen on another thread. One at a time for each document.
    static readonly Dictionary<string, System.Threading.Tasks.Task> writing = new();

    /// <summary>Starts writing the copy and comes back at once. False if the copy before this one is still being written;
    /// then nothing was started and the caller tries again later.</summary>
    public static bool WriteBehind(string id, Document doc, LayoutEngine eng, string? original)
    {
        if (writing.TryGetValue(id, out var before) && !before.IsCompleted) return false;
        Directory.CreateDirectory(Folder);
        var packed = RksFile.Pack(doc, eng);
        int process = Environment.ProcessId;
        writing[id] = System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                string tmp = Path.Combine(Folder, id + ".tmp");
                RksFile.Write(packed, tmp, true);
                using (var fs = new FileStream(tmp, FileMode.Open, FileAccess.ReadWrite)) fs.Flush(true);
                File.Move(tmp, Doc(id), true);
                File.WriteAllLines(Info(id), new[] { process.ToString(CultureInfo.InvariantCulture), original ?? "" });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        });
        return true;
    }

    /// <summary>Waits until a copy that is being written is there.</summary>
    public static void Wait(string id)
    {
        if (!writing.Remove(id, out var task)) return;
        try { task.Wait(); }
        catch (AggregateException) { }
    }

    public static void Remove(string id)
    {
        Wait(id);
        foreach (string f in new[] { Doc(id), Info(id), Path.Combine(Folder, id + ".tmp") })
        {
            try { File.Delete(f); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>The copies left behind by sessions that are no longer running, newest first.</summary>
    public static List<Found> Find()
    {
        var list = new List<Found>();
        try
        {
            if (!Directory.Exists(Folder)) return list;
            foreach (string file in Directory.GetFiles(Folder, "*.docu"))
            {
                string id = Path.GetFileNameWithoutExtension(file);
                string[] info = File.Exists(Info(id)) ? File.ReadAllLines(Info(id)) : Array.Empty<string>();
                if (info.Length > 0 && int.TryParse(info[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int pid) && Running(pid)) continue;
                list.Add(new Found(id, file, info.Length > 1 && info[1].Length > 0 ? info[1] : null, File.GetLastWriteTime(file)));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        list.Sort((a, b) => b.When.CompareTo(a.When));
        return list;
    }

    /// <summary>True when the process that wrote a copy is still a running Reiks Document.</summary>
    static bool Running(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            using var me = Process.GetCurrentProcess();
            return !p.HasExited && p.ProcessName == me.ProcessName;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { return false; }
    }
}
