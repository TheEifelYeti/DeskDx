using System.Diagnostics;

namespace DeskDx.Tests;

/// <summary>
/// Kills stray Notepad instances before and after the acceptance tests.
/// Windows 11 ships Notepad as an app-execution alias, so a second launch simply activates an
/// already running instance instead of starting a new process. The tests need a clean slate.
/// </summary>
public sealed class NotepadCollectionFixture : IDisposable
{
    public NotepadCollectionFixture() => KillAllNotepads();

    public void Dispose() => KillAllNotepads();

    private static void KillAllNotepads()
    {
        foreach (var process in Process.GetProcessesByName("notepad"))
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

[CollectionDefinition("Notepad")]
public sealed class NotepadCollection : ICollectionFixture<NotepadCollectionFixture>
{
}
