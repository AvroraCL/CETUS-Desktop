using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace Cetus.Application;

public sealed record ActivationRequest(string? WorkspacePath);

/// <summary>
/// Named-pipe activation channel between Cetus processes. The primary
/// instance hosts the server; later launches forward their parsed launch
/// request through <see cref="TryForwardAsync"/> and exit. A forwarded
/// request without a workspace path means "summon the window". The pipe name
/// carries the same CETUS_INSTANCE_ID suffix as the single-instance mutex so
/// development profiles stay isolated.
/// </summary>
public sealed class ActivationChannel : IDisposable
{
    private const string PipeNamePrefix = "Cetus.Desktop.Activation";

    private readonly string _pipeName;
    private readonly Action<ActivationRequest> _onActivationRequested;
    private CancellationTokenSource? _cancellation;
    private Task? _serverLoop;
    private bool _disposed;

    public ActivationChannel(string? instanceId, Action<ActivationRequest> onActivationRequested)
    {
        _pipeName = BuildPipeName(instanceId);
        _onActivationRequested = onActivationRequested;
    }

    public static string BuildPipeName(string? instanceId)
    {
        string suffix = string.IsNullOrWhiteSpace(instanceId)
            ? string.Empty
            : $".{instanceId.Trim()}";
        return PipeNamePrefix + suffix;
    }

    /// <summary>Starts accepting forwarded requests in the background.</summary>
    public void Start()
    {
        if (_serverLoop is not null || _disposed)
        {
            return;
        }

        _cancellation = new CancellationTokenSource();
        _serverLoop = Task.Run(() => ServerLoopAsync(_pipeName, _cancellation.Token));
    }

    private async Task ServerLoopAsync(string pipeName, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            string? workspace = null;
            bool activated = false;
            try
            {
                using var server = new NamedPipeServerStream(
                    pipeName,
                    PipeDirection.In,
                    maxNumberOfServerInstances: 1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);
                await server.WaitForConnectionAsync(token);
                using var reader = new StreamReader(server, Encoding.UTF8, leaveOpen: true);
                workspace = ParseMessage(await reader.ReadToEndAsync(token));
                activated = true;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (IOException)
            {
                // A broken client connection must not kill the server loop.
            }

            if (activated)
            {
                _onActivationRequested(new ActivationRequest(workspace));
            }
        }
    }

    internal static string? ParseMessage(string message)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(message);
            JsonElement root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("workspace", out JsonElement workspace)
                && workspace.ValueKind == JsonValueKind.String)
            {
                return workspace.GetString();
            }
        }
        catch (JsonException)
        {
            // Foreign or malformed payload — treated as no request.
        }

        return null;
    }

    /// <summary>
    /// Best-effort forward from a second launch. Returns false when no
    /// primary instance answered within the timeout.
    /// </summary>
    public static Task<bool> TryForwardAsync(string? workspacePath, string? instanceId = null)
    {
        string message = JsonSerializer.Serialize(new { workspace = workspacePath });
        return TryForwardMessageAsync(message, instanceId);
    }

    private static async Task<bool> TryForwardMessageAsync(string message, string? instanceId)
    {
        byte[] payload = Encoding.UTF8.GetBytes(message);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try
        {
            await using var client = new NamedPipeClientStream(
                ".",
                BuildPipeName(instanceId),
                PipeDirection.Out,
                PipeOptions.Asynchronous);
            await client.ConnectAsync(timeout.Token);
            await client.WriteAsync(payload, timeout.Token);
            await client.FlushAsync(timeout.Token);
            return true;
        }
        catch (Exception error) when (error is IOException or OperationCanceledException or ObjectDisposedException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _cancellation?.Cancel();
        try
        {
            _serverLoop?.Wait(TimeSpan.FromSeconds(1));
        }
        catch
        {
            // The loop unwinds through cancellation; nothing to observe.
        }

        _cancellation?.Dispose();
    }
}
