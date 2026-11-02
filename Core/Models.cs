namespace MXSimModManager.Core;

public class ModPackage
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public string Scope { get; set; } = "Personal";
    public string Categories { get; set; } = "Other";
    public string Details { get; set; } = "";
    public string Evidence { get; set; } = "";
    public bool IsDisabled { get; set; }
    public bool IsReadOnly { get; set; }
    public long SizeBytes { get; set; }
    public List<string> TrackIds { get; set; } = [];
    public List<string> TrackNames { get; set; } = [];
    public int SkinCount { get; set; }
    public int ModelCount { get; set; }
    public string? MapPath { get; set; }
    public DateTime ModifiedUtc { get; set; }
    public int TrackCount => TrackIds.Count;
    public string CategoryLabel => Categories;
    public string SizeText => FormatSize(SizeBytes);
    public string StatusLabel => IsDisabled ? "Disabled" : Scope == "Base game" ? "Base game" : "Installed";
    public static string FormatSize(long value) => value >= 1L << 30 ? $"{value / (double)(1L << 30):0.0} GB" : value >= 1L << 20 ? $"{value / (double)(1L << 20):0.0} MB" : $"{value / 1024d:0.#} KB";
}

public class LibrarySnapshot
{
    public List<ModPackage> Packages { get; set; } = [];
    public List<string> Warnings { get; set; } = [];
    public int UniqueTrackCount { get; set; }
    public int GearPackageCount { get; set; }
    public int BikePackageCount { get; set; }
    public long TotalBytes { get; set; }
}

public record InstallEntry(string SourcePath, string RelativePath, long Size);
public sealed class InstallPlan : IDisposable
{
    public string StagingFolder { get; init; } = "";
    public string SourceName { get; init; } = "";
    public List<InstallEntry> Files { get; init; } = [];
    public List<string> Skipped { get; init; } = [];
    public void Dispose() { if (Directory.Exists(StagingFolder)) Directory.Delete(StagingFolder, true); }
}

public class InstallReceipt
{
    public string Id { get; set; } = "";
    public DateTime CreatedUtc { get; set; }
    public string Name { get; set; } = "";
    public string LibraryRoot { get; set; } = "";
    public bool Undone { get; set; }
    public bool Completed { get; set; }
    public List<InstalledFile> Files { get; set; } = [];
}
public class InstalledFile
{
    public string RelativePath { get; set; } = "";
    public string InstalledHash { get; set; } = "";
    public string? BackupPath { get; set; }
}
public class DisabledPackage
{
    public string OriginalPath { get; set; } = "";
    public string PayloadPath { get; set; } = "";
    public string Name { get; set; } = "";
}
