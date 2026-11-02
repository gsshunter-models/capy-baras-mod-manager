using System.Text.Json;
using MXSimModManager.Core;

public static class GroupTests
{
    public static void Run(string root)
    {
        Directory.CreateDirectory(root);
        var state = Path.Combine(root, "groups-state");
        var groups = GroupStore.Load(state);
        Require(groups.Count == 0, "Missing group file should start empty.");
        var favorite = GroupStore.Create(groups, "  Favorites  ");
        var practice = GroupStore.Create(groups, "Practice");
        var id = favorite.Id;
        var key = Path.Combine(root, "library", "track.saf");
        var other = Path.Combine(root, "library", "other.saf");
        GroupStore.Add(favorite, [key, key.ToUpperInvariant(), other]);
        GroupStore.Add(practice, [key]);
        Require(favorite.Members.Count == 2 && practice.Members.Count == 1, "Membership should deduplicate but allow multiple groups.");
        GroupStore.Rename(groups, favorite, "Race tracks");
        Require(favorite.Id == id && favorite.Members.Count == 2, "Rename must preserve identity and members.");
        Throws<InvalidDataException>(() => GroupStore.Create(groups, "practice"));
        Throws<InvalidDataException>(() => GroupStore.Rename(groups, favorite, " Practice "));
        Throws<InvalidDataException>(() => GroupStore.Create(groups, "  "));
        Throws<InvalidDataException>(() => GroupStore.Create(groups, new string('x', 61)));
        Throws<InvalidDataException>(() => GroupStore.Create(groups, "bad\nname"));
        Throws<InvalidDataException>(() => GroupStore.Add(favorite, ["relative.saf"]));
        Require(favorite.Members.Count == 2, "Invalid add must leave membership unchanged.");
        GroupStore.Remove(favorite, [key.ToLowerInvariant()]);
        Require(favorite.Members.Count == 1 && practice.Members.Count == 1, "Removal should affect only the selected group.");
        GroupStore.Save(state, groups);
        var loaded = GroupStore.Load(state);
        Require(loaded.Count == 2 && loaded[0].Id == id && loaded[0].Name == "Race tracks" && loaded[0].Members.Count == 1, "Groups must roundtrip.");
        loaded[0].Name = "Enduro";
        GroupStore.Save(state, loaded);
        Require(GroupStore.Load(state)[0].Name == "Enduro", "Existing store should be replaced atomically.");
        Require(!Directory.EnumerateFiles(state, "*.tmp").Any(), "Successful save must not leave temporary files.");
        var before = File.ReadAllText(Path.Combine(state, "groups.json"));
        loaded[0].Name = loaded[1].Name;
        Throws<InvalidDataException>(() => GroupStore.Save(state, loaded));
        Require(File.ReadAllText(Path.Combine(state, "groups.json")) == before, "Invalid save must preserve the previous store.");
        File.WriteAllText(Path.Combine(state, "groups.json"), "{ invalid");
        Throws<InvalidDataException>(() => GroupStore.Load(state));
        Require(File.ReadAllText(Path.Combine(state, "groups.json")) == "{ invalid", "Invalid load must not overwrite the store.");
        File.WriteAllText(Path.Combine(state, "groups.json"), JsonSerializer.Serialize(new[] { new ModGroup { Id = Guid.NewGuid().ToString("N"), Name = "Bad", Members = ["relative"] } }));
        Throws<InvalidDataException>(() => GroupStore.Load(state));
    }

    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); } catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }
}
