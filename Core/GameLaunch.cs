using System.Diagnostics;

namespace MXSimModManager.Core;
public static class GameLaunch
{
    public static ProcessStartInfo ForTrack(string executable, string trackId)
    {
        trackId = trackId.Replace('\\', '/');
        if (string.IsNullOrWhiteSpace(trackId) || trackId.Length > 512 || trackId.StartsWith('-') || trackId.Contains('"') || trackId.Any(char.IsControl))
            throw new InvalidDataException("This track has an invalid folder ID.");
        ModInstaller.SafePath(Path.GetTempPath(), trackId);
        var start = new ProcessStartInfo(Path.GetFullPath(executable)) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(executable))! };
        start.ArgumentList.Add("--track"); start.ArgumentList.Add(trackId);
        return start;
    }
}
