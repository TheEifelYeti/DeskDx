using System.Runtime.InteropServices;

namespace DeskDx.Mcp;

/// <summary>
/// Asynchronous, UIA-free activation of a control — the trigger a gate must not be used for.
/// </summary>
/// <remarks>
/// <para>
/// Posting <c>BM_CLICK</c> to a button's window handle is the technique the engine's own dialog tests
/// use. It matters for two reasons:
/// </para>
/// <list type="bullet">
///   <item><b>It does not block.</b> <c>InvokePattern.Invoke</c> on a control that opens a modal
///     dialog leaves the provider's UI thread inside <c>MessageBox.Show</c>, so the call either blocks
///     the caller or fails with a UIA timeout — unusable when the tool must return.</item>
///   <item><b>It does not touch UIA.</b> Running the invoke on a second thread to bound it would make
///     two threads use the same automation client, which is not thread-safe: the session's dialog
///     watcher then finds the dialog but can no longer read it, and the dialog is never answered. A
///     posted message keeps every UIA call on the session's own thread.</item>
/// </list>
/// <para>
/// Only usable for controls that have a window handle (buttons always do). The caller falls back to
/// <c>InvokePattern</c> otherwise.
/// </para>
/// </remarks>
internal static class AsyncTrigger
{
    /// <summary>The Win32 <c>BM_CLICK</c> message that makes a button perform its click.</summary>
    private const uint ButtonClickMessage = 0x00F5;

    /// <summary>Posts a click to the control asynchronously. Returns false when the post failed.</summary>
    internal static bool PostButtonClick(IntPtr windowHandle)
        => windowHandle != IntPtr.Zero
           && PostMessage(windowHandle, ButtonClickMessage, IntPtr.Zero, IntPtr.Zero);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr handle, uint message, IntPtr wParam, IntPtr lParam);
}
