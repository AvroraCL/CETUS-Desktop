namespace Cetus.Hosting;

/// <summary>
/// Thrown when the configured endpoint port is held by a foreign process and
/// never became a healthy DSH service within the grace window. Carries the
/// port so the caller can self-heal onto a free one.
/// </summary>
public sealed class DshPortOccupiedException(int port)
    : InvalidOperationException($"端口 {port} 已被其他程序占用，且不是健康的 DSH 服务。")
{
    public int Port { get; } = port;
}
