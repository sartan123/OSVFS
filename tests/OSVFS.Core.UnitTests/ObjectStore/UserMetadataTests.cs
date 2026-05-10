using OSVFS.ObjectStore;
using Xunit;

namespace OSVFS.Core.UnitTests.ObjectStore;

public class UserMetadataTests
{
    /// <summary>
    /// Mirrors the S3 backend's per-object byte ceiling (2 KiB). Used as the
    /// validation limit so the unit tests exercise the same threshold
    /// production code applies to S3 uploads.
    /// </summary>
    private const int S3MaxBytes = 2 * 1024;

    [Fact]
    public void Normalize_lowercases_keys_and_drops_null_or_empty_names()
    {
        var input = new Dictionary<string, string>
        {
            ["Tag"] = "alpha",
            ["MIXEDCase"] = "beta",
            [""] = "ignored",
        };

        var normalized = UserMetadata.Normalize(input);

        Assert.Equal(2, normalized.Count);
        Assert.Equal("alpha", normalized["tag"]);
        Assert.Equal("beta", normalized["mixedcase"]);
    }

    [Fact]
    public void Normalize_returns_empty_singleton_for_null_or_empty()
    {
        Assert.Same(UserMetadata.Empty, UserMetadata.Normalize(null));
        Assert.Same(
            UserMetadata.Empty,
            UserMetadata.Normalize(new Dictionary<string, string>()));
    }

    [Fact]
    public void EnsureWithinSizeLimit_accepts_payloads_at_or_below_limit()
    {
        // Total bytes = key + value lengths. 1024 + 1024 = 2048 = the S3 limit exactly.
        var payload = new Dictionary<string, string>
        {
            [new string('k', 1024)] = new string('v', 1024),
        };

        UserMetadata.EnsureWithinSizeLimit(payload, S3MaxBytes);
    }

    [Fact]
    public void EnsureWithinSizeLimit_throws_when_total_exceeds_limit()
    {
        // 1024 + 1025 = 2049 > 2048; the second insertion should trip the check.
        var payload = new Dictionary<string, string>
        {
            [new string('k', 1024)] = new string('v', 1025),
        };

        var ex = Assert.Throws<UserMetadataTooLargeException>(
            () => UserMetadata.EnsureWithinSizeLimit(payload, S3MaxBytes));
        Assert.True(ex.ActualBytes > S3MaxBytes);
        Assert.Equal(S3MaxBytes, ex.LimitBytes);
    }

    [Fact]
    public void EnsureWithinSizeLimit_counts_utf8_bytes_not_characters()
    {
        // "あ" is 3 UTF-8 bytes. 700 such characters + a 1-byte key = 2101 > 2048.
        var payload = new Dictionary<string, string>
        {
            ["k"] = new string('あ', 700),
        };

        Assert.Throws<UserMetadataTooLargeException>(
            () => UserMetadata.EnsureWithinSizeLimit(payload, S3MaxBytes));
    }

    [Fact]
    public void EnsureWithinSizeLimit_uses_caller_supplied_limit_for_higher_quota_providers()
    {
        // Azure Blob and GCS allow 8 KiB; the same payload that breaches the
        // 2 KiB S3 ceiling must validate cleanly when an 8 KiB ceiling is supplied.
        const int AzureLikeLimit = 8 * 1024;
        var payload = new Dictionary<string, string>
        {
            [new string('k', 1024)] = new string('v', 3072),
        };

        UserMetadata.EnsureWithinSizeLimit(payload, AzureLikeLimit);
        Assert.Throws<UserMetadataTooLargeException>(
            () => UserMetadata.EnsureWithinSizeLimit(payload, S3MaxBytes));
    }
}
