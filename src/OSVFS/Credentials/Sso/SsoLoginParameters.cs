namespace OSVFS.Credentials.Sso;

/// <summary>
/// Inputs collected from the <c>credentials sso</c> CLI: the IAM Identity Center
/// portal start URL, the SSO region, and the AWS account/role to assume.
/// </summary>
internal sealed record SsoLoginParameters
{
    /// <summary>
    /// IAM Identity Center user portal URL (e.g. <c>https://my-org.awsapps.com/start</c>).
    /// </summary>
    public required string StartUrl { get; init; }

    /// <summary>
    /// AWS region that hosts the IAM Identity Center instance (e.g. <c>us-east-1</c>).
    /// </summary>
    public required string Region { get; init; }

    /// <summary>
    /// AWS account ID to retrieve role credentials for.
    /// </summary>
    public required string AccountId { get; init; }

    /// <summary>
    /// Permission-set role name in the target account.
    /// </summary>
    public required string RoleName { get; init; }

    /// <summary>
    /// Local OSVFS profile name to save the resulting role credentials under.
    /// </summary>
    public required string ProfileName { get; init; }
}
