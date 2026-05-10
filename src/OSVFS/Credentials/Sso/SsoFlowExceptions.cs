namespace OSVFS.Credentials.Sso;

/// <summary>
/// Thrown by <see cref="ISsoFlowClient.CreateTokenAsync"/> while the user has not yet
/// approved the device-code prompt; the polling loop catches this and waits.
/// </summary>
internal sealed class SsoAuthorizationPendingException : Exception
{
    /// <summary>Constructs the exception with a default message.</summary>
    public SsoAuthorizationPendingException()
        : base("Authorization is still pending — user has not completed the browser approval.") { }
}

/// <summary>
/// Thrown when IAM Identity Center asks the client to slow its polling cadence.
/// The polling loop responds by extending the interval.
/// </summary>
internal sealed class SsoSlowDownException : Exception
{
    /// <summary>Constructs the exception with a default message.</summary>
    public SsoSlowDownException()
        : base("Identity Center asked the client to back off; extending poll interval.") { }
}

/// <summary>
/// Thrown when the device code has expired before the user approved it; the caller
/// has to restart the flow from <c>StartDeviceAuthorization</c>.
/// </summary>
internal sealed class SsoExpiredTokenException : Exception
{
    /// <summary>Constructs the exception with a default message.</summary>
    public SsoExpiredTokenException()
        : base("Device code expired before approval. Restart the SSO login flow.") { }
}

/// <summary>
/// Wraps any non-recoverable Identity Center error (access denied, invalid client,
/// service errors) so the CLI surface reports a single typed failure mode.
/// </summary>
internal sealed class SsoLoginException : Exception
{
    /// <summary>Constructs the exception with a user-facing message.</summary>
    public SsoLoginException(string message) : base(message) { }

    /// <summary>Constructs the exception with a message and inner cause.</summary>
    public SsoLoginException(string message, Exception inner) : base(message, inner) { }
}
