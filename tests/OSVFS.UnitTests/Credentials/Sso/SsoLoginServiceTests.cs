using Microsoft.Extensions.Time.Testing;
using OSVFS.Credentials.Sso;
using OSVFS.ObjectStore;
using OSVFS.UnitTests.Credentials;
using Xunit;

namespace OSVFS.UnitTests.Credentials.Sso;

/// <summary>
/// Drives <see cref="SsoLoginService"/> end-to-end against in-memory fakes for the
/// flow client, the token cache, the credential store, and the browser launcher,
/// using <see cref="FakeTimeProvider"/> so wall-clock checks are deterministic.
/// </summary>
public class SsoLoginServiceTests
{
    private static readonly DateTimeOffset Anchor = new(2026, 5, 10, 12, 0, 0, TimeSpan.Zero);

    private static readonly SsoLoginParameters DefaultParameters = new()
    {
        StartUrl = "https://example.awsapps.com/start",
        Region = "us-east-1",
        AccountId = "123456789012",
        RoleName = "ReadOnly",
        ProfileName = "demo",
    };

    [Fact]
    public async Task Login_runs_full_device_flow_and_persists_role_credentials()
    {
        var flow = new FakeSsoFlowClient();
        flow.CreateTokenSequence.Enqueue(new SsoAccessToken
        {
            AccessToken = "bearer-1",
            ExpiresAt = Anchor + TimeSpan.FromHours(1),
        });
        var (service, store, cache, browser, _) = NewService(flow);

        var credential = await service.LoginAsync(DefaultParameters, CancellationToken.None);

        Assert.Equal(1, flow.RegisterCalls);
        Assert.Equal(1, flow.StartDeviceCalls);
        Assert.Equal(1, flow.CreateTokenCalls);
        Assert.Equal(1, flow.GetRoleCredentialsCalls);
        Assert.Equal("bearer-1", flow.LastAccessTokenPresentedToSso);
        Assert.Equal("AKIAEXAMPLE", credential.AccessKeyId);
        Assert.Equal(credential, store.Entries[DefaultParameters.ProfileName]);
        Assert.Equal(["https://example.test/verify?user_code=ABC-DEF"], browser.LaunchedUrls);
        Assert.True(cache.Entries.ContainsKey(DefaultParameters.StartUrl));
    }

    [Fact]
    public async Task Login_reuses_cached_token_and_skips_browser_when_token_still_valid()
    {
        var flow = new FakeSsoFlowClient();
        var cache = new FakeSsoTokenCache();
        cache.Save(DefaultParameters.StartUrl, new SsoCachedToken
        {
            ClientId = "cached-id",
            ClientSecret = "cached-secret",
            ClientSecretExpiresAt = Anchor + TimeSpan.FromDays(30),
            AccessToken = "bearer-cached",
            AccessTokenExpiresAt = Anchor + TimeSpan.FromHours(1),
        });
        var (service, store, _, browser, _) = NewService(flow, cache);

        await service.LoginAsync(DefaultParameters, CancellationToken.None);

        Assert.Equal(0, flow.RegisterCalls);
        Assert.Equal(0, flow.StartDeviceCalls);
        Assert.Equal(0, flow.CreateTokenCalls);
        Assert.Equal(1, flow.GetRoleCredentialsCalls);
        Assert.Equal("bearer-cached", flow.LastAccessTokenPresentedToSso);
        Assert.Empty(browser.LaunchedUrls);
        Assert.True(store.Entries.ContainsKey(DefaultParameters.ProfileName));
    }

    [Fact]
    public async Task Login_reregisters_client_when_cached_token_expired_but_not_client_secret()
    {
        var flow = new FakeSsoFlowClient();
        flow.CreateTokenSequence.Enqueue(new SsoAccessToken
        {
            AccessToken = "bearer-fresh",
            ExpiresAt = Anchor + TimeSpan.FromHours(1),
        });
        var cache = new FakeSsoTokenCache();
        cache.Save(DefaultParameters.StartUrl, new SsoCachedToken
        {
            ClientId = "cached-id",
            ClientSecret = "cached-secret",
            ClientSecretExpiresAt = Anchor + TimeSpan.FromDays(30),
            AccessToken = "bearer-stale",
            AccessTokenExpiresAt = Anchor - TimeSpan.FromMinutes(1),
        });
        var (service, _, _, _, _) = NewService(flow, cache);

        await service.LoginAsync(DefaultParameters, CancellationToken.None);

        // The cached client registration is still valid, so we should NOT call RegisterClient again.
        Assert.Equal(0, flow.RegisterCalls);
        Assert.Equal(1, flow.StartDeviceCalls);
        Assert.Equal(1, flow.CreateTokenCalls);
        Assert.Equal("bearer-fresh", flow.LastAccessTokenPresentedToSso);
    }

