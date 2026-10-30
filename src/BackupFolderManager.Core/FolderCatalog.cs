namespace BackupFolderManager;

public sealed record FolderItem(string Name, string FolderPath, string Parent, string State,
    IReadOnlyList<string> ArchivePaths, bool HasData)
{
    public string Archives => ArchivePaths.Count == 0 ? "なし" : string.Join(", ", ArchivePaths.Select(Path.GetFileName));
}

public static class FolderCatalog
{
    // Called on demand from a worker thread, never as part of listing folders.
    public static DateTime? GetLatestFileWriteTimeUtc(string folder)
    {
        RestoreFiles.ValidateTree(folder);
        DateTime? latest = null;
        foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
        {
            var time = File.GetLastWriteTimeUtc(file);
            if (latest is null || time > latest.Value) latest = time;
        }
        return latest;
    }

    // Only list the chosen level; never hash or scan file contents here.
    public static IReadOnlyList<FolderItem> Read(string parent, int targetDepth = 1, Action<string>? log = null)
    {
        if (targetDepth is not (1 or 2)) throw new ArgumentOutOfRangeException(nameof(targetDepth));
        parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent));
        if ((File.GetAttributes(parent) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("リンク・ジャンクションは一覧の対象にできません: " + parent);
        if (targetDepth == 2)
        {
            var grandchildren = new List<FolderItem>();
            foreach (var child in Directory.EnumerateDirectories(parent))
            {
                if (Path.GetFileName(child).StartsWith(".backup_verify_", StringComparison.OrdinalIgnoreCase)) continue;
                try { grandchildren.AddRange(Read(child)); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    log?.Invoke("孫フォルダの取得をスキップ: " + child + ": " + ex.Message);
                }
            }
            return grandchildren;
        }
        var folders = Directory.EnumerateDirectories(parent).ToDictionary(p => Path.GetFileName(p), p => p, StringComparer.OrdinalIgnoreCase);
        var archives = Directory.EnumerateFiles(parent)
            .Where(p => Path.GetExtension(p).Equals(".7z", StringComparison.OrdinalIgnoreCase) || Path.GetExtension(p).Equals(".zip", StringComparison.OrdinalIgnoreCase))
            .Where(p => Path.GetFileNameWithoutExtension(p) is not ("" or "." or ".."))
            .GroupBy(p => Path.GetFileNameWithoutExtension(p), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<string>)g.Order(StringComparer.OrdinalIgnoreCase).ToArray(), StringComparer.OrdinalIgnoreCase);
        var result = new List<FolderItem>();
        foreach (var name in folders.Keys.Concat(archives.Keys).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase))
        {
            if (name.StartsWith(".backup_verify_", StringComparison.OrdinalIgnoreCase)) continue;
            var folder = folders.GetValueOrDefault(name) ?? Path.Combine(parent, name);
            var files = archives.GetValueOrDefault(name) ?? [];
            var hasData = false;
            string state;
            try
            {
                if (File.Exists(folder)) state = "同名ファイルと競合";
                else if (folders.ContainsKey(name))
                {
                    if ((File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0) state = "リンク・処理対象外";
                    else
                    {
                        hasData = Directory.EnumerateFileSystemEntries(folder).Any();
                        state = hasData ? (files.Count > 0 ? "展開済み・アーカイブあり（未照合）" : "展開済み")
                            : files.Count > 0 ? "アーカイブ済み" : "空フォルダ";
                    }
                }
                else state = "アーカイブ済み（フォルダなし）";
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                state = "確認失敗: " + ex.Message;
            }
            result.Add(new FolderItem(name, folder, parent, state, files, hasData));
        }
        return result;
    }
}
