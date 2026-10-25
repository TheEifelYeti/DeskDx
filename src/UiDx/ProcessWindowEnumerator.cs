using System.Runtime.InteropServices;
using System.Text;

namespace UiDx;

/// <summary>
/// Enumerates the top-level windows of a process by HWND.
/// </summary>
/// <remarks>
/// This exists because the UIA tree is <em>not</em> a reliable way to find a process's dialogs:
/// <c>MessageBox.Show(owner, …)</c> creates an <b>owned</b> window, and UIA3 reports owned windows as
/// children of their owner window rather than as children of the desktop. Enumerating HWNDs by
/// process id and resolving each through <c>ElementFromHandle</c> catches them regardless of where
/// UIA decides to place them in the tree.
/// </remarks>
internal static class ProcessWindowEnumerator
{
    internal readonly record struct WindowHandle(IntPtr Handle, string Title, string ClassName);

    /// <summary>Returns the visible top-level windows that belong to <paramref name="processId"/>.</summary>
    internal static List<WindowHandle> GetVisibleTopLevelWindows(int processId)
    {
        var found = new List<WindowHandle>();

        try
        {
            EnumWindows((handle, _) =>
            {
                try
                {
                    GetWindowThreadProcessId(handle, out var owner);
                    if (owner != (uint)processId || !IsWindowVisible(handle))
                    {
                        return true;
                    }

                    found.Add(new WindowHandle(handle, ReadTitle(handle), ReadClassName(handle)));
                }
                catch (Exception)
                {
                    // A window that vanished mid-enumeration is simply skipped.
                }

                return true;
            }, IntPtr.Zero);
        }
        catch (Exception)
        {
            // Enumeration failed entirely; report what we have.
        }

        return found;
    }

    private static string ReadTitle(IntPtr handle)
    {
        var buffer = new StringBuilder(512);
        GetWindowText(handle, buffer, buffer.Capacity);
        return buffer.ToString();
    }

    private static string ReadClassName(IntPtr handle)
    {
        var buffer = new StringBuilder(256);
        GetClassName(handle, buffer, buffer.Capacity);
        return buffer.ToString();
    }

    private delegate bool EnumWindowsProc(IntPtr handle, IntPtr parameter);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr handle, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetWindowText(IntPtr handle, StringBuilder text, int maxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetClassName(IntPtr handle, StringBuilder className, int maxCount);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr handle);
}