    [Fact]
    public async Task Login_polls_through_authorization_pending_until_user_approves()
    {
        var flow = new FakeSsoFlowClient
        {
            DeviceAuthorizationToReturn = new SsoDeviceAuthorization
            {
                DeviceCode = "device",
                UserCode = "ABC",
                VerificationUri = "https://example.test/v",
                VerificationUriComplete = "https://example.test/v?user_code=ABC",
                ExpiresIn = TimeSpan.FromMinutes(10),
                PollInterval = TimeSpan.Zero,
            },
        };
        flow.CreateTokenSequence.Enqueue(new SsoAuthorizationPendingException());
        flow.CreateTokenSequence.Enqueue(new SsoAuthorizationPendingException());
        flow.CreateTokenSequence.Enqueue(new SsoAccessToken
        {
            AccessToken = "bearer-after-approval",
            ExpiresAt = Anchor + TimeSpan.FromHours(1),
        });
        var (service, _, _, _, _) = NewService(flow, minPollInterval: TimeSpan.Zero);

        await service.LoginAsync(DefaultParameters, CancellationToken.None);

        Assert.Equal(3, flow.CreateTokenCalls);
        Assert.Equal("bearer-after-approval", flow.LastAccessTokenPresentedToSso);
    }

    [Fact]
    public async Task Login_throws_expired_when_device_code_lifetime_exceeded()
    {
        var flow = new FakeSsoFlowClient
        {
            DeviceAuthorizationToReturn = new SsoDeviceAuthorization
            {
                DeviceCode = "device",
                UserCode = "ABC",
                VerificationUri = "https://example.test/v",
                VerificationUriComplete = "https://example.test/v?user_code=ABC",
                ExpiresIn = TimeSpan.FromSeconds(2),
                PollInterval = TimeSpan.FromSeconds(5),
            },
        };
        flow.CreateTokenSequence.Enqueue(new SsoAuthorizationPendingException());
        var (service, store, _, _, _) = NewService(flow);

        await Assert.ThrowsAsync<SsoExpiredTokenException>(
            () => service.LoginAsync(DefaultParameters, CancellationToken.None));
        Assert.Empty(store.Entries);
    }

    [Fact]
    public async Task Login_propagates_unexpected_create_token_failure()
    {
        var flow = new FakeSsoFlowClient();
        flow.CreateTokenSequence.Enqueue(new SsoLoginException("boom"));
        var (service, store, _, _, _) = NewService(flow);

        var ex = await Assert.ThrowsAsync<SsoLoginException>(
            () => service.LoginAsync(DefaultParameters, CancellationToken.None));
        Assert.Equal("boom", ex.Message);
        Assert.Empty(store.Entries);
    }

    [Fact]
    public async Task Login_continues_when_browser_launch_throws()
    {
        var flow = new FakeSsoFlowClient();
        flow.CreateTokenSequence.Enqueue(new SsoAccessToken
        {
            AccessToken = "bearer-1",
            ExpiresAt = Anchor + TimeSpan.FromHours(1),
        });
        var browser = new RecordingBrowserLauncher
        {
            ThrowOnLaunch = new InvalidOperationException("no browser available"),
        };
        var (service, store, _, _, output) = NewService(flow, browser: browser);

        await service.LoginAsync(DefaultParameters, CancellationToken.None);

        Assert.Single(store.Entries);
        Assert.Contains("could not auto-launch browser", output.ToString());
    }

    /// <summary>
    /// Builds a service with sensible defaults for the collaborators not under
    /// test, and returns the collaborators so callers can assert against them.
    /// </summary>
    private static (
        SsoLoginService Service,
        FakeCredentialStore Store,
        FakeSsoTokenCache Cache,
        RecordingBrowserLauncher Browser,
        StringWriter Output) NewService(
        FakeSsoFlowClient flow,
        FakeSsoTokenCache? cache = null,
        RecordingBrowserLauncher? browser = null,
        TimeSpan? minPollInterval = null)
    {
        var store = new FakeCredentialStore();
        cache ??= new FakeSsoTokenCache();
        browser ??= new RecordingBrowserLauncher();
        var time = new FakeTimeProvider(Anchor);
        var output = new StringWriter();
        var service = new SsoLoginService(
            flow, cache, store, browser, time, output, minPollInterval ?? TimeSpan.Zero);
        return (service, store, cache, browser, output);
    }
}
