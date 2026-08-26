using System.Diagnostics;
using System.IO;
using System.Net.Http;

namespace Cetus.Desktop.Core;

/// <summary>
/// Owns the DeepSeek Harness sidecar (node.exe + @deepseek-ai/dsh) lifecycle:
///
///   EnsureReadyAsync  health probe → reuse an already-healthy server, otherwise
///                     spawn node hidden and poll until healthy.
///   Stop              kill the process tree, but ONLY when we spawned it.
///
/// Route A (M0): the sidecar serves http://127.0.0.1:PORT and WebView2 loads it
/// over plain HTTP. Route B (file:// + IPC bridge) is a later milestone.
/// </summary>
public sealed class DshServerProcess : IDisposable
{
    private const string RootMarker = "id=\"root\"";

    private readonly CetusConfig _config;
    private Process? _process;
    private CancellationTokenSource? _cts;
    private string? _logPath;

    public Uri Url => _config.Url;
    public bool SpawnedByUs => _process is not null;
    public string? LogPath => _logPath;
    public int? ProcessId => _process?.Id;

    public DshServerProcess(CetusConfig config) => _config = config;

    /// <summary>
    /// GET the page; healthy = HTTP 200 and the page contains the Vite root
    /// marker <c>id="root"</c> (the same probe the community launchers use).
    /// </summary>
    public async Task<bool> IsHealthyAsync(CancellationToken ct = default)
    {
        try
        {
            using var client = new HttpClient { Timeout = _config.HealthTimeout };
            using var response = await client.GetAsync(_config.Url, ct);
            if (!response.IsSuccessStatusCode) return false;
            var html = await response.Content.ReadAsStringAsync(ct);
            return html.Contains(RootMarker, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false; // connection refused / timeout → not healthy yet
        }
    }

    /// <summary>
    /// Make the backend ready: reuse a healthy server or spawn one and wait for
    /// it. Returns false when the timeout elapses without a healthy response.
    /// </summary>
    public async Task<bool> EnsureReadyAsync(CancellationToken ct = default)
    {
        if (await IsHealthyAsync(ct))
            return true; // reused — we must NOT kill it on exit

        var nodeExe = ResolveNodeExe();
        var dshCliJs = ResolveDshCliJs();
        CetusTrace.Info($"resolved node: {nodeExe}");
        CetusTrace.Info($"resolved dsh: {dshCliJs}");
        if (nodeExe.Length == 0)
            throw new InvalidOperationException("找不到 node.exe。请安装 Node.js，或设置 CETUS_NODE_EXE。");
        if (dshCliJs.Length == 0)
            throw new InvalidOperationException(
                "找不到 @deepseek-ai/dsh 的 lib/bin.js。请全局安装 dsh，或设置 CETUS_DSH_CLI_JS。");

        Spawn(nodeExe, dshCliJs);
        return await WaitUntilReadyAsync(_config.ReadyTimeout, ct);
    }

    private void Spawn(string nodeExe, string dshCliJs)
    {
        var psi = new ProcessStartInfo
        {
            FileName = nodeExe,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(dshCliJs)!,
        };
        psi.ArgumentList.Add(dshCliJs);
        psi.ArgumentList.Add("--profile");
        psi.ArgumentList.Add("web");
        if (_config.Port != 3080)
        {
            psi.ArgumentList.Add("--port");
            psi.ArgumentList.Add(_config.Port.ToString());
        }
        if (_config.DshHome is { Length: > 0 })
            psi.Environment["DSH_HOME"] = _config.DshHome;

        // Sidecar stdout/stderr goes to a log file (also drains the pipes so
        // node can never block on a full buffer).
        _logPath = Path.Combine(CetusPaths.LogDir, $"dsh-{DateTime.Now:yyyyMMdd-HHmmss}.log");
        Directory.CreateDirectory(Path.GetDirectoryName(_logPath)!);
        var logStream = new FileStream(_logPath, FileMode.Create, FileAccess.Write, FileShare.Read);
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;

        _cts = new CancellationTokenSource();
        _process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        _process.Exited += (_, _) => _cts?.Cancel();

        try
        {
            if (!_process.Start())
                throw new InvalidOperationException($"无法启动 node.exe：{nodeExe}");
        }
        catch
        {
            logStream.Dispose();
            _process.Dispose();
            _process = null;
            throw;
        }

        _ = PumpAsync(_process.StandardOutput.BaseStream, logStream, _cts.Token);
        _ = PumpAsync(_process.StandardError.BaseStream, logStream, _cts.Token);
    }

    private static async Task PumpAsync(Stream source, Stream sink, CancellationToken ct)
    {
        try
        {
            var buffer = new byte[81920];
            int read;
            while (!ct.IsCancellationRequested && (read = await source.ReadAsync(buffer, ct)) > 0)
            {
                await sink.WriteAsync(buffer.AsMemory(0, read), ct);
                await sink.FlushAsync(ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
    }

    private async Task<bool> WaitUntilReadyAsync(TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (await IsHealthyAsync(ct))
                return true;
            if (_process!.HasExited)
                throw new InvalidOperationException(
                    $"后端进程提前退出（exit code {_process.ExitCode}），日志：{_logPath}");
            await Task.Delay(500, ct);
        }
        return false;
    }

    /// <summary>Kill the sidecar process tree — only when we spawned it.</summary>
    public void Stop()
    {
        if (_process is null) return;
        try
        {
            if (!_process.HasExited)
                _process.Kill(entireProcessTree: true);
            _process.WaitForExit(5000);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Cetus: stop sidecar failed: {ex.Message}");
        }
        finally
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _process.Dispose();
            _process = null;
            _cts = null;
        }
    }

    public void Dispose() => Stop();

    private string ResolveNodeExe()
    {
        // 1) bundled with the package
        if (File.Exists(CetusPaths.BundledNodeExe))
            return CetusPaths.BundledNodeExe;

        // 2) explicit override
        if (_config.NodeExe is { Length: > 0 } && File.Exists(_config.NodeExe))
            return _config.NodeExe;

        // 3) machine-wide discovery
        var candidates = new List<string>
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "nodejs", "node.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "nodejs", "node.exe"),
        };
        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try { candidates.Add(Path.Combine(dir.Trim(), "node.exe")); }
            catch { /* malformed PATH entry */ }
        }
        return candidates.FirstOrDefault(File.Exists) ?? "";
    }

    private string ResolveDshCliJs()
    {
        // 1) bundled with the package
        if (File.Exists(CetusPaths.BundledDshCliJs))
            return CetusPaths.BundledDshCliJs;

        // 2) explicit override
        if (_config.DshCliJs is { Length: > 0 } && File.Exists(_config.DshCliJs))
            return _config.DshCliJs;

        // 3) machine-wide discovery
        var roots = new List<string>
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "npm"),
        };
        var prefix = Environment.GetEnvironmentVariable("NPM_CONFIG_PREFIX");
        if (!string.IsNullOrWhiteSpace(prefix))
            roots.Add(prefix.Trim());

        var candidates = roots
            .Select(root => Path.Combine(root, "node_modules", "@deepseek-ai", "dsh", "lib", "bin.js"));
        return candidates.FirstOrDefault(File.Exists) ?? "";
    }
}
