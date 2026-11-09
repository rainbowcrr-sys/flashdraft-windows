using System;
using System.IO;

namespace FlashDraft.App;

/// <summary>
/// Records the user's acceptance of the license. The marker is local to the
/// device; no data leaves the application.
/// </summary>
public static class LicenseGate
{
    private static string MarkerPath
    {
        get
        {
            string root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(root, "FlashDraft", "license-accepted.txt");
        }
    }

    public static bool IsAccepted()
    {
        try { return File.Exists(MarkerPath); }
        catch { return false; }
    }

    public static void MarkAccepted()
    {
        try
        {
            string path = MarkerPath;
            string? dir = Path.GetDirectoryName(path);
            if (dir is not null) Directory.CreateDirectory(dir);
            File.WriteAllText(path, "accepted");
        }
        catch
        {
            // Acceptance still applies for this session if the marker cannot be written.
        }
    }
}
