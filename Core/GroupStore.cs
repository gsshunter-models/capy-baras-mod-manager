using System.Text.Json;

namespace MXSimModManager.Core;

public sealed class ModGroup
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public List<string> Members { get; set; } = [];
}

/// <summary>Virtual collections; no mod file is renamed or moved by grouping.</summary>
public static class GroupStore
{
    private const int MaximumGroups = 1000;
    private const int MaximumMembers = 100000;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static List<ModGroup> Load(string stateRoot)
    {
        var path = Path.Combine(stateRoot, "groups.json");
        if (!File.Exists(path)) return [];
        try
        {
            if (new FileInfo(path).Length > 16 * 1024 * 1024)
                throw new InvalidDataException("The group file exceeds the supported size.");
            var groups = JsonSerializer.Deserialize<List<ModGroup>>(File.ReadAllText(path));
            return ValidateAndCopy(groups ?? throw new InvalidDataException("The group file is empty or invalid."));
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or NotSupportedException)
        {
            throw new InvalidDataException("The group file is invalid. Its original contents have been preserved.", ex);
        }
    }

    public static void Save(string stateRoot, IEnumerable<ModGroup> groups)
    {
        var validated = ValidateAndCopy(groups);
        var json = JsonSerializer.Serialize(validated, JsonOptions);
        if (System.Text.Encoding.UTF8.GetByteCount(json) > 16 * 1024 * 1024)
            throw new InvalidDataException("The group file exceeds the supported size.");
        Directory.CreateDirectory(stateRoot);
        var path = Path.Combine(stateRoot, "groups.json");
        var temporary = Path.Combine(stateRoot, "groups-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            File.WriteAllText(temporary, json);
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static ModGroup Create(IList<ModGroup> groups, string name)
    {
        if (groups.Count >= MaximumGroups) throw new InvalidDataException("The maximum number of groups has been reached.");
        var cleanName = ValidateName(name);
        EnsureUniqueName(groups, cleanName, null);
        var group = new ModGroup { Id = Guid.NewGuid().ToString("N"), Name = cleanName };
        groups.Add(group);
        return group;
    }

    public static void Rename(IList<ModGroup> groups, ModGroup group, string name)
    {
        if (!groups.Any(g => ReferenceEquals(g, group))) throw new InvalidDataException("This group no longer exists.");
        var cleanName = ValidateName(name);
        EnsureUniqueName(groups, cleanName, group);
        group.Name = cleanName;
    }

    public static void Add(ModGroup group, IEnumerable<string> keys)
    {
        var members = group.Members.Concat(keys).Select(NormalizeKey).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (members.Count > MaximumMembers) throw new InvalidDataException("This group contains too many packages.");
        group.Members = members;
    }

    public static void Remove(ModGroup group, IEnumerable<string> keys)
    {
        var remove = keys.Select(NormalizeKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
        group.Members = group.Members.Select(NormalizeKey).Where(k => !remove.Contains(k)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static List<ModGroup> ValidateAndCopy(IEnumerable<ModGroup> groups)
    {
        ArgumentNullException.ThrowIfNull(groups);
        var result = new List<ModGroup>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in groups)
        {
            if (result.Count >= MaximumGroups) throw new InvalidDataException("The group file contains too many groups.");
            if (group == null || !Guid.TryParse(group.Id, out var id) || group.Members == null)
                throw new InvalidDataException("The group file contains an invalid group.");
            var name = ValidateName(group.Name);
            var idText = id.ToString("N");
            if (!names.Add(name) || !ids.Add(idText)) throw new InvalidDataException("The group file contains duplicate names or IDs.");
            if (group.Members.Count > MaximumMembers) throw new InvalidDataException("A group contains too many packages.");
            result.Add(new ModGroup { Id = idText, Name = name, Members = group.Members.Select(NormalizeKey).Distinct(StringComparer.OrdinalIgnoreCase).ToList() });
        }
        return result;
    }

    private static string ValidateName(string name)
    {
        var clean = name?.Trim() ?? "";
        if (clean.Length == 0 || clean.Length > 60 || clean.Any(char.IsControl))
            throw new InvalidDataException("Use a group name between 1 and 60 characters without control characters.");
        return clean;
    }

    private static void EnsureUniqueName(IEnumerable<ModGroup> groups, string name, ModGroup? except)
    {
        if (groups.Any(g => !ReferenceEquals(g, except) && string.Equals(g.Name, name, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("A group with that name already exists.");
    }

    private static string NormalizeKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key) || !Path.IsPathFullyQualified(key)) throw new InvalidDataException("A group member must identify an absolute package path.");
        var full = Path.GetFullPath(key);
        var root = Path.GetPathRoot(full)!;
        return (full.Length > root.Length ? full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) : full).ToUpperInvariant();
    }
}
