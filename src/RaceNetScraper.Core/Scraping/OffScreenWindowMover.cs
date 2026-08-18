using System.Diagnostics;
using System.Runtime.InteropServices;

namespace RaceNetScraper.Core.Scraping;

/// <summary>
/// Moves a just-launched process's window(s) off-screen, the same effect Chromium's
/// "--window-position=-32000,-32000" argument achieves for Chrome/Edge — except Firefox has no
/// equivalent command-line flag, so this does it at the OS level via Win32 instead. Windows-only
/// (no-ops elsewhere, since this app's off-screen requirement is Windows-specific already — see
/// ScraperOptions.HideWindow).
/// </summary>
internal static class OffScreenWindowMover
{
    private const int OffScreenX = -32000;
    private const int OffScreenY = -32000;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetParent(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    private static readonly TimeSpan SettleWindow = TimeSpan.FromSeconds(1.5);

    /// <summary>
    /// Repeatedly scans every top-level, visible window in the system, moving off-screen any that
    /// belong to a <paramref name="processName"/> process started at or after
    /// <paramref name="launchedAfter"/>, until either <paramref name="timeout"/> elapses or a
    /// window has been moved and stayed quiet (no new matching windows) for <see cref="SettleWindow"/>.
    /// Deliberately does NOT stop at the very first match: <c>Process.MainWindowHandle</c> (the
    /// more obvious .NET API) proved unreliable here — it can stay <see cref="IntPtr.Zero"/> even
    /// once a real visible window exists — and Firefox can open more than one top-level window (a
    /// profile-creation prompt on first run, for instance), so this walks every window directly
    /// via EnumWindows/GetWindowThreadProcessId instead and keeps checking a little longer after
    /// the first success to catch any stragglers, rather than returning the instant one window
    /// moves and potentially missing a second one that opens a moment later.
    /// </summary>
    public static async Task TryMoveOffScreenAsync(string processName, DateTime launchedAfter, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows()) return;

        var deadline = DateTime.UtcNow + timeout;
        DateTime? firstMovedAt = null;

        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var pids = new HashSet<uint>();
            foreach (var process in Process.GetProcessesByName(processName))
            {
                try
                {
                    if (process.StartTime.ToUniversalTime() >= launchedAfter) pids.Add((uint)process.Id);
                }
                catch (InvalidOperationException)
                {
                    // Exited between GetProcessesByName and reading StartTime.
                }
                catch (System.ComponentModel.Win32Exception)
                {
                    // No permission to query this process (e.g. a different user's instance).
                }
            }

            var movedAny = false;
            if (pids.Count > 0)
            {
                EnumWindows((hWnd, _) =>
                {
                    GetWindowThreadProcessId(hWnd, out var pid);
                    if (pids.Contains(pid) && IsWindowVisible(hWnd) && GetParent(hWnd) == IntPtr.Zero)
                    {
                        SetWindowPos(hWnd, IntPtr.Zero, OffScreenX, OffScreenY, 0, 0,
                            SwpNoSize | SwpNoZOrder | SwpNoActivate);
                        movedAny = true;
                    }
                    return true;
                }, IntPtr.Zero);
            }

            if (movedAny) firstMovedAt ??= DateTime.UtcNow;
            if (firstMovedAt is { } moved && DateTime.UtcNow - moved >= SettleWindow) return;

            await Task.Delay(200, cancellationToken);
        }
    }
}
