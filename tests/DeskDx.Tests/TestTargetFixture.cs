using System.Diagnostics;

namespace DeskDx.Tests;

/// <summary>
/// Resolves the TestTarget executable and kills stray TestTarget instances before and after the run.
/// </summary>
public sealed class TestTargetFixture : IDisposable
{
    public TestTargetFixture()
    {
        ExecutablePath = ResolveExecutablePath();
        KillAllTestTargets();
    }

    /// <summary>Absolute path of the built TestTarget.exe.</summary>
    public string ExecutablePath { get; }

    public void Dispose() => KillAllTestTargets();

    private static string ResolveExecutablePath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "DeskDx.slnx")))
        {
            directory = directory.Parent;
        }

        if (directory is null)
        {
            throw new InvalidOperationException("could not locate the repository root (DeskDx.slnx)");
        }

        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name ?? "Debug";
        var path = Path.Combine(
            directory.FullName,
            "tools", "TestTarget", "bin", configuration, "net8.0-windows", "TestTarget.exe");
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"TestTarget.exe not found at '{path}'. Build the solution first (dotnet build DeskDx.slnx).",
                path);
        }

        return path;
    }

    /// <summary>
    /// Absolute path of the repository root (the folder containing <c>DeskDx.slnx</c>), used to place
    /// committed artefacts outside the build output.
    /// </summary>
    internal static string RepositoryRoot
    {
        get
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "DeskDx.slnx")))
            {
                directory = directory.Parent;
            }

            return directory?.FullName
                   ?? throw new InvalidOperationException("could not locate the repository root (DeskDx.slnx)");
        }
    }

    /// <summary>
    /// Absolute path of the test project folder (<c>tests/DeskDx.Tests</c>), where the committed
    /// snapshot baselines live.
    /// </summary>
    internal static string TestProjectRoot => Path.Combine(RepositoryRoot, "tests", "DeskDx.Tests");

    private static void KillAllTestTargets()
    {
        foreach (var process in Process.GetProcessesByName("TestTarget"))
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
