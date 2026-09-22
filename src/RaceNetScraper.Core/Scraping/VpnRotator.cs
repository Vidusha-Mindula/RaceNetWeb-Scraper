using System.Diagnostics;

namespace RaceNetScraper.Core.Scraping;

/// <summary>
/// Rotates the ExpressVPN connection when a scrape looks like it hit bot-detection/an IP-based
/// block, so a retry gets a fresh IP instead of immediately re-hitting the one that just got
/// challenged (see RaceNetScraperService's Cloudflare-challenge comments — datacenter/cloud IPs
/// get challenged far more than residential ones).
///
/// Talks to ExpressVPN via a headless `claude -p` call rather than the MCP HTTP endpoint
/// directly: this machine's `claude` CLI already has the `expressvpn` MCP server registered
/// (see the ExpressVPN MCP setup guide), and routing through it means *which* region to use
/// stays a deterministic, logged decision made here in C#, while Claude is only asked to
/// execute that one specific change — not to improvise which server to pick.
///
/// Requires: the ExpressVPN desktop app running locally with its MCP server active, and
/// `claude` on PATH and logged in, with `expressvpn` registered at user scope (`--scope user`)
/// so it resolves no matter which directory this process's working directory is.
/// </summary>
public static class VpnRotator
{
    // Deliberately short and spread across regions/providers so consecutive rotations don't
    // keep landing in the same datacenter block. Adjust to taste — these are ExpressVPN region
    // slugs as accepted by `expressvpn_set_region`.
    private static readonly string[] Regions =
    [
        "uk", "usny", "usla", "ca", "de", "fr", "nl", "sg", "jp"
    ];

    private static int _index = -1;

    // Serializes actual rotations: if several scrapes hit a block around the same time (e.g.
    // concurrent race-detail scraping), they should queue behind one rotation rather than each
    // spawning their own overlapping `claude -p` process and region change.
    private static readonly SemaphoreSlim RotationLock = new(1, 1);

    /// <summary>
    /// Runs <paramref name="action"/>; if it throws something that looks like a bot-detection/
    /// IP block (see <see cref="LooksLikeBotBlock"/>), rotates the VPN once and retries
    /// <paramref name="action"/> exactly once more. A second failure (or a failed/unavailable
    /// rotation) propagates to the caller exactly like it would without this wrapper, so
    /// existing catch blocks around scrape calls don't need to change.
    /// </summary>
    public static async Task<T> RunWithRotationOnBlockAsync<T>(
        Func<Task<T>> action, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        try
        {
            return await action();
        }
        catch (Exception ex) when (ex is not OperationCanceledException && LooksLikeBotBlock(ex))
        {
            progress?.Report($"[VPN] Scrape looks blocked ({ex.Message}); rotating ExpressVPN and retrying once...");

            if (!await RotateAsync(progress, cancellationToken))
            {
                throw; // rotation itself failed/unavailable — surface the original block error
            }

            return await action(); // one retry; a second failure propagates to the caller's own catch
        }
    }

    /// <summary>
    /// Heuristic for "this failure looks like an IP-based block/bot-detection" as opposed to a
    /// logic/parsing error, where rotating the VPN would just waste a retry. Matches the
    /// specific signatures RaceNetScraperService's Cloudflare-challenge handling and its
    /// diagnostics text use.
    /// </summary>
    public static bool LooksLikeBotBlock(Exception ex)
    {
        var msg = ex.Message;
        return msg.Contains("Just a moment", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("Checking your browser", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("Attention Required", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("challenge", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("No embedded Nuxt3 apollo cache found", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("blocked", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("CAPTCHA", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Picks the next region in round-robin rotation (not random, so repeated failures don't
    /// re-roll the same just-blocked region back-to-back) and asks ExpressVPN, via a headless
    /// `claude -p` call, to disconnect, switch, and reconnect. Returns false (does not throw) on
    /// any failure/timeout — callers should treat that as "rotation unavailable this time" and
    /// fall back to their own error handling rather than let this crash the scrape.
    /// </summary>
    public static async Task<bool> RotateAsync(
        IProgress<string>? progress = null, CancellationToken cancellationToken = default, int timeoutMs = 45_000)
    {
        await RotationLock.WaitAsync(cancellationToken);
        try
        {
            var region = NextRegion();
            var prompt =
                $"Disconnect ExpressVPN if connected, switch the region to '{region}', then connect. " +
                "Report only Connected or Failed plus the reason, nothing else.";

            progress?.Report($"[VPN] Rotating to region '{region}'...");

            var psi = new ProcessStartInfo
            {
                FileName = "claude",
                ArgumentList = { "-p", prompt },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                // `expressvpn` is currently registered at *local* (project) scope under the
                // user's home directory (see the ExpressVPN MCP setup guide) — `claude` only
                // resolves local-scope servers when its cwd matches where they were added, and
                // this process's own working directory (wherever the scraper .exe launches from)
                // won't match that. Pin it explicitly rather than relying on scope alone. If you
                // re-register expressvpn with `--scope user` this stops mattering, but leaving it
                // pinned is harmless either way.
                WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            };

            using var process = Process.Start(psi)
                ?? throw new InvalidOperationException("Failed to start the 'claude' CLI process.");

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeoutMs);

            var stdOutTask = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
            var stdErrTask = process.StandardError.ReadToEndAsync(timeoutCts.Token);

            try
            {
                await process.WaitForExitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                progress?.Report($"[VPN] Rotation to '{region}' timed out after {timeoutMs}ms.");
                TryKill(process);
                return false;
            }

            var stdout = await stdOutTask;
            var stderr = await stdErrTask;

            if (process.ExitCode != 0)
            {
                progress?.Report($"[VPN] Rotation to '{region}' failed (exit {process.ExitCode}): {stderr.Trim()}");
                return false;
            }

            progress?.Report($"[VPN] Rotation to '{region}' result: {stdout.Trim()}");
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            progress?.Report($"[VPN] Rotation errored: {ex.Message}");
            return false;
        }
        finally
        {
            RotationLock.Release();
        }
    }

    private static void TryKill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch { /* best-effort */ }
    }

    private static string NextRegion()
    {
        var next = Interlocked.Increment(ref _index);
        return Regions[(next % Regions.Length + Regions.Length) % Regions.Length];
    }
}
