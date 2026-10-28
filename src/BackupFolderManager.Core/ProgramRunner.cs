using System.Diagnostics;

namespace BackupFolderManager;

internal static class ProgramRunner
{
    public static async Task RunAsync(string executable, IReadOnlyList<string> arguments, string workingDirectory, Action<string>? log)
    {
        if (!File.Exists(executable)) throw new FileNotFoundException("必要なプログラムが見つかりません。", executable);
        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = start };
        process.Start();
        async Task ReadAsync(StreamReader reader)
        {
            while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line) log?.Invoke(line);
        }
        await Task.WhenAll(ReadAsync(process.StandardOutput), ReadAsync(process.StandardError), process.WaitForExitAsync()).ConfigureAwait(false);
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"{Path.GetFileName(executable)} が失敗しました (終了コード {process.ExitCode})。");
    }
}
