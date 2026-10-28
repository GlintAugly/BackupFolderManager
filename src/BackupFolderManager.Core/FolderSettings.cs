using System.Text.Json;

namespace BackupFolderManager;

public sealed class FolderSettings
{
    public string FilePath { get; }
    private readonly string? legacyPath;

    public FolderSettings() : this(Path.Combine(AppContext.BaseDirectory, "settings.json"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BackupFolderManager", "folders.json")) { }

    internal FolderSettings(string filePath, string? legacyPath = null)
    {
        FilePath = filePath;
        this.legacyPath = legacyPath;
    }

    public string[] Load()
    {
        if (File.Exists(FilePath))
            return JsonSerializer.Deserialize<string[]>(File.ReadAllText(FilePath)) ?? [];
        if (legacyPath is null || !File.Exists(legacyPath)) return [];
        var paths = JsonSerializer.Deserialize<string[]>(File.ReadAllText(legacyPath)) ?? [];
        Save(paths);
        return paths;
    }

    public void Save(IEnumerable<string> paths)
    {
        // Existence is deliberately not checked: disconnected drives remain registered.
        var temp = FilePath + ".tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(paths.ToArray(), new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temp, FilePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new IOException("設定を保存できません。exe のフォルダへの書き込み権限を確認してください。\n" + FilePath, ex);
        }
    }
}
