using System.IO;

namespace MeetingRecorder.Services;

/// <summary>Free space and what it buys in recording hours — the setup screen's input check.</summary>
public static class DiskInfo
{
    public static long FreeBytes(string path)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            if (string.IsNullOrEmpty(root)) return 0;
            return new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    public static string Describe(string path, int bitrateKbps)
    {
        var free = FreeBytes(path);
        if (free <= 0) return "unavailable";

        var gigabytes = free / 1024.0 / 1024.0 / 1024.0;
        var bytesPerHour = bitrateKbps * 1000.0 / 8.0 * 3600.0;
        var hours = free / bytesPerHour;
        return gigabytes.ToString("0") + " GB · ≈ " + hours.ToString("0") + " h at " +
               bitrateKbps.ToString() + " kbps";
    }
}
