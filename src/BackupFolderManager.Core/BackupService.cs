using System.Security.Cryptography;
using System.Text;

namespace BackupFolderManager;

public sealed record BackupResult(int Succeeded, int Skipped, int Failed);

public sealed class BackupService
{
    private readonly BackupPrograms? programsOverride;
    private readonly Func<string, IReadOnlyList<string>, string, Action<string>?, Task> run;

    public BackupService() => run = ProgramRunner.RunAsync;

    // Enables failure-path checks without running external programs against real data.
    internal BackupService(BackupPrograms programs, Func<string, IReadOnlyList<string>, string, Action<string>?, Task> runner)
    {
        programsOverride = programs;
        run = runner;
    }

    public async Task<BackupResult> BackupAsync(string rootPath, bool deleteSource, IProgress<string>? progress = null,
        IEnumerable<string>? selectedFolders = null, bool overwriteExisting = false)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath));
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException("対象フォルダが見つかりません: " + root);
        RejectLink(root);
        var programs = programsOverride ?? RequiredPrograms.FindForBackup(root);
        CheckPrograms(programs);
        // Snapshot before creating any temporary verification directory.
        var folders = selectedFolders is null ? Directory.GetDirectories(root) :
            selectedFolders.Select(p => Path.TrimEndingDirectorySeparator(Path.GetFullPath(p)))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        foreach (var folder in folders) EnsureImmediateChild(root, folder);
        folders = folders.Where(p => !Path.GetFileName(p).StartsWith(".backup_verify_", StringComparison.OrdinalIgnoreCase)).ToArray();
        var succeeded = 0;
        var skipped = 0;
        var failed = 0;
        Action<string> log = message => progress?.Report(message);
        foreach (var folder in folders)
        {
            var archive = folder + ".7z";
            var par = folder + ".par2";
            var temp = Path.Combine(root, ".backup_verify_" + Guid.NewGuid().ToString("N"));
            var extractionRoot = Path.GetFullPath(Path.GetTempPath());
            var extraction = Path.Combine(extractionRoot, ".backup_verify_" + Guid.NewGuid().ToString("N"));
            var deleting = false;
            log("=== " + Path.GetFileName(folder) + " ===");
            try
            {
                RejectLink(folder);
                if (!Directory.EnumerateFileSystemEntries(folder).Any())
                {
                    skipped++;
                    log("SKIP: 空のフォルダです。");
                    continue;
                }
                if (!overwriteExisting) CheckExistingOutputs(folder, archive, par);
                else
                {
                    // Build a fresh archive: updating an old archive could retain deleted files.
                    Directory.CreateDirectory(temp);
                    archive = Path.Combine(temp, Path.GetFileName(folder) + ".7z");
                    par = Path.Combine(temp, Path.GetFileName(folder) + ".par2");
                    log("再圧縮: 旧 .7z・SHA256・PAR2 を先に削除し、現在の元データから新規作成します。");
                }
                long sourceBytes = 0;
                var before = GetInventory(folder, length => sourceBytes = checked(sourceBytes + length));
                CheckExtractionSpace(extractionRoot, sourceBytes);

                if (overwriteExisting)
                {
                    var oldFiles = OutputFiles(root, Path.GetFileName(folder));
                    foreach (var file in oldFiles) RejectLink(file);
                    foreach (var file in oldFiles)
                    {
                        log("旧出力を削除: " + file);
                        File.Delete(file);
                    }
                }
                // Source deletion remains last, after all verification succeeds.
                await CompressAsync(programs, root, folder, archive, log).ConfigureAwait(false);
                await TestArchiveAsync(programs, root, archive, log).ConfigureAwait(false);
                // Compression may have consumed space on the TEMP volume too.
                CheckExtractionSpace(extractionRoot, sourceBytes);
                log("検証用の一時展開先: " + extraction);
                await ExtractForVerificationAsync(programs, root, archive, extraction, log).ConfigureAwait(false);
                VerifyInventory(before, Path.Combine(extraction, Path.GetFileName(folder)), "展開内容が元データと一致しません。");
                VerifyInventory(before, folder, "処理中に元データが変更されました。");
                var hash = await WriteArchiveHashAsync(archive).ConfigureAwait(false);
                await CreateRecoveryAsync(programs, root, archive, par, log).ConfigureAwait(false);
                await VerifyRecoveryAsync(programs, root, par, log).ConfigureAwait(false);
                await VerifyArchiveHashAsync(archive, hash).ConfigureAwait(false);
                VerifyInventory(before, folder, "削除前の再確認で元データの変更を検出しました。");
                if (overwriteExisting) PublishReplacement(root, folder, temp, log);
                if (deleteSource)
                {
                    deleting = true;
                    DeleteSourceContents(folder);
                }
                succeeded++;
                log("SUCCESS: " + Path.GetFileName(folder) + (deleteSource ? "（元フォルダは空で保持）" : "（元データを保持）"));
            }
            catch (Exception ex)
            {
                failed++;
                log("FAILED: " + folder + ": " + ex.Message +
                    (deleting ? "（削除中に失敗したため、一部削除済みの可能性があります）" : "（元データは削除していません）"));
            }
            finally
            {
                CleanupTemporaryDirectory(extractionRoot, extraction, log);
                CleanupTemporaryDirectory(root, temp, log);
            }
        }
        return new BackupResult(succeeded, skipped, failed);
    }

    private static void CheckPrograms(BackupPrograms programs)
    {
        foreach (var path in new[] { programs.SevenZip, programs.Par2 })
            if (!File.Exists(path)) throw new FileNotFoundException("必要なプログラムが見つかりません。", path);
    }

    private static void CheckExistingOutputs(string folder, string archive, string par)
    {
        var prefix = Path.GetFileName(folder) + ".vol";
        if (Path.Exists(archive) || Path.Exists(archive + ".sha256") || Path.Exists(par) ||
            Directory.EnumerateFiles(Path.GetDirectoryName(folder)!).Any(p =>
                Path.GetFileName(p).StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && p.EndsWith(".par2", StringComparison.OrdinalIgnoreCase)))
            throw new IOException("既存の出力があるため、上書きしません。");
    }

    private static string[] OutputFiles(string directory, string name) =>
        Directory.EnumerateFiles(directory).Where(p =>
        {
            var file = Path.GetFileName(p);
            return file.Equals(name + ".7z", StringComparison.OrdinalIgnoreCase) ||
                file.Equals(name + ".7z.sha256", StringComparison.OrdinalIgnoreCase) ||
                file.Equals(name + ".par2", StringComparison.OrdinalIgnoreCase) ||
                (file.StartsWith(name + ".vol", StringComparison.OrdinalIgnoreCase) && file.EndsWith(".par2", StringComparison.OrdinalIgnoreCase));
        }).ToArray();

    private static void PublishReplacement(string root, string folder, string staging, Action<string> log)
    {
        var name = Path.GetFileName(folder);
        var newFiles = OutputFiles(staging, name);
        foreach (var file in newFiles)
        {
            RejectLink(file);
            var destination = Path.Combine(root, Path.GetFileName(file));
            if (Path.Exists(destination)) throw new IOException("Output already exists: " + destination);
        }
        foreach (var file in newFiles)
            File.Move(file, Path.Combine(root, Path.GetFileName(file)));
        log("置換完了: " + folder + ".7z（SHA256・PAR2 も更新）");
    }

    private Task CompressAsync(BackupPrograms programs, string root, string folder, string archive, Action<string> log) =>
        run(programs.SevenZip, ["a", "-t7z", "-mx=9", "-m0=lzma2", "-md=1g", "-ms=on", "-mmt=2", "-bb1", "--", archive, ".\\" + Path.GetFileName(folder)], root, log);

    private Task TestArchiveAsync(BackupPrograms programs, string root, string archive, Action<string> log) =>
        run(programs.SevenZip, ["t", "--", archive], root, log);

    private Task ExtractForVerificationAsync(BackupPrograms programs, string root, string archive, string temp, Action<string> log)
    {
        Directory.CreateDirectory(temp);
        return run(programs.SevenZip, ["x", "-y", "-o" + temp, "--", archive], root, log);
    }

    private async Task CreateRecoveryAsync(BackupPrograms programs, string root, string archive, string par, Action<string> log)
    {
        await run(programs.Par2, ["c", "/rr10", par, archive], root, log).ConfigureAwait(false);
        if (!File.Exists(par)) throw new IOException("PAR2 ファイルが作成されていません。");
    }

    private Task VerifyRecoveryAsync(BackupPrograms programs, string root, string par, Action<string> log) =>
        run(programs.Par2, ["v", par], root, log);

    private static async Task<string> WriteArchiveHashAsync(string archive)
    {
        var hash = await GetArchiveHashAsync(archive).ConfigureAwait(false);
        await File.WriteAllTextAsync(archive + ".sha256", $"{hash.ToLowerInvariant()} *{Path.GetFileName(archive)}\n", new UTF8Encoding(false)).ConfigureAwait(false);
        return hash;
    }

    private static async Task VerifyArchiveHashAsync(string archive, string expected)
    {
        if (await GetArchiveHashAsync(archive).ConfigureAwait(false) != expected)
            throw new IOException("PAR2 処理中にアーカイブが変更されました。");
    }

    private static async Task<string> GetArchiveHashAsync(string archive)
    {
        using var stream = File.OpenRead(archive);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream).ConfigureAwait(false));
    }

    internal static void CheckExtractionSpace(string directory, long sourceBytes)
    {
        var available = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(directory))!).AvailableFreeSpace;
        EnsureExtractionSpace(sourceBytes, available);
    }

    internal static void EnsureExtractionSpace(long sourceBytes, long availableBytes)
    {
        // Leave room for allocation overhead and concurrent writes.
        var required = checked(sourceBytes + Math.Max(64L * 1024 * 1024, sourceBytes / 20));
        if (availableBytes < required)
            throw new IOException($"TEMP の空き容量が不足しています。必要: {required:N0} バイト、空き: {availableBytes:N0} バイト。検証用の一時展開を中止します。");
    }

    private static string[] GetInventory(string root, Action<long>? fileSize = null)
    {
        RestoreFiles.ValidateTree(root);
        return Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories).Select(path =>
        {
            var relative = Path.GetRelativePath(root, path);
            if (Directory.Exists(path)) return "D\t" + relative;
            using var stream = File.OpenRead(path);
            fileSize?.Invoke(stream.Length);
            return "F\t" + relative + "\t" + stream.Length + "\t" + Convert.ToHexString(SHA256.HashData(stream));
        }).Order(StringComparer.Ordinal).ToArray();
    }

    private static void VerifyInventory(string[] expected, string folder, string error)
    {
        if (!expected.SequenceEqual(GetInventory(folder))) throw new IOException(error);
    }

    private static void RejectLink(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("リンク・ジャンクションは処理できません: " + path);
    }

    private static void DeleteSourceContents(string folder)
    {
        RestoreFiles.ValidateTree(folder);
        foreach (var child in Directory.GetFileSystemEntries(folder))
        {
            EnsureImmediateChild(folder, child);
            DeleteEntry(child);
        }
    }

    private static void DeleteEntry(string path)
    {
        RejectLink(path);
        if (Directory.Exists(path))
        {
            foreach (var child in Directory.GetFileSystemEntries(path))
            {
                EnsureImmediateChild(path, child);
                DeleteEntry(child);
            }
            File.SetAttributes(path, File.GetAttributes(path) & ~FileAttributes.ReadOnly);
            Directory.Delete(path);
        }
        else
        {
            File.SetAttributes(path, File.GetAttributes(path) & ~FileAttributes.ReadOnly);
            File.Delete(path);
        }
    }

    private static void EnsureImmediateChild(string parent, string child)
    {
        if (!string.Equals(Path.TrimEndingDirectorySeparator(Path.GetDirectoryName(Path.GetFullPath(child))!),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent)), StringComparison.OrdinalIgnoreCase))
            throw new IOException("対象が指定フォルダの直下にありません: " + child);
    }

    private static void CleanupTemporaryDirectory(string root, string temp, Action<string> log)
    {
        try
        {
            EnsureImmediateChild(root, temp);
            if (!Path.GetFileName(temp).StartsWith(".backup_verify_", StringComparison.Ordinal)) throw new IOException("一時フォルダ名が無効です。");
            if (Directory.Exists(temp)) DeleteEntry(temp);
        }
        catch (Exception ex) { log("一時フォルダを削除できません: " + temp + " / " + ex.Message); }
    }
}
