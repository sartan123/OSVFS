using OSVFS.Credentials.Sso;

namespace OSVFS.UnitTests.Credentials.Sso;

/// <summary>
/// <see cref="IBrowserLauncher"/> that records every URL it was asked to launch
/// instead of opening a browser. Lets tests assert that the verification URL was
/// passed through correctly.
/// </summary>
internal sealed class RecordingBrowserLauncher : IBrowserLauncher
{
    /// <summary>URLs the service requested in launch order.</summary>
    public List<string> LaunchedUrls { get; } = [];

    /// <summary>When non-null, <see cref="Launch"/> throws this instead of recording.</summary>
    public Exception? ThrowOnLaunch { get; set; }

    /// <inheritdoc/>
    public void Launch(string url)
    {
        if (ThrowOnLaunch is not null) throw ThrowOnLaunch;
        LaunchedUrls.Add(url);
    }
}
