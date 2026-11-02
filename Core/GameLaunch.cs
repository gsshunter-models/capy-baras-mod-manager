using System.Diagnostics;

namespace MXSimModManager.Core;
public static class GameLaunch
{
    public static string ValidateTrackId(string trackId)
    {
        trackId = trackId.Replace('\\', '/');
        if (string.IsNullOrWhiteSpace(trackId) || trackId.Length > 512 || trackId.StartsWith('-') || trackId.Contains('"') || trackId.Any(char.IsControl))
            throw new InvalidDataException("This track has an invalid folder ID.");
        ModInstaller.SafePath(Path.GetTempPath(), trackId);
        return trackId;
    }

    public static ProcessStartInfo ForTrack(string executable, string trackId, string personalFolder, string stateRoot, bool gameRunning)
    {
        trackId = ValidateTrackId(trackId);
        var start = new ProcessStartInfo(Path.GetFullPath(executable)) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(executable))! };
        // --track starts the game's special single-AI session. Normal startup reads
        // the saved player and race settings, including the selected track.
        SetupManager.SelectTrack(trackId, personalFolder, stateRoot, gameRunning);
        return start;
    }
}
