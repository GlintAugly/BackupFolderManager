using BackupFolderManager;

var root = Path.Combine(Path.GetTempPath(), "BackupFolderManagerChecks_" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var checks = 0;
void Check(bool condition, string description)
{
    if (!condition) throw new Exception(description);
    checks++;
    Console.WriteLine("PASS: " + description);
}
try
{
    var staging = Path.Combine(root, "staging");
    var content = Path.Combine(staging, "Game");
    Directory.CreateDirectory(Path.Combine(content, "empty"));
    File.WriteAllText(Path.Combine(content, "save.dat"), "original");
    var destination = Path.Combine(root, "Game");
    Directory.CreateDirectory(destination);
    Check(RestoreFiles.ContentRoot(staging, "Game") == content, "Archive root folder is unwrapped");
    RestoreFiles.CopyInto(content, destination);
    Check(File.ReadAllText(Path.Combine(destination, "save.dat")) == "original" && !Directory.Exists(Path.Combine(destination, "Game")), "Restores into the empty original folder without nesting");
    Check(Directory.Exists(Path.Combine(destination, "empty")), "Preserves empty directories");
    Check(RestoreFiles.AreIdentical(content, destination), "Identical trees match");
    File.WriteAllText(Path.Combine(destination, "save.dat"), "modified");
    Check(!RestoreFiles.AreIdentical(content, destination), "Changed content is detected");
    Check(File.ReadAllText(Path.Combine(destination, "save.dat")) == "modified", "Comparison does not alter existing data");
    File.WriteAllText(Path.Combine(destination, "extra.dat"), "keep");
    RestoreFiles.CopyInto(content, destination);
    Check(File.ReadAllText(Path.Combine(destination, "save.dat")) == "original" && File.Exists(Path.Combine(destination, "extra.dat")), "Overwrite replaces conflicts and retains destination-only files");
    Check(!RestoreFiles.AreIdentical(content, destination), "Extra files are detected");
    File.Delete(Path.Combine(destination, "extra.dat"));
    Directory.CreateDirectory(Path.Combine(destination, "extra-empty"));
    Check(!RestoreFiles.AreIdentical(content, destination), "Extra empty directories are detected");
    File.WriteAllText(Path.Combine(staging, "top-level.txt"), "flat");
    Check(RestoreFiles.ContentRoot(staging, "Game") == staging, "Mixed archive content is not unwrapped");
    var flat = Path.Combine(root, "flat");
    Directory.CreateDirectory(flat);
    File.WriteAllText(Path.Combine(flat, "data.txt"), "flat");
    Check(RestoreFiles.ContentRoot(flat, "Game") == flat, "Flat archives keep their content root");
    var conflict = Path.Combine(root, "conflict");
    Directory.CreateDirectory(conflict);
    File.WriteAllText(Path.Combine(conflict, "empty"), "existing file");
    var rejected = false;
    try { RestoreFiles.CopyInto(content, conflict); } catch (IOException) { rejected = true; }
    Check(rejected && !File.Exists(Path.Combine(conflict, "save.dat")), "File/directory conflict is rejected before writing");
    // A junction requires no symbolic-link privilege on Windows.
    if (OperatingSystem.IsWindows())
    {
        var junction = Path.Combine(root, "junction");
        var start = new System.Diagnostics.ProcessStartInfo("cmd.exe") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        start.ArgumentList.Add("/c");
        start.ArgumentList.Add("mklink");
        start.ArgumentList.Add("/J");
        start.ArgumentList.Add(junction);
        start.ArgumentList.Add(content);
        using var process = System.Diagnostics.Process.Start(start)!;
        process.WaitForExit();
        if (process.ExitCode != 0) throw new Exception("Could not create junction for validation test.");
        try
        {
            rejected = false;
            try { RestoreFiles.CopyInto(content, junction); } catch (IOException) { rejected = true; }
            Check(rejected, "Junction destination is rejected");
        }
        finally { Directory.Delete(junction); }
    }
    var seven = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "7-Zip", "7z.exe");
    if (File.Exists(seven))
    {
        void RunSeven(params string[] arguments)
        {
            var info = new System.Diagnostics.ProcessStartInfo(seven) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = staging, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var argument in arguments) info.ArgumentList.Add(argument);
            using var process = System.Diagnostics.Process.Start(info)!;
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            process.WaitForExit();
            Task.WaitAll(output, error);
            if (process.ExitCode != 0) throw new Exception(output.Result + error.Result);
        }
        var archive = Path.Combine(root, "Game.7z");
        RunSeven("a", "-t7z", "--", archive, "Game");
        var unpacked = Path.Combine(root, "unpacked");
        RunSeven("x", "-y", "-o" + unpacked, "--", archive);
        var restored = Path.Combine(root, "roundtrip", "Game");
        RestoreFiles.CopyInto(RestoreFiles.ContentRoot(unpacked, "Game"), restored);
        Check(RestoreFiles.AreIdentical(content, restored), "Real 7-Zip archive restores original files and empty directories without nesting");
    }
    await BackupChecks.RunAsync(root, Check);
    await FolderChecks.RunAsync(root, Check);
    Console.WriteLine($"{checks} checks passed.");
}
finally
{
    var fullRoot = Path.GetFullPath(root);
    if (Path.GetDirectoryName(fullRoot) == Path.TrimEndingDirectorySeparator(Path.GetTempPath()) && Path.GetFileName(fullRoot).StartsWith("BackupFolderManagerChecks_"))
        Directory.Delete(fullRoot, recursive: true);
}
