using System.IO;
using System.Security.Cryptography;

namespace BackupFolderManager;

// Restores only regular files/directories; junctions and symbolic links are rejected.
internal static class RestoreFiles
{
    public static string ContentRoot(string staging, string name)
    {
        var entries = Directory.GetFileSystemEntries(staging);
        return entries.Length == 1 && Directory.Exists(entries[0]) &&
            string.Equals(Path.GetFileName(entries[0]), name, StringComparison.OrdinalIgnoreCase)
            ? entries[0] : staging;
    }

    public static void ValidateTree(string path)
    {
        if (!Directory.Exists(path)) throw new DirectoryNotFoundException(path);
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("リンクを含むフォルダには展開できません: " + path);
        foreach (var entry in Directory.EnumerateFileSystemEntries(path))
        {
            if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("リンクは処理できません: " + entry);
            if (Directory.Exists(entry)) ValidateTree(entry);
        }
    }

    public static bool AreIdentical(string source, string destination)
    {
        ValidateTree(source);
        ValidateTree(destination);
        return Inventory(source).SequenceEqual(Inventory(destination));
    }

    private static IEnumerable<string> Inventory(string root)
    {
        return Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories)
            .Select(path =>
            {
                var relative = Path.GetRelativePath(root, path);
                if (Directory.Exists(path)) return "D\t" + relative;
                using var stream = File.OpenRead(path);
                return "F\t" + relative + "\t" + stream.Length + "\t" + Convert.ToHexString(SHA256.HashData(stream));
            }).Order(StringComparer.Ordinal);
    }

    public static void CopyInto(string source, string destination)
    {
        ValidateTree(source);
        if (Directory.Exists(destination)) ValidateTree(destination);
        // Detect file/directory conflicts before any writes.
        foreach (var entry in Directory.EnumerateFileSystemEntries(source, "*", SearchOption.AllDirectories).Prepend(source))
        {
            var target = Path.Combine(destination, Path.GetRelativePath(source, entry));
            if (Directory.Exists(entry) ? File.Exists(target) : Directory.Exists(target))
                throw new IOException("ファイルとフォルダが競合しています: " + target);
        }
        Directory.CreateDirectory(destination);
        foreach (var dir in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, dir)));
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(destination, Path.GetRelativePath(source, file)), overwrite: true);
    }
}
