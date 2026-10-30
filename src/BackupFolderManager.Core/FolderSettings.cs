using System.Text.Json;

namespace BackupFolderManager;

public sealed record FolderRegistration(string Path, int TargetDepth = 1);

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

    public string[] Load() => LoadRegistrations().Select(r => r.Path).ToArray();

    public FolderRegistration[] LoadRegistrations()
    {
        if (File.Exists(FilePath))
            return ReadRegistrations(FilePath);
        if (legacyPath is null || !File.Exists(legacyPath)) return [];
        var registrations = ReadRegistrations(legacyPath);
        SaveRegistrations(registrations);
        return registrations;
    }

    private static FolderRegistration[] ReadRegistrations(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        if (document.RootElement.ValueKind == JsonValueKind.Null) return [];
        var registrations = document.RootElement.EnumerateArray().Select(item =>
            item.ValueKind == JsonValueKind.String
                ? new FolderRegistration(item.GetString()!)
                : item.Deserialize<FolderRegistration>() ?? throw new JsonException("登録設定が空です。")).ToArray();
        Validate(registrations);
        return registrations;
    }

    private static void Validate(IEnumerable<FolderRegistration> registrations)
    {
        foreach (var registration in registrations)
            if (string.IsNullOrWhiteSpace(registration.Path) || registration.TargetDepth is not (1 or 2))
                throw new ArgumentException("登録にはパスと対象階層（1: 直下、2: 孫）を指定してください。");
    }

    public void Save(IEnumerable<string> paths) => SaveRegistrations(paths.Select(path => new FolderRegistration(path)));

    public void SaveRegistrations(IEnumerable<FolderRegistration> registrations)
    {
        // Existence is deliberately not checked: disconnected drives remain registered.
        var items = registrations.ToArray();
        Validate(items);
        var temp = FilePath + ".tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(items, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temp, FilePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new IOException("設定を保存できません。exe のフォルダへの書き込み権限を確認してください。\n" + FilePath, ex);
        }
    }
}
