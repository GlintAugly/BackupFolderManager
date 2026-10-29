using BackupFolderManager;

internal static class FolderChecks
{
    public static async Task RunAsync(string testRoot, Action<bool, string> check)
    {
        var root = Path.Combine(testRoot, "folder-catalog");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(Path.Combine(root, "Expanded"));
        File.WriteAllText(Path.Combine(root, "Expanded", "data"), "data");
        Directory.CreateDirectory(Path.Combine(root, "Archived"));
        File.WriteAllText(Path.Combine(root, "Archived.7z"), "archive");
        File.WriteAllText(Path.Combine(root, "ArchiveOnly.ZIP"), "archive");
        Directory.CreateDirectory(Path.Combine(root, "Both"));
        File.WriteAllText(Path.Combine(root, "Both", "data"), "data");
        File.WriteAllText(Path.Combine(root, "Both.7z"), "archive");
        File.WriteAllText(Path.Combine(root, "Both.zip"), "archive");
        Directory.CreateDirectory(Path.Combine(root, "Empty"));
        Directory.CreateDirectory(Path.Combine(root, ".backup_verify_test"));
        File.WriteAllText(Path.Combine(root, "not-an-archive.txt"), "ignore");
        File.WriteAllText(Path.Combine(root, "Archived.7z.sha256"), "ignore");
        File.WriteAllText(Path.Combine(root, "Archived.par2"), "ignore");
        File.WriteAllText(Path.Combine(root, "Conflict"), "file");
        File.WriteAllText(Path.Combine(root, "Conflict.7z"), "archive");
        var items = FolderCatalog.Read(root).ToDictionary(f => f.Name);
        check(items.Count == 6, "Folder listing merges folders and matching archives, ignoring sidecars and temporary directories");
        check(items["Expanded"].State == "展開済み" && items["Expanded"].HasData && items["Expanded"].ArchivePaths.Count == 0, "Data without archive is displayed as expanded");
        check(items["Archived"].State == "アーカイブ済み" && !items["Archived"].HasData, "Empty source with archive is displayed as archived");
        check(items["ArchiveOnly"].State == "アーカイブ済み（フォルダなし）" && items["ArchiveOnly"].FolderPath == Path.Combine(root, "ArchiveOnly"), "Archive without source gets a folder row and the correct restore destination");
        check(items["Both"].State.Contains("未照合") && items["Both"].ArchivePaths.Count == 2 && items["Both"].HasData, "Expanded data with multiple archives is one unverified row");
        check(items["Empty"].State == "空フォルダ", "Empty folder without archive is distinct from archived data");
        check(items["Conflict"].State == "同名ファイルと競合", "A restore destination occupied by a file is reported");
        check(FolderCatalog.Read(root).Single(f => f.Name == "Both").ArchivePaths.Count == 2, "Refreshing folder listing retains all archive choices");
        var older = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var newer = older.AddDays(1);
        File.SetLastWriteTimeUtc(Path.Combine(root, "Both", "data"), older);
        Directory.CreateDirectory(Path.Combine(root, "Both", "nested"));
        File.WriteAllText(Path.Combine(root, "Both", "nested", "new"), "new");
        File.SetLastWriteTimeUtc(Path.Combine(root, "Both", "nested", "new"), newer);
        check(FolderCatalog.GetLatestFileWriteTimeUtc(Path.Combine(root, "Both")) == newer, "Latest file timestamp includes nested files");
        check(FolderCatalog.GetLatestFileWriteTimeUtc(Path.Combine(root, "Empty")) is null, "Empty folders have no latest file timestamp");

        var backupRoot = Path.Combine(testRoot, "selected-folder-backup");
        var selected = Path.Combine(backupRoot, "Selected");
        var untouched = Path.Combine(backupRoot, "Untouched");
        Directory.CreateDirectory(selected);
        Directory.CreateDirectory(untouched);
        File.WriteAllText(Path.Combine(selected, "data"), "selected");
        File.WriteAllText(Path.Combine(untouched, "data"), "must remain");
        var tools = new BackupPrograms(Path.Combine(testRoot, "fake-7z.exe"), Path.Combine(testRoot, "fake-par2.exe"));
        var calls = 0;
        var service = new BackupService(tools, (_, arguments, _, _) =>
        {
            calls++;
            if (arguments[0] == "a")
            {
                check(arguments[^1] == ".\\Selected", "Only the explicitly selected folder is compressed");
                File.WriteAllText(selected + ".7z", "archive");
            }
            if (arguments[0] == "x")
                RestoreFiles.CopyInto(selected, Path.Combine(arguments.Single(a => a.StartsWith("-o"))[2..], "Selected"));
            if (arguments[0] == "c") File.WriteAllText(selected + ".par2", "recovery");
            return Task.CompletedTask;
        });
        var result = await service.BackupAsync(backupRoot, true, selectedFolders: [selected, selected]);
        check(result == new BackupResult(1, 0, 0) && calls == 5, "Duplicate selections are processed only once");
        check(!Directory.EnumerateFileSystemEntries(selected).Any() && File.ReadAllText(Path.Combine(untouched, "data")) == "must remain" && !File.Exists(untouched + ".7z"), "Archiving selected rows never compresses or deletes an unselected sibling");
        check(FolderCatalog.Read(backupRoot).Single(f => f.Name == "Selected").State == "アーカイブ済み", "Folder status changes to archived after verified backup and source deletion");
        RestoreFiles.CopyInto(untouched, selected);
        check(FolderCatalog.Read(backupRoot).Single(f => f.Name == "Selected").State.Contains("未照合"), "Restored data with retained archive is displayed as expanded and unverified");
        calls = 0;
        result = await service.BackupAsync(backupRoot, true, selectedFolders: []);
        check(result == new BackupResult(0, 0, 0) && calls == 0, "Empty selection does not default to backing up all folders");
        var rejected = false;
        try { await service.BackupAsync(backupRoot, true, selectedFolders: [testRoot]); }
        catch (IOException) { rejected = true; }
        check(rejected && calls == 0, "A selected folder outside the parent is rejected before execution");
        var nested = Path.Combine(untouched, "nested");
        Directory.CreateDirectory(nested);
        rejected = false;
        try { await service.BackupAsync(backupRoot, true, selectedFolders: [nested]); }
        catch (IOException) { rejected = true; }
        check(rejected && calls == 0, "A nested selection is rejected to preserve the parent/folder layout");
    }
}
