using System.Diagnostics;
using UiDx;

namespace UiDx.Mcp.Tests;

/// <summary>
/// Shared state for the M-tests: the TestTarget executable, a UiDx server instance built in-proc and
/// the cleanup of the processes a test run leaves behind.
/// </summary>
/// <remarks>
/// <para>
/// The tests run against <see cref="UiDxTools"/> directly — the whole server behaviour is in-proc
/// callable, so M1–M7 need no process pairing. Only M8 starts the real stdio server.
/// </para>
/// <para>
/// The committed baseline lives in <c>tests/UiDx.Tests/snapshots/</c> (that is where the engine's own
/// step-6 test put it), so <c>SnapshotStore.BaseDirectory</c> is pointed there.
/// </para>
/// <para>
/// <b>Why a renamed copy of the test target.</b> The engine's fixture cleans up by killing every
/// process named <c>TestTarget</c>, and <c>dotnet test</c> runs the two test projects in parallel —
/// so a shared process name lets one suite kill the other's application mid-test (observed: a
/// sporadic <c>VB6Locator</c> failure in the engine suite). This fixture therefore runs a private
/// copy named <c>TestTargetMcp</c> and only ever kills that name.
/// </para>
/// </remarks>
public sealed class TestTargetFixture : IDisposable
{
    /// <summary>Process/image name of this fixture's private copy of the test target.</summary>
    private const string PrivateImageName = "TestTargetMcp";

    public TestTargetFixture()
    {
        ExecutablePath = ResolveExecutablePath();
        SnapshotStore.BaseDirectory = BaselineDirectory;

        KillAll(PrivateImageName);
    }

    /// <summary>Absolute path of this fixture's private TestTargetMcp.exe.</summary>
    public string ExecutablePath { get; }

    /// <summary>Directory holding the committed snapshot baselines.</summary>
    public static string BaselineDirectory => Path.Combine(RepositoryRoot, "tests", "UiDx.Tests");

    /// <summary>Repository root (the folder containing <c>UiDx.slnx</c>).</summary>
    public static string RepositoryRoot
    {
        get
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "UiDx.slnx")))
            {
                directory = directory.Parent;
            }

            return directory?.FullName
                   ?? throw new InvalidOperationException("could not locate the repository root (UiDx.slnx)");
        }
    }

    /// <summary>Creates a fresh server-side tool set with its own session registry.</summary>
    public static UiDxTools NewTools(SessionRegistry? registry = null)
        => new(registry ?? new SessionRegistry(), new TraceRoot(Path.Combine(
            Path.GetTempPath(),
            "UiDx.Mcp.Tests",
            Guid.NewGuid().ToString("N"))));

    public void Dispose() => KillAll(PrivateImageName);

    /// <summary>
    /// Resolves the second build of the test target — the same form under a different image name. See
    /// <c>tools/TestTarget.Mcp</c>: the distinct name is what keeps this suite's process cleanup from
    /// killing the engine suite's application, because the test projects run in parallel.
    /// </summary>
    private static string ResolveExecutablePath()
    {
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name ?? "Debug";
        var path = Path.Combine(
            RepositoryRoot,
            "tools", "TestTarget.Mcp", "bin", configuration, "net8.0-windows", PrivateImageName + ".exe");

        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"{PrivateImageName}.exe not found at '{path}'. Build the solution first (dotnet build UiDx.slnx).",
                path);
        }

        return path;
    }

    private static void KillAll(string processName)
    {
        foreach (var process in Process.GetProcessesByName(processName))
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill();
                    process.WaitForExit(5000);
                }
            }
            catch (Exception)
            {
                // Process vanished underneath us.
            }
            finally
            {
                process.Dispose();
            }
        }
    }
}

[CollectionDefinition("TestTarget")]
public sealed class TestTargetCollection : ICollectionFixture<TestTargetFixture>
{
}
