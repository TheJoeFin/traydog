using System;
using System.IO;
using System.Text.Json;
using Windows.Storage;

static class ResourceStore
{
    static string PathName => Path.Combine(ApplicationData.Current.LocalFolder.Path, "resources.json");

    public static ResourceOptions Load(out string? error)
    {
        error = null;
        try
        {
            string path = PathName;
            return File.Exists(path)
                ? (JsonSerializer.Deserialize(File.ReadAllText(path), ResourceJson.Default.ResourceOptions)
                    ?? throw new InvalidDataException("The resource settings file is empty.")).Sanitize()
                : new();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            CrashLog.Write(ex);
            error = "Resource settings could not be loaded. Defaults are in use; the original file has not been changed.";
            return new();
        }
    }

    public static string? Save(ResourceOptions options)
    {
        try
        {
            string path = PathName;
            File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(options, ResourceJson.Default.ResourceOptions));
            File.Move(path + ".tmp", path, overwrite: true);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            CrashLog.Write(ex);
            return "Resource settings could not be saved. Changes apply only until traydog exits.";
        }
    }
}
