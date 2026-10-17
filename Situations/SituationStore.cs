using System;
using System.Collections.Generic;
using System.IO;
using Windows.Storage;

// situations.json in the app's LocalFolder: a list of records, too big for an 8 KB LocalSettings value.
// Saves write a temp file and move it over the old one, so a crash mid-write can't truncate it.
static class SituationStore
{
    const string FileName = "situations.json";

    // Every failure means "no situations" (or, unpackaged, "don't persist"), never a crash.
    public static IReadOnlyList<Situation> Load()
    {
        if (FilePath() is not { } path || !File.Exists(path))
            return [];

        try
        {
            return SituationJson.Read(File.ReadAllText(path));
        }
        catch (Exception ex)
        {
            // Unreadable (corrupt, or from a newer traydog): keep it aside rather than overwrite it.
            CrashLog.Write(ex);
            try { File.Move(path, path + ".unreadable", overwrite: true); }
            catch { }
            return [];
        }
    }

    public static void Save(IReadOnlyList<Situation> situations)
    {
        if (FilePath() is not { } path)
            return;

        try
        {
            string temp = path + ".tmp";
            File.WriteAllText(temp, SituationJson.Write(situations));
            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            // Best-effort, like settings; the next change tries again.
        }
    }

    // Null without package identity (the --devtools profile): situations then live in memory only.
    static string? FilePath()
    {
        try
        {
            return Path.Combine(ApplicationData.Current.LocalFolder.Path, FileName);
        }
        catch
        {
            return null;
        }
    }
}
