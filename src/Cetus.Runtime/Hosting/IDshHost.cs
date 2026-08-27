namespace Cetus.Hosting;

/// <summary>
/// Internal seam used by the desktop runtime state machine. Production uses
/// <see cref="DshHost"/>; deterministic tests provide an in-memory adapter.
/// </summary>
internal interface IDshHost : IDisposable
{
    event EventHandler<DshHostFailureEventArgs>? RuntimeFailure;

    string? LogPath { get; }

    Task StartAsync(CancellationToken cancellationToken = default);

    Task StopAsync();
}

internal interface IDshHostFactory
{
    IDshHost Create(Uri endpoint, string? dshHomeOverride);
}

internal sealed class DefaultDshHostFactory : IDshHostFactory
{
    public IDshHost Create(Uri endpoint, string? dshHomeOverride) =>
        new DshHost(DshLocator.Resolve(), endpoint.AbsoluteUri, dshHomeOverride);
}
