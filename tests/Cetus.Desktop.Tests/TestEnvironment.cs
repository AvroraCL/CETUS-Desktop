using System.Runtime.CompilerServices;

namespace Cetus.Desktop.Tests;

/// <summary>
/// Assembly-wide sandbox for the real Cetus user-data directory, installed
/// before any test class is constructed.
///
/// Lifecycle tests spawn actual node sidecars: <c>DshHost</c> appends every
/// start attempt to the runtime log and records the spawned pid for orphan
/// detection. Written to the real location, a test run fabricates "DSH keeps
/// restarting" evidence inside the very log a user is asked to inspect, and
/// overwrites the ownership marker a live Cetus instance relies on.
///
/// Environment variables can only be redirected once, so this runs as a module
/// initializer instead of a per-class scope; the scratch directory is left to
/// the OS temp cleaner.
/// </summary>
internal static class TestEnvironment
{
    internal static string? ScratchDirectory { get; private set; }

    [ModuleInitializer]
    internal static void Initialize()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "cetus-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        ScratchDirectory = directory;

        Environment.SetEnvironmentVariable("CETUS_USER_DATA_DIR", directory);
        Environment.SetEnvironmentVariable("CETUS_LOG_DIR", Path.Combine(directory, "logs"));
        Environment.SetEnvironmentVariable("CETUS_UPDATE_DIR", Path.Combine(directory, "updates"));
    }
}
