namespace BackupFolderManager;

internal sealed record BackupPrograms(string SevenZip, string Par2);

public static class RequiredPrograms
{
    public static string FindSevenZip() => Find("7z.exe", "7-Zip", []);

    internal static BackupPrograms FindForBackup(string root)
    {
        // Report all missing dependencies before creating output or modifying source data.
        var errors = new List<string>();
        string seven = "", par = "";
        try { seven = FindSevenZip(); } catch (FileNotFoundException ex) { errors.Add(ex.Message); }
        try { par = Find("par2j64.exe", "MultiPar", [Path.Combine(root, "par2j64.exe")]); }
        catch (FileNotFoundException ex) { errors.Add(ex.Message); }
        if (errors.Count > 0) throw new FileNotFoundException(string.Join(Environment.NewLine + Environment.NewLine, errors));
        return new BackupPrograms(seven, par);
    }

    private static string Find(string executable, string installedFolder, IEnumerable<string> additionalPaths)
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, executable),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), installedFolder, executable),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), installedFolder, executable)
        }.Concat(additionalPaths).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return candidates.FirstOrDefault(File.Exists) ?? throw new FileNotFoundException(
            $"{executable} が見つかりません。{installedFolder} をインストールするか、exe と同じフォルダに配置してください。\n確認した場所:\n" + string.Join("\n", candidates));
    }
}
