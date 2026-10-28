using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace BackupFolderManager;

public partial class MainWindow : Window
{
    private bool busy;
    private sealed record ArchiveItem(string Name, string FullPath, string Parent, string Size, string Modified);
    private sealed record RootItem(string Path, string Status);
    private readonly ObservableCollection<RootItem> roots = new();
    private readonly FolderSettings settings = new();
    private readonly BackupService backup = new();

    public MainWindow()
    {
        InitializeComponent();
        Roots.ItemsSource = roots;
        Loaded += (_, _) => LoadFolders();
        Closing += (_, e) =>
        {
            if (!busy) return;
            e.Cancel = true;
            MessageBox.Show(this, "処理が完了してから閉じてください。", "処理中");
        };
    }

    private void LoadFolders()
    {
        try
        {
            foreach (var path in settings.Load())
                roots.Add(new RootItem(path, "未確認"));
            RefreshArchives();
            Roots.SelectAll();
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private void SaveFolders()
    {
        settings.Save(roots.Select(r => r.Path));
    }

    private void AddPaths(IEnumerable<string> paths)
    {
        var normalized = paths.Select(input =>
        {
            if (!Path.IsPathFullyQualified(input)) throw new ArgumentException("絶対パスを指定してください。");
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(input));
        }).ToArray();
        foreach (var path in normalized)
            if (!roots.Any(r => string.Equals(r.Path, path, StringComparison.OrdinalIgnoreCase)))
                roots.Add(new RootItem(path, "未確認"));
        SaveFolders();
        RefreshArchives();
        Roots.SelectAll();
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "バックアップ対象の親フォルダを選択", Multiselect = true };
        if (dialog.ShowDialog(this) == true)
            try { AddPaths(dialog.FolderNames); } catch (Exception ex) { ShowError(ex); }
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        try { AddPaths(new[] { RootPath.Text.Trim() }); RootPath.Clear(); }
        catch (Exception ex) { ShowError(ex); }
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            foreach (var item in Roots.SelectedItems.Cast<RootItem>().ToArray()) roots.Remove(item);
            SaveFolders();
            RefreshArchives();
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private void RefreshArchives()
    {
        var selected = Roots.SelectedItems.Cast<RootItem>().Select(r => r.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var archives = new List<ArchiveItem>();
        for (var i = 0; i < roots.Count; i++)
        {
            var path = roots[i].Path;
            var state = "利用可能";
            try
            {
                if (!Directory.Exists(path)) state = "未接続 / 存在しません";
                else archives.AddRange(Directory.EnumerateFiles(path)
                    .Where(p => Path.GetExtension(p).Equals(".7z", StringComparison.OrdinalIgnoreCase) || Path.GetExtension(p).Equals(".zip", StringComparison.OrdinalIgnoreCase))
                    .Select(p => new FileInfo(p))
                    .Select(f => new ArchiveItem(f.Name, f.FullName, path, (f.Length / 1048576d).ToString("N1"), f.LastWriteTime.ToString("yyyy-MM-dd HH:mm"))).ToList());
            }
            catch (Exception ex) { state = "アクセス失敗"; AppendLog(path + ": " + ex.Message); }
            roots[i] = new RootItem(path, state);
        }
        foreach (var item in roots.Where(r => selected.Contains(r.Path))) Roots.SelectedItems.Add(item);
        Archives.ItemsSource = archives.OrderBy(a => a.Parent).ThenBy(a => a.Name).ToList();
    }

    private void Refresh_Click(object sender, RoutedEventArgs e)
    {
        try { RefreshArchives(); } catch (Exception ex) { ShowError(ex); }
    }

    private async void Backup_Click(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        try
        {
            var selected = Roots.SelectedItems.Cast<RootItem>().ToArray();
            if (selected.Length == 0) throw new InvalidOperationException("バックアップする登録フォルダを選択してください。");
            var available = selected.Where(r => Directory.Exists(r.Path)).ToArray();
            if (available.Length == 0) throw new DirectoryNotFoundException("選択したフォルダはすべて未接続、または存在しません。");
            if (DeleteSource.IsChecked == true && MessageBox.Show(this,
                "検証に成功した各フォルダの中身を削除します。実行しますか？\n" + string.Join("\n", available.Select(r => r.Path)),
                "元データの削除確認", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            SetBusy(true);
            var failed = false;
            var deleteSource = DeleteSource.IsChecked == true;
            var progress = new Progress<string>(AppendLog);
            foreach (var missing in selected.Where(r => !available.Contains(r))) AppendLog("未接続のためスキップ: " + missing.Path);
            foreach (var root in available)
            {
                try
                {
                    AppendLog("対象: " + root.Path);
                    var result = await Task.Run(() => backup.BackupAsync(root.Path, deleteSource, progress));
                    failed |= result.Failed > 0;
                    AppendLog($"成功: {result.Succeeded} / スキップ: {result.Skipped} / 失敗: {result.Failed}");
                }
                catch (Exception ex) { failed = true; AppendLog(root.Path + ": " + ex.Message); }
            }
            RefreshArchives();
            Status.Text = failed ? "一部のバックアップに失敗（ログを確認）" : "バックアップ完了";
        }
        catch (Exception ex) { ShowError(ex); }
        finally { SetBusy(false); }
    }

    private async void Extract_Click(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        if (Archives.SelectedItem is not ArchiveItem archive) { MessageBox.Show(this, "アーカイブを選択してください。"); return; }
        try
        {
            var name = Path.GetFileNameWithoutExtension(archive.FullPath);
            if (string.IsNullOrWhiteSpace(name) || name is "." or "..") throw new IOException("展開先のフォルダ名が無効です。");
            var destination = Path.Combine(archive.Parent, name);
            if (File.Exists(destination)) throw new IOException("展開先と同名のファイルがあります。");
            if (Directory.Exists(destination)) RestoreFiles.ValidateTree(destination);
            var action = ExistingAction.Overwrite;
            if (Directory.Exists(destination) && Directory.EnumerateFileSystemEntries(destination).Any()) action = ChooseExistingAction(destination);
            if (action == ExistingAction.Cancel) return;
            SetBusy(true);
            var seven = RequiredPrograms.FindSevenZip();
            var hashPath = archive.FullPath + ".sha256";
            if (File.Exists(hashPath))
            {
                AppendLog("SHA256 を確認中...");
                var expected = (await File.ReadAllTextAsync(hashPath)).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                using var stream = File.OpenRead(archive.FullPath);
                var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream));
                if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("SHA256 が一致しません。展開を中止しました。");
            }
            var staging = Path.Combine(archive.Parent, ".backup_verify_restore_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(staging);
            try
            {
                AppendLog("一時展開先: " + staging);
                await RunAsync(seven, new[] { "x", "-y", "-o" + staging, "--", archive.FullPath });
                RestoreFiles.ValidateTree(staging);
                var content = RestoreFiles.ContentRoot(staging, name);
                if (action == ExistingAction.Compare)
                {
                    var identical = await Task.Run(() => RestoreFiles.AreIdentical(content, destination));
                    Status.Text = identical ? "同一性チェック: 一致" : "同一性チェック: 不一致";
                    MessageBox.Show(this, identical ? "フォルダ構成と全ファイルの SHA256 が一致しました。" : "フォルダ構成またはファイル内容が異なります。変更は行っていません。", "同一性チェック");
                    if (!identical) return;
                }
                else
                {
                    AppendLog("復元先: " + destination);
                    await Task.Run(() => RestoreFiles.CopyInto(content, destination));
                    Status.Text = "展開完了";
                }
                Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { destination }, UseShellExecute = true });
            }
            finally
            {
                var fullStaging = Path.GetFullPath(staging);
                if (string.Equals(Path.TrimEndingDirectorySeparator(Path.GetDirectoryName(fullStaging)!), Path.TrimEndingDirectorySeparator(Path.GetFullPath(archive.Parent)), StringComparison.OrdinalIgnoreCase) &&
                    Path.GetFileName(fullStaging).StartsWith(".backup_verify_restore_", StringComparison.Ordinal) && Directory.Exists(fullStaging))
                {
                    try { Directory.Delete(fullStaging, recursive: true); }
                    catch (Exception ex) { AppendLog("一時フォルダを削除できません: " + staging + " / " + ex.Message); }
                }
            }
        }
        catch (Exception ex) { ShowError(ex); }
        finally { SetBusy(false); }
    }

    private enum ExistingAction { Cancel, Overwrite, Compare }

    private ExistingAction ChooseExistingAction(string destination)
    {
        var result = ExistingAction.Cancel;
        var dialog = new Window { Owner = this, Title = "展開先にデータがあります", Width = 600, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize };
        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(new TextBlock { Text = destination + "\n\n上書き: 同名ファイルを置き換えます。展開先にだけあるデータは残します。\n同一性チェック: 構成と SHA256 を比較します。既存データは変更しません。", TextWrapping = TextWrapping.Wrap });
        var buttons = new WrapPanel { Margin = new Thickness(0, 16, 0, 0) };
        foreach (var (label, action) in new[] { ("上書きする", ExistingAction.Overwrite), ("同一性チェック", ExistingAction.Compare), ("何もしない", ExistingAction.Cancel) })
        {
            var button = new Button { Content = label, Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(0, 0, 8, 0), IsCancel = action == ExistingAction.Cancel };
            button.Click += (_, _) => { result = action; dialog.Close(); };
            buttons.Children.Add(button);
        }
        panel.Children.Add(buttons);
        dialog.Content = panel;
        dialog.ShowDialog();
        return result;
    }

    private async Task RunAsync(string executable, IEnumerable<string> arguments)
    {
        IProgress<string> progress = new Progress<string>(AppendLog);
        await ProgramRunner.RunAsync(executable, arguments.ToArray(), AppContext.BaseDirectory, progress.Report);
    }

    private void AppendLog(string message) { Log.AppendText(message + Environment.NewLine); Log.ScrollToEnd(); }
    private void ShowError(Exception ex) { Status.Text = "エラー"; AppendLog(ex.Message); MessageBox.Show(this, ex.Message, "エラー", MessageBoxButton.OK, MessageBoxImage.Error); }
    private void SetBusy(bool value)
    {
        busy = value;
        BrowseButton.IsEnabled = BackupButton.IsEnabled = RefreshButton.IsEnabled = ExtractButton.IsEnabled = RootPath.IsEnabled = DeleteSource.IsEnabled = Roots.IsEnabled = AddButton.IsEnabled = RemoveButton.IsEnabled = !value;
        if (value) { Log.Clear(); Status.Text = "処理中..."; }
    }
}
