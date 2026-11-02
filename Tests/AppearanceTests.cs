using System.Text;
using MXSimModManager.Core;

public static class AppearanceTests
{
    public static void Run(string root)
    {
        var install = Path.Combine(root, "install"); var personal = Path.Combine(root, "personal"); var state = Path.Combine(root, "state");
        Directory.CreateDirectory(install); Directory.CreateDirectory(personal); Directory.CreateDirectory(state);
        // Native metadata fixture: records need not have a NUL before the bike name.
        File.WriteAllBytes(Path.Combine(install, "mx.exe"), Encoding.Latin1.GetBytes("binary-prefixcr125\0@cr125_frame.jm\0@cr125.png\0" + new string('x', 1500) + "float-prefix250sxf(2018)\0@250sxfv2016_frame.jm\0@250sxfv2016.png\0"));
        foreach (var family in new[] { "cr125", "250sxfv2016" })
        {
            Put(install, family + ".png", "diffuse");
            foreach (var part in new[] { "frame", "fork_upper", "fork_lower", "swingarm" }) Put(install, family + "_" + part + ".jm.gz", "model");
        }
        foreach (var part in new[] { "rider_body", "rider_head", "front_wheel", "rear_wheel" }) Put(install, part + ".jm", "default model");
        foreach (var part in new[] { "rider_body", "rider_head", "wheels" }) Put(install, part + ".png", "default texture");
        var entries = new List<string>();
        foreach (var part in new[] { "frame", "fork_upper", "fork_lower", "swingarm" }) entries.Add("Bike Pack/250sxfv2016_" + part + "-Modern.jm.scram");
        entries.AddRange(["Bike Pack/250sxfv2016-Modern-Paint #123.png.scram", "Bike Pack/250sxfv2016-Modern-Paint #123_norm.png.scram", "Gear/rider_body-Mesh.jm", "Gear/rider_body-Mesh-Suit #123.png.scram", "Gear/rider_body-Mesh-Suit #123_spec.png.scram", "Helmet/rider_head-Mesh.jm", "Helmet/rider_head-Mesh-Helmet ; Blue.png.scram", "Wheels/front_wheel-Mesh.jm", "Wheels/rear_wheel-Mesh.jm", "Wheels/wheels-Mesh-Gold.png.scram"]);
        Saf(Path.Combine(personal, "skins.saf"), entries);
        var original = "bike=250sxf(2018)\r\nbike_texture=Modern-Paint #123\r\nrider_texture=Mesh-Suit #123\r\nhead_texture=Mesh-Helmet ; Blue\r\nwheel_texture=Mesh-Gold\r\nstability=25\r\nfork_compression_adjustment=37\r\nshock_rebound_adjustment=62\r\nfront_sprocket_adjustment=1\r\nrear_sprocket_adjustment=-2\r\nleveling=1.75\r\nroll_damping=2.34\r\nname=Test Rider\r\nnumber=30\r\nscreen_width=1280\r\n";
        Put(personal, "config", original);
        var catalog = AppearanceManager.Scan(personal, install);
        Check(catalog.Bikes.Count == 2, "Both native bike records must be discovered without a leading NUL assumption.");
        Check(catalog.Bikes.Single(b => b.Value == "250sxf(2018)").ModelPrefix == "250sxfv2016", "The explicit model family must be used instead of the dyno year.");
        Check(catalog.GetBikeSkins("250sxf(2018)").Count == 2, "Default and protected diffuse bike skin should be indexed, excluding normal maps.");
        Check(catalog.Gear.Count == 2 && catalog.Helmets.Count == 2 && catalog.Wheels.Count == 2, "Gear/helmet/wheel diffuse choices must have matching model parts.");
        Check(catalog.Current.BikeSkin == "Modern-Paint #123" && catalog.Current.Helmet == "Mesh-Helmet ; Blue", "Literal hash and semicolon selector text must remain intact.");
        var look = Copy(catalog.Current); look.Helmet = "default";
        var profile = AppearanceManager.SavePreset(catalog, look, personal, state, "Look One");
        Check(profile.LoadCommand == "/readsetup Look One", "Native load command must match the new appearance preset.");
        var native = File.ReadAllLines(profile.Path);
        var appearanceKeys = new HashSet<string> { "bike", "bike_texture", "rider_texture", "head_texture", "wheel_texture" };
        Check(native.Length == 5 && native.All(l => appearanceKeys.Contains(l.Split('=')[0])), "Appearance preset must contain only the five appearance fields, without setup numbers or rider number.");
        Check(AppearanceManager.ListPresets(personal).Count == 1, "Saved native appearance preset must be listed.");
        Put(personal, "setups/Existing Full Setup", original);
        Throws<IOException>(() => AppearanceManager.SavePreset(catalog, look, personal, state, "Existing Full Setup"));
        Check(File.ReadAllText(Path.Combine(personal, "setups", "Existing Full Setup")) == original, "Saving an appearance must never overwrite an existing full setup.");
        Throws<IOException>(() => AppearanceManager.SavePreset(catalog, look, personal, state, "Look One"));
        Throws<InvalidOperationException>(() => AppearanceManager.Apply(catalog, look, personal, state, true));
        Check(File.ReadAllText(Path.Combine(personal, "config")) == original, "Running-game protection must preserve the config.");
        var backup = AppearanceManager.Apply(catalog, look, personal, state, false);
        Check(File.ReadAllText(backup) == original, "Apply must save an exact original config backup.");
        var applied = File.ReadAllText(Path.Combine(personal, "config"));
        Check(applied.Contains("head_texture=default\r\n") && !applied.Contains("default ; Blue"), "Old selector punctuation must not be appended as a comment.");
        Check(applied.Contains("rider_texture=Mesh-Suit #123\r\n") && applied.Contains("stability=25\r\n") && applied.Contains("name=Test Rider\r\n"), "Unchanged appearance and other settings must be preserved.");
        static string OtherLines(string text, HashSet<string> keys) => string.Join("\n", text.Split('\n').Where(line => !keys.Contains(line.Split('=')[0])));
        Check(OtherLines(original, appearanceKeys) == OtherLines(applied, appearanceKeys), "Appearance apply must preserve every other line, including suspension, gearing, advanced settings and rider number.");
        SetupManager.RestoreBackup(backup, personal, state, false);
        Check(File.ReadAllText(Path.Combine(personal, "config")) == original, "Restore must recover the entire original config.");
        var wrong = Copy(look); wrong.Bike = "cr125";
        Throws<InvalidDataException>(() => AppearanceManager.Apply(catalog, wrong, personal, state, false));
        var unknown = Copy(look); unknown.Gear = "not installed";
        Throws<InvalidDataException>(() => AppearanceManager.SavePreset(catalog, unknown, personal, state, "Unknown"));
        var delimiter = Copy(look); delimiter.Gear = "Mesh-Bad|Delimiter";
        catalog.Gear.Add(new() { Value = delimiter.Gear });
        Throws<InvalidDataException>(() => AppearanceManager.SavePreset(catalog, delimiter, personal, state, "Invalid Delimiter"));

        // A missing current selection may be kept while another field is changed.
        Put(personal, "config", original.Replace("Mesh-Suit #123", "Legacy Missing Gear"));
        var missing = AppearanceManager.Scan(personal, install); var kept = Copy(missing.Current); kept.Helmet = "default";
        AppearanceManager.SavePreset(missing, kept, personal, state, "Keep Missing");

        // Latin1 config cannot silently replace a Unicode selector with question marks.
        var latinBytes = Encoding.Latin1.GetBytes(original.Replace("Test Rider", "Café Rider"));
        File.WriteAllBytes(Path.Combine(personal, "config"), latinBytes);
        var latin = AppearanceManager.Scan(personal, install);
        latin.Gear.Add(new AppearanceChoice { Label = "Mesh-漢字", Value = "Mesh-漢字" });
        var unicode = Copy(latin.Current); unicode.Gear = "Mesh-漢字";
        Throws<InvalidDataException>(() => AppearanceManager.Apply(latin, unicode, personal, state, false));
        Check(File.ReadAllBytes(Path.Combine(personal, "config")).SequenceEqual(latinBytes), "Unrepresentable Unicode must leave original bytes intact.");

        // Individually valid names must also fit the native multiplayer payload together.
        Put(personal, "config", original);
        var longCatalog = AppearanceManager.Scan(personal, install); var tooLong = Copy(longCatalog.Current);
        tooLong.Gear = new string('g', 180); tooLong.Helmet = new string('h', 180); tooLong.Wheels = new string('w', 180);
        longCatalog.Gear.Add(new() { Value = tooLong.Gear }); longCatalog.Helmets.Add(new() { Value = tooLong.Helmet }); longCatalog.Wheels.Add(new() { Value = tooLong.Wheels });
        Throws<InvalidDataException>(() => AppearanceManager.SavePreset(longCatalog, tooLong, personal, state, "Too Long"));
        Check(File.ReadAllText(Path.Combine(personal, "config")) == original, "Oversized rider information must not change config.");
    }
    private static AppearanceSelection Copy(AppearanceSelection s) => new() { Bike = s.Bike, BikeSkin = s.BikeSkin, Gear = s.Gear, Helmet = s.Helmet, Wheels = s.Wheels };
    private static void Put(string root, string path, string text) { var target = Path.Combine(root, path); Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.WriteAllText(target, text, new UTF8Encoding(false)); }
    private static void Saf(string file, List<string> paths)
    {
        using var output = File.Create(file); var header = Encoding.UTF8.GetBytes(string.Join("", paths.Select(p => "1 " + p + "\n")) + "-\n"); output.Write(header); output.Write(new byte[paths.Count]);
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void Throws<T>(Action run) where T : Exception { try { run(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
}
