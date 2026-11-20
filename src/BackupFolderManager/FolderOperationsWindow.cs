using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace BackupFolderManager;

public partial class MainWindow
{
    private sealed record OperationChoice(string Label, FolderOperation Operation, ExistingAction Action = ExistingAction.Overwrite);
    private sealed record FolderPlan(FolderItem Folder, FolderOperation Operation, string? Archive, ExistingAction Action);

    private async Task<FolderPlan[]?> ChooseFolderOperationsAsync(FolderItem[] folders)
    {
        var skip = new OperationChoice("何もしない", FolderOperation.Skip);
        var archive = new OperationChoice("圧縮", FolderOperation.Archive);
        var extract = new OperationChoice("展開（既存ファイルを上書き）", FolderOperation.Extract);
        var compare = new OperationChoice("同一性チェック（変更しない）", FolderOperation.Extract, ExistingAction.Compare);
        var replace = new OperationChoice("再圧縮（.7z を置換）", FolderOperation.ReplaceArchive);
        Status.Text = "更新日時を確認中...";
        var timestamps = await Task.Run(() => folders.Select(folder =>
        {
            if (!folder.HasData || folder.ArchivePaths.Count == 0) return "";
            try
            {
                var latest = FolderCatalog.GetLatestFileWriteTimeUtc(folder.FolderPath);
                var path = folder.FolderPath + ".7z";
                return "展開: " + (latest?.ToLocalTime().ToString("yyyy/MM/dd HH:mm:ss") ?? "ファイルなし") +
                    " / .7z: " + (File.Exists(path) ? File.GetLastWriteTime(path).ToString("yyyy/MM/dd HH:mm:ss") : "なし（ZIP は保持）");
            }
            catch (Exception ex) { return "日時を取得できません: " + ex.Message; }
        }).ToArray());

        var dialog = new Window
        {
            Owner = this, Title = "選択フォルダの操作をまとめて指定", Width = 1100, Height = 650,
            MinWidth = 750, MinHeight = 400, WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        var panel = new DockPanel { Margin = new Thickness(16) };
        var header = new StackPanel();
        DockPanel.SetDock(header, Dock.Top);
        header.Children.Add(new TextBlock { Text = $"対象: {folders.Length} フォルダ。一括指定後も、各行の操作・展開元を変更できます。", TextWrapping = TextWrapping.Wrap });
        var bulk = new WrapPanel { Margin = new Thickness(0, 10, 0, 10) };
        header.Children.Add(bulk);
        panel.Children.Add(header);
        var footer = new StackPanel();
        DockPanel.SetDock(footer, Dock.Bottom);
        footer.Children.Add(new TextBlock
        {
            Text = "上書きは同名ファイルを置換し、展開先にだけあるデータは残します。\n再圧縮は旧 .7z・SHA256・PAR2 を先に削除します。旧出力は復元できません。ZIP は保持します。\n日時は目安です。削除・改名や日時を保持した変更は検出できません。",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 8)
        });
        var delete = new TextBlock
        {
            Text = DeleteSource.IsChecked == true ? "圧縮・再圧縮は検証成功後に元フォルダの中身を削除します。" : "圧縮・再圧縮後も元データを保持します。",
            TextWrapping = TextWrapping.Wrap
        };
        footer.Children.Add(delete);
        var buttons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        var run = new Button { Content = "この内容で実行", Padding = new Thickness(16, 6, 16, 6) };
        var cancel = new Button { Content = "キャンセル", IsCancel = true, Padding = new Thickness(16, 6, 16, 6), Margin = new Thickness(8, 0, 0, 0) };
        buttons.Children.Add(run);
        buttons.Children.Add(cancel);
        footer.Children.Add(buttons);
        panel.Children.Add(footer);

        var rows = new StackPanel();
        var controls = new List<(FolderItem Folder, ComboBox Operation, ComboBox Archive, OperationChoice[] Choices)>();
        for (var i = 0; i < folders.Length; i++)
        {
            var folder = folders[i];
            OperationChoice[] choices = folder.ArchivePaths.Count == 0
                ? (folder.HasData ? [skip, archive] : [skip])
                : (folder.HasData ? [skip, compare, extract, replace] : [skip, extract]);
            var row = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
            row.Children.Add(new TextBlock { Text = folder.FolderPath, FontWeight = FontWeights.Bold, TextWrapping = TextWrapping.Wrap });
            row.Children.Add(new TextBlock { Text = folder.State + "  " + timestamps[i], TextWrapping = TextWrapping.Wrap });
            var inputs = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
            var operation = new ComboBox { ItemsSource = choices, DisplayMemberPath = nameof(OperationChoice.Label), Width = 260, SelectedIndex = folder.HasData && folder.ArchivePaths.Count > 0 ? 0 : choices.Length - 1 };
            var source = new ComboBox { ItemsSource = folder.ArchivePaths, SelectedIndex = 0, MinWidth = 200, MaxWidth = 650, Margin = new Thickness(8, 0, 0, 0), ToolTip = "展開・同一性チェックで使うアーカイブ" };
            operation.SelectionChanged += (_, _) => source.IsEnabled = (operation.SelectedItem as OperationChoice)?.Operation == FolderOperation.Extract;
            source.IsEnabled = (operation.SelectedItem as OperationChoice)?.Operation == FolderOperation.Extract;
            inputs.Children.Add(operation);
            inputs.Children.Add(source);
            row.Children.Add(inputs);
            rows.Children.Add(row);
            controls.Add((folder, operation, source, choices));
        }
        foreach (var choice in new[] { compare, extract, replace, archive, skip })
        {
            var button = new Button { Content = "全行: " + choice.Label, Padding = new Thickness(8, 5, 8, 5), Margin = new Thickness(0, 0, 6, 6) };
            button.Click += (_, _) =>
            {
                foreach (var row in controls)
                    if (row.Choices.Contains(choice)) row.Operation.SelectedItem = choice;
            };
            bulk.Children.Add(button);
        }
        header.Children.Add(new TextBlock { Text = "一括指定は、その操作を選べる行に適用します。それ以外の行は現在の指定を維持します。", TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new ScrollViewer { Content = rows, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        run.Click += (_, _) => dialog.DialogResult = true;
        dialog.Content = panel;
        Status.Text = "操作をまとめて指定してください";
        if (dialog.ShowDialog() != true) return null;
        return controls.Select(row =>
        {
            var choice = (OperationChoice)row.Operation.SelectedItem;
            return new FolderPlan(row.Folder, choice.Operation, row.Archive.SelectedItem as string, choice.Action);
        }).ToArray();
    }
}
