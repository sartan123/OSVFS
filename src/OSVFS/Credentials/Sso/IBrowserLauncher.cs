using System.Diagnostics;

namespace OSVFS.Credentials.Sso;

/// <summary>
/// Opens an arbitrary URL in the user's default browser. Tests substitute a fake
/// implementation so the device-flow polling loop can run without spawning a real
/// browser window.
/// </summary>
internal interface IBrowserLauncher
{
    /// <summary>Best-effort: launch <paramref name="url"/> in the system default browser.</summary>
    void Launch(string url);
}

/// <summary>
/// Production launcher that delegates to <c>ShellExecute</c> via <see cref="Process.Start"/>
/// with <c>UseShellExecute = true</c>; on Windows that picks the registered HTTP handler.
/// </summary>
internal sealed class DefaultBrowserLauncher : IBrowserLauncher
{
    /// <inheritdoc/>
    public void Launch(string url)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        Process.Start(new ProcessStartInfo
        {
            FileName = url,
            UseShellExecute = true,
        })?.Dispose();
    }
}
