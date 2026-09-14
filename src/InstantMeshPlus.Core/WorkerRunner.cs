using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace InstantMeshPlus.Core;

public sealed record WorkerOptions
{
    public string? ExecutablePath { get; init; }
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(10);
    public string? TemporaryRoot { get; init; }
    public bool KeepFiles { get; init; }
}

public sealed class WorkerRunner
{
    public async Task<RemeshResult> RunAsync(
        RemeshRequest request,
        WorkerOptions? options = null,
        IProgress<WorkerProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        options ??= new WorkerOptions();
        var executable = WorkerLocator.Resolve(options.ExecutablePath);
        var root = options.TemporaryRoot ?? Path.GetTempPath();
        var directory = Path.Combine(root, $"InstantMeshPlus-{Guid.NewGuid():N}");
        WorkerFiles? files = null;
        try
        {
            files = WorkerProtocol.WriteRequest(request, directory);
            var startInfo = new ProcessStartInfo
            {
                FileName = executable,
                WorkingDirectory = directory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            startInfo.ArgumentList.Add("--request");
            startInfo.ArgumentList.Add(files.ManifestPath);
            using var process = new Process { StartInfo = startInfo };
            var error = new StringBuilder();
            process.ErrorDataReceived += (_, args) =>
            {
                if (!string.IsNullOrWhiteSpace(args.Data))
                    error.AppendLine(args.Data);
            };
            process.OutputDataReceived += (_, args) =>
            {
                if (!string.IsNullOrWhiteSpace(args.Data) &&
                    TryReadProgress(args.Data, out var update))
                    progress?.Report(update);
            };
            if (!process.Start())
                throw new WorkerException("The native worker could not be started.");
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            using var timeout = new CancellationTokenSource(options.Timeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                timeout.Token);
            try
            {
                await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                TryKill(process);
                if (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                    throw new TimeoutException(
                        $"The native worker exceeded its {options.Timeout.TotalSeconds.ToString(CultureInfo.InvariantCulture)} second timeout.");
                throw;
            }

            if (process.ExitCode != 0)
            {
                if (File.Exists(files.ResultManifestPath))
                    return WorkerProtocol.ReadResult(files);
                throw new WorkerException(
                    $"The native worker exited with code {process.ExitCode}: {error.ToString().Trim()}");
            }
            return WorkerProtocol.ReadResult(files);
        }
        finally
        {
            if (!options.KeepFiles && Directory.Exists(directory))
            {
                try
                {
                    Directory.Delete(directory, recursive: true);
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }
    }

    private static bool TryReadProgress(string line, out WorkerProgress update)
    {
        update = default!;
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            update = new WorkerProgress(
                root.GetProperty("phase").GetString() ?? string.Empty,
                root.GetProperty("progress").GetSingle());
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (KeyNotFoundException)
        {
            return false;
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
        }
    }
}

public static class WorkerLocator
{
    // Kept for source compatibility with existing Windows callers.
    public const string ExecutableName = "InstantMeshPlus.Worker.exe";
    internal static string PlatformExecutableName => ExecutableNameFor(OperatingSystem.IsWindows());

    internal static string ExecutableNameFor(bool isWindows) =>
        isWindows ? ExecutableName : "InstantMeshPlus.Worker";

    public static string Resolve(string? explicitPath = null)
    {
        var executableName = PlatformExecutableName;
        var assemblyDirectory = Path.GetDirectoryName(typeof(WorkerLocator).Assembly.Location);
        var candidates = new[]
        {
            explicitPath,
            Environment.GetEnvironmentVariable("INSTANT_MESH_PLUS_WORKER"),
            string.IsNullOrWhiteSpace(assemblyDirectory)
                ? null
                : Path.Combine(assemblyDirectory, executableName),
            Path.Combine(AppContext.BaseDirectory, executableName)
        };
        foreach (var candidate in candidates.Where(c => !string.IsNullOrWhiteSpace(c)))
        {
            var fullPath = Path.GetFullPath(candidate!);
            if (File.Exists(fullPath))
                return fullPath;
        }
        throw new FileNotFoundException(
            $"Could not locate {executableName}. Place it beside the plugin, set INSTANT_MESH_PLUS_WORKER, or provide WorkerOptions.ExecutablePath.");
    }
}
