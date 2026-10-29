using BackupFolderManager;
using System.Security.Cryptography;

internal static class BackupChecks
{
    public static async Task RunAsync(string testRoot, Action<bool, string> check)
    {
        var settings = new FolderSettings(Path.Combine(testRoot, "settings.json"));
        var disconnected = Path.Combine(testRoot, "disconnected-drive", "games");
        settings.Save([disconnected]);
        check(new FolderSettings(settings.FilePath).Load().SequenceEqual(new[] { disconnected }) && !Directory.Exists(disconnected), "Settings retain unavailable paths across reloads");
        check(new FolderSettings().FilePath == Path.Combine(AppContext.BaseDirectory, "settings.json"), "Default settings file is beside the executable");
        settings.Save([disconnected, testRoot]);
        check(settings.Load().Length == 2, "Settings updates replace the previous file");
        var migrated = new FolderSettings(Path.Combine(testRoot, "migrated-settings.json"), settings.FilePath);
        check(migrated.Load().SequenceEqual(settings.Load()) && File.Exists(migrated.FilePath), "Old settings migrate to the new location");
        migrated.Save([disconnected]);
        check(migrated.Load().SequenceEqual(new[] { disconnected }), "Settings beside the executable take precedence over old settings");
        var saveFailed = false;
        try { new FolderSettings(Path.Combine(testRoot, "missing-settings-dir", "settings.json")).Save([disconnected]); }
        catch (IOException ex) { saveFailed = ex.Message.Contains("settings.json"); }
        check(saveFailed, "Settings save errors identify the failed file");

        var tools = new BackupPrograms(Path.Combine(testRoot, "fake-7z.exe"), Path.Combine(testRoot, "fake-par2.exe"));
        File.WriteAllText(tools.SevenZip, "fake tool used only by injected runner");
        File.WriteAllText(tools.Par2, "fake tool used only by injected runner");
        foreach (var scenario in new[] { "success-delete", "keep-source", "fail-a", "fail-t", "fail-x", "fail-c", "fail-v", "bad-extraction", "source-changed", "late-source-change", "archive-changed", "no-par-output" })
        {
            var root = Path.Combine(testRoot, scenario);
            var source = Path.Combine(root, "Game [test]");
            Directory.CreateDirectory(Path.Combine(source, "empty"));
            var sourceFile = Path.Combine(source, "save.dat");
            File.WriteAllText(sourceFile, "original");
            Directory.CreateDirectory(Path.Combine(root, "AlreadyEmpty"));
            var calls = new List<string>();
            var log = new InlineProgress();
            var service = new BackupService(tools, (executable, arguments, working, _) =>
            {
                var command = arguments[0];
                calls.Add(command);
                if (scenario == "fail-" + command) throw new IOException("Injected external program failure");
                if (command == "a")
                {
                    check(working == root && arguments[^1] == ".\\Game [test]", "Compression uses source parent and literal folder argument");
                    File.WriteAllText(source + ".7z", "archive");
                }
                if (command == "x")
                {
                    var extraction = arguments.Single(a => a.StartsWith("-o"))[2..];
                    var extractedRoot = Path.Combine(extraction, Path.GetFileName(source));
                    RestoreFiles.CopyInto(source, extractedRoot);
                    if (scenario == "bad-extraction") File.WriteAllText(Path.Combine(extractedRoot, "save.dat"), "corrupt");
                    if (scenario == "source-changed") File.WriteAllText(sourceFile, "modified");
                }
                if (command == "c")
                {
                    check(File.Exists(source + ".7z.sha256"), "SHA256 is written before PAR2 creation");
                    if (scenario != "no-par-output") File.WriteAllText(source + ".par2", "recovery");
                }
                if (command == "v")
                {
                    check(File.Exists(sourceFile), "Original data remains until PAR2 verification finishes");
                    if (scenario == "late-source-change") File.WriteAllText(sourceFile, "changed during PAR2 verification");
                    if (scenario == "archive-changed") File.WriteAllText(source + ".7z", "changed archive");
                }
                return Task.CompletedTask;
            });
            if (scenario == "success-delete") File.SetAttributes(sourceFile, FileAttributes.ReadOnly);
            var result = await service.BackupAsync(root, scenario != "keep-source", log);
            if (scenario is "success-delete" or "keep-source")
            {
                check(result == new BackupResult(1, 1, 0) && calls.SequenceEqual(new[] { "a", "t", "x", "c", "v" }), scenario + ": all stages run in order");
                check(Directory.Exists(source) && (scenario == "keep-source" ? File.Exists(sourceFile) : !Directory.EnumerateFileSystemEntries(source).Any()), scenario + ": source retention policy is respected");
                var expected = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(source + ".7z"))).ToLowerInvariant();
                check(File.ReadAllText(source + ".7z.sha256") == expected + " *Game [test].7z\n", "Archive SHA256 sidecar has the expected format");
            }
            else
            {
                check(result == new BackupResult(0, 1, 1) && File.Exists(sourceFile), scenario + ": failure retains original data");
                check(log.Messages.Any(m => m.StartsWith("FAILED:")), scenario + ": failure is logged");
                if (scenario is "bad-extraction" or "source-changed") check(!calls.Contains("c"), scenario + ": PAR2 stage is not reached");
            }
            check(!Directory.GetDirectories(root).Any(p => Path.GetFileName(p).StartsWith(".backup_verify_")), scenario + ": verification directory is cleaned");
        }

        var collisionRoot = Path.Combine(testRoot, "existing-output");
        var collisionSource = Path.Combine(collisionRoot, "Game");
        Directory.CreateDirectory(collisionSource);
        File.WriteAllText(Path.Combine(collisionSource, "save.dat"), "keep");
        File.WriteAllText(collisionSource + ".7z", "existing");
        var called = false;
        var collisionService = new BackupService(tools, (_, _, _, _) => { called = true; return Task.CompletedTask; });
        var collisionResult = await collisionService.BackupAsync(collisionRoot, true);
        check(collisionResult.Failed == 1 && !called && File.ReadAllText(collisionSource + ".7z") == "existing", "Existing outputs prevent compression and remain unchanged");

        var missingTools = new BackupPrograms(tools.SevenZip, Path.Combine(testRoot, "missing-par2.exe"));
        var dependencyService = new BackupService(missingTools, (_, _, _, _) => throw new Exception("Must not start"));
        var dependencyRejected = false;
        try { await dependencyService.BackupAsync(collisionRoot, true); }
        catch (FileNotFoundException) { dependencyRejected = true; }
        check(dependencyRejected && File.Exists(Path.Combine(collisionSource, "save.dat")), "Missing dependencies fail before processing source data");
        dependencyRejected = false;
        try { await ProgramRunner.RunAsync(missingTools.Par2, [], testRoot, null); }
        catch (FileNotFoundException) { dependencyRejected = true; }
        check(dependencyRejected, "Process launcher rechecks executable existence");

        foreach (var scenario in new[] { "replace-keep", "replace-delete", "fail-a", "fail-t", "fail-x", "fail-c", "fail-v", "bad-content", "source-change", "publish-collision" })
        {
            var root = Path.Combine(testRoot, "replacement-" + scenario);
            var source = Path.Combine(root, "Game");
            Directory.CreateDirectory(source);
            var data = Path.Combine(source, "updated.dat");
            File.WriteAllText(data, "updated data");
            var old = new Dictionary<string, string>
            {
                [source + ".7z"] = "old archive",
                [source + ".7z.sha256"] = "old hash",
                [source + ".vol00+01.par2"] = "old recovery volume"
            };
            if (scenario != "publish-collision") old[source + ".par2"] = "old recovery index";
            foreach (var item in old) File.WriteAllText(item.Key, item.Value);
            if (scenario == "publish-collision") Directory.CreateDirectory(source + ".par2");
            File.WriteAllText(source + ".zip", "unrelated zip");
            var service = new BackupService(tools, (_, arguments, working, _) =>
            {
                var command = arguments[0];
                if (scenario == "fail-" + command) throw new IOException("Injected failure");
                if (command == "a")
                {
                    var output = arguments[^2];
                    check(working == root && output != source + ".7z" && !File.Exists(output), "Replacement is built fresh beside the source without modifying the old archive");
                    File.WriteAllText(output, "new archive");
                }
                if (command == "x")
                {
                    var output = Path.Combine(arguments.Single(a => a.StartsWith("-o"))[2..], "Game");
                    RestoreFiles.CopyInto(source, output);
                    if (scenario == "bad-content") File.WriteAllText(Path.Combine(output, "updated.dat"), "bad");
                }
                if (command == "c")
                {
                    File.WriteAllText(arguments[2], "new recovery index");
                    File.WriteAllText(Path.Combine(Path.GetDirectoryName(arguments[2])!, "Game.vol00+02.par2"), "new volume");
                }
                if (command == "v")
                {
                    check(old.All(item => File.ReadAllText(item.Key) == item.Value), "Old output set remains untouched through recovery verification");
                    if (scenario == "source-change") File.WriteAllText(data, "changed during verification");
                }
                return Task.CompletedTask;
            });
            var result = await service.BackupAsync(root, scenario == "replace-delete", selectedFolders: [source], overwriteExisting: true);
            if (scenario.StartsWith("replace-"))
            {
                check(result == new BackupResult(1, 0, 0) && File.ReadAllText(source + ".7z") == "new archive" && File.ReadAllText(source + ".par2") == "new recovery index", scenario + ": verified outputs replace old outputs");
                check(!File.Exists(source + ".vol00+01.par2") && File.Exists(source + ".vol00+02.par2"), "Replacement removes stale recovery volumes and publishes new volumes");
                var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(source + ".7z"))).ToLowerInvariant();
                check(File.ReadAllText(source + ".7z.sha256") == hash + " *Game.7z\n", "Replacement SHA256 references the final archive name");
                check(File.Exists(data) == (scenario == "replace-keep"), "Replacement respects source retention after publication");
            }
            else
                check(result.Failed == 1 && File.Exists(data) && old.All(item => File.ReadAllText(item.Key) == item.Value), scenario + ": failure preserves or restores the entire old output set and source");
            check(File.ReadAllText(source + ".zip") == "unrelated zip", "Replacement leaves existing ZIP untouched");
            check(!Directory.EnumerateDirectories(root).Any(p => Path.GetFileName(p).StartsWith(".backup_verify_")), scenario + ": replacement temporary directories are cleaned");
        }

        // Exercise the production backup pipeline with real 7-Zip; PAR2 is simulated if not installed.
        string seven;
        try { seven = RequiredPrograms.FindSevenZip(); }
        catch (FileNotFoundException)
        {
            Console.WriteLine("SKIP: Real backup integration requires 7-Zip.");
            return;
        }
        var integrationRoot = Path.Combine(testRoot, "backup-integration");
        var integrationSource = Path.Combine(integrationRoot, "@Game [日本語]");
        Directory.CreateDirectory(Path.Combine(integrationSource, "empty"));
        File.WriteAllText(Path.Combine(integrationSource, "save.dat"), "roundtrip");
        var integrationService = new BackupService(new BackupPrograms(seven, tools.Par2), async (executable, arguments, working, log) =>
        {
            if (executable == seven) await ProgramRunner.RunAsync(executable, arguments, working, log);
            else if (arguments[0] == "c") File.WriteAllText(arguments[2], "simulated PAR2");
        });
        var integrationResult = await integrationService.BackupAsync(integrationRoot, true);
        check(integrationResult == new BackupResult(1, 0, 0) && Directory.Exists(integrationSource) && !Directory.EnumerateFileSystemEntries(integrationSource).Any(), "C# backup pipeline verifies a real 7-Zip roundtrip before deleting source data (PAR2 simulated)");
        File.WriteAllText(Path.Combine(integrationSource, "new.dat"), "updated roundtrip");
        integrationResult = await integrationService.BackupAsync(integrationRoot, false, selectedFolders: [integrationSource], overwriteExisting: true);
        var restored = Path.Combine(testRoot, "replacement-roundtrip");
        await ProgramRunner.RunAsync(seven, ["x", "-y", "-o" + restored, "--", integrationSource + ".7z"], integrationRoot, null);
        var restoredSource = Path.Combine(restored, Path.GetFileName(integrationSource));
        check(integrationResult == new BackupResult(1, 0, 0) && File.Exists(Path.Combine(restoredSource, "new.dat")) && !File.Exists(Path.Combine(restoredSource, "save.dat")), "Real 7-Zip replacement includes updated files and omits deleted files");
    }

    private sealed class InlineProgress : IProgress<string>
    {
        public List<string> Messages { get; } = [];
        public void Report(string value) => Messages.Add(value);
    }
}
