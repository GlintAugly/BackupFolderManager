using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32;

namespace BackupFolderManager;

public partial class MainWindow : Window
{
    private bool busy;
    private sealed record RootItem(string Path, string Status);
    private readonly ObservableCollection<RootItem> roots = new();
    private readonly FolderSettings settings = new();
    private readonly BackupService backup = new();
    private const int MaxLogCharacters = 64 * 1024;
    private readonly object logGate = new();
    private readonly StringBuilder pendingLog = new();
    private readonly DispatcherTimer logTimer;
    private StreamWriter? logFile;

    // Unlike Progress<T>, this does not post one Dispatcher operation per line.
    private sealed class LogProgress(Action<string> report) : IProgress<string>
    {
        public void Report(string value) => report(value);
    }

    public MainWindow()
    {
        InitializeComponent();
        logTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(250)
        };
        logTimer.Tick += (_, _) => FlushLog();
        logTimer.Start();
        try
        {
            var directory = Path.Combine(AppContext.BaseDirectory, "logs");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, $"{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.log");
            logFile = new StreamWriter(path, append: false, Encoding.UTF8);
            AppendLog("ログ保存先: " + path);
            Log.ToolTip = "画面には最新64K文字を表示します。完全なログの保存先: " + path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppendLog("ログファイルを作成できません（画面表示のみ）: " + ex.Message);
        }
        Closed += (_, _) =>
        {
            logTimer.Stop();
            FlushLog();
            lock (logGate) { logFile?.Dispose(); logFile = null; }
        };
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
            RefreshFolders();
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
        RefreshFolders();
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
            RefreshFolders();
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private void RefreshFolders()
    {
        var selected = Roots.SelectedItems.Cast<RootItem>().Select(r => r.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var selectedFolders = Folders.SelectedItems.Cast<FolderItem>().Select(f => f.FolderPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var folders = new List<FolderItem>();
        for (var i = 0; i < roots.Count; i++)
        {
            var path = roots[i].Path;
            var state = "利用可能";
            try
            {
                if (!Directory.Exists(path)) state = "未接続 / 存在しません";
                else folders.AddRange(FolderCatalog.Read(path));
            }
            catch (Exception ex) { state = "アクセス失敗"; AppendLog(path + ": " + ex.Message); }
            roots[i] = new RootItem(path, state);
        }
        foreach (var item in roots.Where(r => selected.Contains(r.Path))) Roots.SelectedItems.Add(item);
        var items = folders.OrderBy(f => f.Parent).ThenBy(f => f.Name).ToList();
        Folders.ItemsSource = items;
        foreach (var item in items.Where(f => selectedFolders.Contains(f.FolderPath))) Folders.SelectedItems.Add(item);
    }

    private void Refresh_Click(object sender, RoutedEventArgs e)
    {
        try { RefreshFolders(); } catch (Exception ex) { ShowError(ex); }
    }

    private async void Backup_Click(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        try
        {
            var parents = Roots.SelectedItems.Cast<RootItem>().ToArray();
            if (parents.Length == 0) throw new InvalidOperationException("上の登録一覧でアーカイブ対象の親フォルダを選択してください。");
            SetBusy(true);
            var targets = new List<FolderItem>();
            foreach (var parent in parents)
            {
                if (!Directory.Exists(parent.Path)) { AppendLog("未接続のためスキップ: " + parent.Path); continue; }
                try { targets.AddRange(FolderCatalog.Read(parent.Path)); }
                catch (Exception ex) { AppendLog(parent.Path + ": " + ex.Message); }
            }
            var success = await ArchiveFoldersAsync(targets.ToArray(), parents.Select(p => p.Path).ToArray());
            RefreshFolders();
            Status.Text = success ? "アーカイブ完了" : "中止・スキップまたは失敗（ログを確認）";
        }
        catch (Exception ex) { ShowError(ex); }
        finally { SetBusy(false); }
    }

    private async Task<bool> ArchiveFoldersAsync(FolderItem[] selected, IReadOnlyList<string>? confirmationParents = null)
    {
        var available = selected.Where(f => f.HasData && f.ArchivePaths.Count == 0).ToArray();
        if (available.Length == 0) throw new InvalidOperationException("アーカイブできるフォルダがありません。データがあり、同名アーカイブがないフォルダを選択してください。既存アーカイブは上書きしません。");
        if (DeleteSource.IsChecked == true && MessageBox.Show(this,
            "検証に成功した各フォルダの中身を削除します。実行しますか？\n" +
            (confirmationParents is null
                ? string.Join("\n", available.Select(f => f.FolderPath))
                : "対象の親フォルダ（直下のフォルダを処理）:\n" + string.Join("\n", confirmationParents)),
            "元データの削除確認", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return false;
        var failed = false;
        var deleteSource = DeleteSource.IsChecked == true;
        var progress = new LogProgress(AppendLog);
        foreach (var skipped in selected.Where(f => !available.Contains(f))) AppendLog("アーカイブ対象外のためスキップ: " + skipped.FolderPath + " / " + skipped.State);
        foreach (var group in available.GroupBy(f => f.Parent, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                AppendLog("保存先: " + group.Key);
                var targets = group.Select(f => f.FolderPath).ToArray();
                var result = await Task.Run(() => backup.BackupAsync(group.Key, deleteSource, progress, targets));
                failed |= result.Failed > 0;
                AppendLog($"成功: {result.Succeeded} / スキップ: {result.Skipped} / 失敗: {result.Failed}");
            }
            catch (Exception ex) { failed = true; AppendLog(group.Key + ": " + ex.Message); }
        }
        return !failed;
    }

    private async void Extract_Click(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        var selected = Folders.SelectedItems.Cast<FolderItem>().ToArray();
        if (selected.Length == 0) { MessageBox.Show(this, "下の一覧で展開／圧縮するフォルダを選択してください。"); return; }
        try
        {
            SetBusy(true);
            var completed = 0;
            var skipped = 0;
            var failed = 0;
            foreach (var folder in selected)
            {
                try
                {
                    var operation = ChooseFolderOperation(folder);
                    if (operation == FolderOperation.Skip)
                    {
                        skipped++;
                        AppendLog("スキップ: " + folder.FolderPath);
                    }
                    else if (operation == FolderOperation.Archive ? await ArchiveFoldersAsync([folder]) : await ExtractFolderAsync(folder)) completed++;
                    else skipped++;
                }
                catch (Exception ex) { failed++; AppendLog(folder.FolderPath + ": " + ex.Message); }
            }
            RefreshFolders();
            Status.Text = $"展開・圧縮完了: {completed} / スキップ・不一致: {skipped} / 失敗: {failed}";
        }
        catch (Exception ex) { ShowError(ex); }
        finally { SetBusy(false); }
    }

    private enum FolderOperation { Skip, Archive, Extract }

    private FolderOperation ChooseFolderOperation(FolderItem folder)
    {
        if (folder.ArchivePaths.Count == 0) return folder.HasData ? FolderOperation.Archive : FolderOperation.Skip;
        if (!folder.HasData) return FolderOperation.Extract;
        var result = FolderOperation.Skip;
        var dialog = new Window { Owner = this, Title = "フォルダの操作を選択", Width = 600, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize };
        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(new TextBlock { Text = folder.FolderPath + "\n展開データとアーカイブの両方があります。操作を選択してください。\n圧縮時は既存アーカイブを上書きしません。", TextWrapping = TextWrapping.Wrap });
        var buttons = new WrapPanel { Margin = new Thickness(0, 16, 0, 0) };
        foreach (var (label, operation) in new[] { ("展開・同一性チェック", FolderOperation.Extract), ("圧縮", FolderOperation.Archive), ("何もしない", FolderOperation.Skip) })
        {
            var button = new Button { Content = label, Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(0, 0, 8, 0), IsCancel = operation == FolderOperation.Skip,
                IsEnabled = operation != FolderOperation.Archive || folder.ArchivePaths.Count == 0,
                ToolTip = operation == FolderOperation.Archive ? "既存の同名アーカイブがあるため、圧縮はできません。" : null };
            button.Click += (_, _) => { result = operation; dialog.Close(); };
            buttons.Children.Add(button);
        }
        panel.Children.Add(buttons);
        dialog.Content = panel;
        dialog.ShowDialog();
        return result;
    }

    private async Task<bool> ExtractFolderAsync(FolderItem folder)
    {
        var archive = ChooseArchive(folder);
        if (archive is null) return false;
        var name = folder.Name;
        if (string.IsNullOrWhiteSpace(name) || name is "." or "..") throw new IOException("展開先のフォルダ名が無効です。");
        var destination = folder.FolderPath;
        if (File.Exists(destination)) throw new IOException("展開先と同名のファイルがあります。");
        if (Directory.Exists(destination)) RestoreFiles.ValidateTree(destination);
        var action = ExistingAction.Overwrite;
        if (Directory.Exists(destination) && Directory.EnumerateFileSystemEntries(destination).Any()) action = ChooseExistingAction(destination);
        if (action == ExistingAction.Cancel) return false;
        var seven = RequiredPrograms.FindSevenZip();
        var hashPath = archive + ".sha256";
        if (File.Exists(hashPath))
        {
            AppendLog("SHA256 を確認中...");
            var expected = (await File.ReadAllTextAsync(hashPath)).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            using var stream = File.OpenRead(archive);
            var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream));
            if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("SHA256 が一致しません。展開を中止しました。");
        }
        var staging = Path.Combine(folder.Parent, ".backup_verify_restore_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            AppendLog("一時展開先: " + staging);
            await RunAsync(seven, new[] { "x", "-y", "-o" + staging, "--", archive });
            RestoreFiles.ValidateTree(staging);
            var content = RestoreFiles.ContentRoot(staging, name);
            if (action == ExistingAction.Compare)
            {
                var identical = await Task.Run(() => RestoreFiles.AreIdentical(content, destination));
                Status.Text = identical ? "同一性チェック: 一致" : "同一性チェック: 不一致";
                MessageBox.Show(this, identical ? "フォルダ構成と全ファイルの SHA256 が一致しました。" : "フォルダ構成またはファイル内容が異なります。変更は行っていません。", "同一性チェック");
                if (!identical) return false;
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
            if (string.Equals(Path.TrimEndingDirectorySeparator(Path.GetDirectoryName(fullStaging)!), Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder.Parent)), StringComparison.OrdinalIgnoreCase) &&
                Path.GetFileName(fullStaging).StartsWith(".backup_verify_restore_", StringComparison.Ordinal) && Directory.Exists(fullStaging))
            {
                try { Directory.Delete(fullStaging, recursive: true); }
                catch (Exception ex) { AppendLog("一時フォルダを削除できません: " + staging + " / " + ex.Message); }
            }
        }
        return true;
    }

    private string? ChooseArchive(FolderItem folder)
    {
        if (folder.ArchivePaths.Count == 1) return folder.ArchivePaths[0];
        var dialog = new Window { Owner = this, Title = "展開するアーカイブを選択", Width = 600, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize };
        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(new TextBlock { Text = folder.FolderPath + "\n同名アーカイブが複数あります。展開するファイルを選択してください。", TextWrapping = TextWrapping.Wrap });
        var list = new ComboBox { ItemsSource = folder.ArchivePaths, SelectedIndex = 0, Margin = new Thickness(0, 12, 0, 12) };
        panel.Children.Add(list);
        var buttons = new WrapPanel();
        var select = new Button { Content = "選択", Padding = new Thickness(16, 6, 16, 6), Margin = new Thickness(0, 0, 8, 0) };
        select.Click += (_, _) => dialog.DialogResult = true;
        buttons.Children.Add(select);
        buttons.Children.Add(new Button { Content = "キャンセル", IsCancel = true, Padding = new Thickness(16, 6, 16, 6) });
        panel.Children.Add(buttons);
        dialog.Content = panel;
        return dialog.ShowDialog() == true ? list.SelectedItem as string : null;
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
        IProgress<string> progress = new LogProgress(AppendLog);
        await ProgramRunner.RunAsync(executable, arguments.ToArray(), AppContext.BaseDirectory, progress.Report);
    }

    private void AppendLog(string message)
    {
        lock (logGate)
        {
            try { logFile?.WriteLine(message); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                try { logFile?.Dispose(); } catch (IOException) { }
                logFile = null;
                BufferLog("ログファイルへの保存に失敗しました（以降は画面表示のみ）: " + ex.Message);
            }
            BufferLog(message);
        }
    }

    // Keep the pending display buffer bounded even when the UI cannot keep up.
    private void BufferLog(string message)
    {
        if (message.Length > MaxLogCharacters) message = message[^MaxLogCharacters..];
        pendingLog.Append(message).AppendLine();
        if (pendingLog.Length > MaxLogCharacters)
            pendingLog.Remove(0, pendingLog.Length - MaxLogCharacters);
    }

    private void FlushLog()
    {
        string text;
        lock (logGate)
        {
            try { logFile?.Flush(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                try { logFile?.Dispose(); } catch (IOException) { }
                logFile = null;
                BufferLog("ログファイルへの保存に失敗しました（以降は画面表示のみ）: " + ex.Message);
            }
            if (pendingLog.Length == 0) return;
            text = pendingLog.ToString();
            pendingLog.Clear();
        }
        var retained = Log.Text;
        var keep = Math.Min(retained.Length, MaxLogCharacters - text.Length);
        Log.Text = retained[(retained.Length - keep)..] + text;
        Log.ScrollToEnd();
    }
    private void ShowError(Exception ex) { Status.Text = "エラー"; AppendLog(ex.Message); MessageBox.Show(this, ex.Message, "エラー", MessageBoxButton.OK, MessageBoxImage.Error); }
    private void SetBusy(bool value)
    {
        busy = value;
        BrowseButton.IsEnabled = BackupButton.IsEnabled = RefreshButton.IsEnabled = ExtractButton.IsEnabled = RootPath.IsEnabled = DeleteSource.IsEnabled = Roots.IsEnabled = Folders.IsEnabled = AddButton.IsEnabled = RemoveButton.IsEnabled = !value;
        if (value)
        {
            FlushLog();
            Log.Clear();
            Status.Text = "処理中...";
        }
        else FlushLog();
    }
}
