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
    }

    private sealed class InlineProgress : IProgress<string>
    {
        public List<string> Messages { get; } = [];
        public void Report(string value) => Messages.Add(value);
    }
}
