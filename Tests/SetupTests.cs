using System.Text;
using MXSimModManager.Core;

public static class SetupTests
{
    public static int Run()
    {
        var tests = new (string Name, Action<Area> Test)[]
        {
            ("read/capture/filter native files", ReadAndCapture),
            ("import filters private and cosmetic fields", Import),
            ("finite numbers, suspension scale, native enums", Validation),
            ("edited overwrite preserves original backup", SaveOverwrite),
            ("apply preserves BOM, mixed endings, comments and other settings", ApplyPreservesText),
            ("apply preserves legacy and UTF16 encoding", EncodingRoundTrip),
            ("config backups restore and preserve current config", Restore),
            ("running game blocks capture/apply/restore", RunningGame),
            ("native export is exact and refuses overwrite/private fields", Export),
            ("unsafe names, license input, outside backup rejected", UnsafeInputs)
        };
        var root = Path.Combine(Path.GetTempPath(), "MXSimModManager-setup-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var failures = 0;
        try
        {
            for (var i = 0; i < tests.Length; i++)
            {
                var area = new Area(Path.Combine(root, i.ToString("D2")));
                try { tests[i].Test(area); Console.WriteLine("PASS Setup " + tests[i].Name); }
                catch (Exception ex) { failures++; Console.WriteLine("FAIL Setup " + tests[i].Name + ": " + ex); }
            }
            Console.WriteLine($"Setup tests: {tests.Length - failures}/{tests.Length} passed.");
            return failures;
        }
        finally
        {
            var full = Path.GetFullPath(root);
            var temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!full.StartsWith(temp, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(full).StartsWith("MXSimModManager-setup-tests-"))
                throw new InvalidOperationException("Refusing cleanup outside the generated setup test folder.");
            if (Directory.Exists(full)) Directory.Delete(full, true);
        }
    }

    private static void ReadAndCapture(Area a)
    {
        a.Config("# native config\nbike=fc450(2017)\nstability=35.000000\nfork_compression_adjustment=50\nfront_brake=Antilock\nname=Synthetic Rider\nnumber=999\nrider_texture=sample\nserver_password=synthetic-secret\nfov=105\nthrottle_key=keyboard\n");
        var settings = SetupManager.ReadCurrent(a.Personal);
        Equal(4, settings.Count, "Only bike fields returned");
        Check(settings.All(s => s.Key is "bike" or "stability" or "fork_compression_adjustment" or "front_brake"), "No identity or graphics returned");
        Check(settings.All(s => s.Label.Length > 0 && s.Group.Length > 0), "Editor labels/groups populated");
        var profile = SetupManager.SaveCurrent(a.Personal, a.State, "My MX Setup", false);
        Equal("/readsetup My MX Setup", profile.LoadCommand, "Native load command");
        Equal(Path.Combine(a.Personal, "setups", "My MX Setup"), profile.Path, "Native filename");
        Check(!profile.HasOtherSettings, "Captured profile is setup-only");
        Equal(1, SetupManager.List(a.Personal).Count, "Captured native setup listed");
        var contents = File.ReadAllText(profile.Path);
        Check(!contents.Contains("Synthetic Rider") && !contents.Contains("server_password") && !contents.Contains("throttle_key"), "Private and unrelated settings excluded");
        Throws<IOException>(() => SetupManager.SaveCurrent(a.Personal, a.State, "My MX Setup", false), "Capture refuses existing name");
        Equal(0, SetupManager.ListBackups(a.State).Count, "Capture does not modify config");
    }

    private static void Import(Area a)
    {
        var source = a.Source("native.txt", "bike=yz250f(2014)\nstability=40\nname=Old Rider\nnumber=12\nbike_texture=oldskin\npassword=synthetic\n");
        var profile = SetupManager.Import(source, a.Personal, a.State, "Imported");
        var contents = File.ReadAllText(profile.Path);
        Equal("bike=yz250f(2014)\nstability=40\n", contents, "Import writes allowlisted native fields only");
        Throws<IOException>(() => SetupManager.Import(source, a.Personal, a.State, "Imported"), "Import refuses duplicate name");
        var invalid = a.Source("unrelated.txt", "name=Someone\nvideo_mode=0\n");
        Throws<InvalidDataException>(() => SetupManager.Import(invalid, a.Personal, a.State, "Empty"), "Empty import rejected");
        Check(!File.Exists(Path.Combine(a.Personal, "setups", "Empty")), "Invalid import writes no preset");
    }

    private static void Validation(Area a)
    {
        foreach (var value in new[] { "NaN", "Infinity", "-Infinity", "1e999", "one", "1,5", "50\nstability=1" })
            Throws<InvalidDataException>(() => SetupManager.SaveEdited(a.Personal, a.State, "Bad", [Setting("stability", value)]), "Nonfinite/invalid numeric value");
        foreach (var value in new[] { "-0.1", "100.1" })
            Throws<InvalidDataException>(() => SetupManager.SaveEdited(a.Personal, a.State, "Bad", [Setting("fork_spring_rate_adjustment", value)]), "Suspension uses native 0–100 scale");
        Throws<InvalidDataException>(() => SetupManager.SaveEdited(a.Personal, a.State, "Bad", [Setting("name", "Someone")]), "Unknown/private editor key rejected");
        Throws<InvalidDataException>(() => SetupManager.SaveEdited(a.Personal, a.State, "Bad", [Setting("stability", "20"), Setting("STABILITY", "30")]), "Duplicate key rejected");
        Throws<InvalidDataException>(() => SetupManager.SaveEdited(a.Personal, a.State, "Bad", [Setting("front_brake", "Antilcok")]), "Typo enum rejected");
        Throws<InvalidDataException>(() => SetupManager.SaveEdited(a.Personal, a.State, "Bad", [Setting("auto_shift", "Maybe")]), "Toggle enum rejected");
        var good = SetupManager.SaveEdited(a.Personal, a.State, "Good", [Setting("stability", "1000"), Setting("front_sprocket_adjustment", "-2"),
            Setting("fork_spring_rate_adjustment", "0"), Setting("shock_rebound_adjustment", "100"), Setting("front_brake", "antilock"), Setting("stop_wheelies", "front only")]);
        var settings = SetupManager.ReadSettings(good.Path);
        Equal("Antilock", settings.Single(s => s.Key == "front_brake").Value, "Native enum normalized");
        Equal("Front Only", settings.Single(s => s.Key == "stop_wheelies").Value, "Third stop-wheelies mode supported");
        Equal("-2", settings.Single(s => s.Key == "front_sprocket_adjustment").Value, "Signed gearing offset allowed");
    }

    private static void SaveOverwrite(Area a)
    {
        var original = Encoding.UTF8.GetBytes("name=Synthetic Old Rider\nstability=25\nrider_texture=old\n");
        var path = Path.Combine(a.Personal, "setups", "Original"); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllBytes(path, original);
        Check(SetupManager.List(a.Personal).Single().HasOtherSettings, "Native identity/cosmetics flagged");
        var saved = SetupManager.SaveEdited(a.Personal, a.State, "Original", [Setting("stability", "75")]);
        Check(saved.LastBackupPath is not null && File.Exists(saved.LastBackupPath), "Existing native preset backed up");
        Bytes(original, File.ReadAllBytes(saved.LastBackupPath!), "Full native preset backup exact");
        Equal("stability=75\n", File.ReadAllText(path), "Edited file contains supported settings only");
        Equal(0, SetupManager.ListBackups(a.State).Count, "Preset backup never mixed with config restore backups");
        var imported = SetupManager.Import(saved.LastBackupPath!, a.Personal, a.State, "Recovered");
        Equal("25", SetupManager.ReadSettings(imported.Path).Single().Value, "Preset backup can be recovered as a new filtered native setup");
    }

    private static void ApplyPreservesText(Area a)
    {
        var original = "# Keep heading\r\nname = Synthetic Rider \r\nstability = 1 # keep comment\r\nstability=2\t\nserver_password = synthetic-secret\nlast=untouched";
        var bom = new byte[] { 0xEF, 0xBB, 0xBF };
        var before = bom.Concat(Encoding.UTF8.GetBytes(original)).ToArray(); a.ConfigBytes(before);
        var source = a.Source("edit", "stability=75\nfork_compression_adjustment=25\nname=Do Not Apply\nserver_password=Do Not Apply\n");
        var backup = SetupManager.Apply(source, a.Personal, a.State, false);
        Bytes(before, File.ReadAllBytes(backup), "Full config backed up before applying");
        var expected = original.Replace("stability = 1", "stability = 75").Replace("stability=2", "stability=75") + "\r\nfork_compression_adjustment=25\r\n";
        Bytes(bom.Concat(Encoding.UTF8.GetBytes(expected)).ToArray(), File.ReadAllBytes(a.ConfigPath), "Only selected values and necessary appended key change");
        Equal(1, SetupManager.ListBackups(a.State).Count, "Config backup listed");
    }

    private static void EncodingRoundTrip(Area a)
    {
        var source = a.Source("edit", "stability=70\n");
        foreach (var encoding in new Encoding[] { Encoding.Latin1, Encoding.Unicode, Encoding.BigEndianUnicode })
        {
            var text = "# café\r\nstability=10\r\nunknown=keep";
            var bytes = encoding.GetPreamble().Concat(encoding.GetBytes(text)).ToArray(); a.ConfigBytes(bytes);
            SetupManager.Apply(source, a.Personal, a.State, false);
            var expected = encoding.GetPreamble().Concat(encoding.GetBytes(text.Replace("stability=10", "stability=70"))).ToArray();
            Bytes(expected, File.ReadAllBytes(a.ConfigPath), "Encoding and final newline retained");
        }
    }

    private static void Restore(Area a)
    {
        var original = Encoding.UTF8.GetBytes("name=Synthetic\nstability=10\nother=untouched\n"); a.ConfigBytes(original);
        var source = a.Source("edit", "stability=90\n");
        var backup = SetupManager.Apply(source, a.Personal, a.State, false);
        var applied = File.ReadAllBytes(a.ConfigPath);
        var currentBackup = SetupManager.RestoreBackup(backup, a.Personal, a.State, false);
        Bytes(original, File.ReadAllBytes(a.ConfigPath), "Full original config restored exactly");
        Bytes(applied, File.ReadAllBytes(currentBackup), "Restoring first backs up current config");
        Equal(2, SetupManager.ListBackups(a.State).Count, "Both backups visible");
    }

    private static void RunningGame(Area a)
    {
        a.Config("stability=10\n");
        var source = a.Source("edit", "stability=20\n");
        var original = File.ReadAllBytes(a.ConfigPath);
        Throws<InvalidOperationException>(() => SetupManager.SaveCurrent(a.Personal, a.State, "Current", true), "Live config capture blocked");
        Throws<InvalidOperationException>(() => SetupManager.Apply(source, a.Personal, a.State, true), "Live config write blocked");
        Throws<InvalidOperationException>(() => SetupManager.RestoreBackup(source, a.Personal, a.State, true), "Live config restore blocked");
        Bytes(original, File.ReadAllBytes(a.ConfigPath), "Blocked operations mutate nothing");
        Equal(0, SetupManager.ListBackups(a.State).Count, "Blocked operations create no backups");
    }

    private static void Export(Area a)
    {
        var original = "# native file\r\nname=Synthetic\r\nstability=20\r\n";
        var source = a.Source("native", original);
        var destination = Path.Combine(a.Sources, "export.setup");
        SetupManager.Export(source, destination);
        Bytes(File.ReadAllBytes(source), File.ReadAllBytes(destination), "Native export unchanged, including other native fields");
        Throws<IOException>(() => SetupManager.Export(source, destination), "Export refuses overwrite");
        var sensitive = a.Source("private", "stability=20\nserver_password=synthetic\n");
        Throws<InvalidDataException>(() => SetupManager.Export(sensitive, Path.Combine(a.Sources, "private-export")), "Private account key blocks unchanged export");
    }

    private static void UnsafeInputs(Area a)
    {
        a.Config("stability=10\n");
        foreach (var name in new[] { "../escape", "sub/path", "sub\\path", "name\n/readsetup other", "\"quoted\"", "name:stream", "NUL", "trail.", "trail " })
            Throws<InvalidDataException>(() => SetupManager.SaveEdited(a.Personal, a.State, name, [Setting("stability", "25")]), "Unsafe setup name rejected");
        var regkey = a.Source("regkey", "stability=25\n");
        Throws<InvalidDataException>(() => SetupManager.ReadSettings(regkey), "License path is never read as setup");
        var outside = a.Source("outside.config", "stability=25\n");
        Throws<InvalidDataException>(() => SetupManager.RestoreBackup(outside, a.Personal, a.State, false), "Backup outside manager state rejected");
        Equal("stability=10\n", File.ReadAllText(a.ConfigPath), "Unsafe operations leave config intact");
    }

    private static SetupSetting Setting(string key, string value) => new() { Key = key, Value = value };
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void Equal<T>(T expected, T actual, string message) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException($"{message}: expected {expected}, got {actual}"); }
    private static void Bytes(byte[] expected, byte[] actual, string message) { if (!expected.AsSpan().SequenceEqual(actual)) throw new InvalidOperationException(message + ": byte content differs"); }
    private static void Throws<T>(Action action, string message) where T : Exception
    {
        try { action(); } catch (T) { return; }
        throw new InvalidOperationException(message + ": expected " + typeof(T).Name);
    }
    private sealed class Area
    {
        public string Personal { get; }
        public string State { get; }
        public string Sources { get; }
        public string ConfigPath => Path.Combine(Personal, "config");
        public Area(string root)
        {
            Personal = Path.Combine(root, "personal"); State = Path.Combine(root, "state"); Sources = Path.Combine(root, "sources");
            foreach (var path in new[] { Personal, State, Sources }) Directory.CreateDirectory(path);
        }
        public void Config(string text) => ConfigBytes(new UTF8Encoding(false).GetBytes(text));
        public void ConfigBytes(byte[] bytes) => File.WriteAllBytes(ConfigPath, bytes);
        public string Source(string name, string text)
        {
            var path = Path.Combine(Sources, name); File.WriteAllText(path, text, new UTF8Encoding(false)); return path;
        }
    }
}
