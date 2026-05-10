using OSVFS.ObjectStore;
using OSVFS.ObjectStore.S3;
using Xunit;

namespace OSVFS.UnitTests;

/// <summary>
/// Boundary checks for the multipart-upload validation that gates startup.
/// All assertions run against <see cref="MultipartCapabilities.S3"/> because
/// S3 is the only backend shipping today; the validator's contract — bounds
/// come from the supplied capabilities — is the same for future Azure / GCS
/// arms.
/// </summary>
public class MultipartSettingsValidatorTests
{
    private static readonly MultipartCapabilities S3 = MultipartCapabilities.S3;

    [Fact]
    public void Returns_null_when_both_inputs_are_null()
    {
        Assert.Null(MultipartSettingsValidator.Validate(null, null, S3));
    }

    [Fact]
    public void Returns_null_at_min_part_size()
    {
        Assert.Null(MultipartSettingsValidator.Validate(
            thresholdBytes: 8L * 1024 * 1024,
            partSizeBytes: S3.MinPartSizeBytes,
            S3));
    }

    [Fact]
    public void Returns_null_at_max_part_size()
    {
        Assert.Null(MultipartSettingsValidator.Validate(
            thresholdBytes: 8L * 1024 * 1024,
            partSizeBytes: S3.MaxPartSizeBytes,
            S3));
    }

    [Fact]
    public void Rejects_part_size_one_byte_below_min()
    {
        var error = MultipartSettingsValidator.Validate(
            thresholdBytes: 8L * 1024 * 1024,
            partSizeBytes: S3.MinPartSizeBytes - 1,
            S3);
        Assert.NotNull(error);
        Assert.Contains("multipart-part-size", error, StringComparison.Ordinal);
        Assert.Contains("5 MiB", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Rejects_part_size_one_byte_above_max()
    {
        var error = MultipartSettingsValidator.Validate(
            thresholdBytes: 8L * 1024 * 1024,
            partSizeBytes: S3.MaxPartSizeBytes + 1,
            S3);
        Assert.NotNull(error);
        Assert.Contains("5 GiB", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Rejects_zero_threshold()
    {
        var error = MultipartSettingsValidator.Validate(
            thresholdBytes: 0,
            partSizeBytes: S3.MinPartSizeBytes,
            S3);
        Assert.NotNull(error);
        Assert.Contains("multipart-threshold", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Rejects_negative_threshold()
    {
        var error = MultipartSettingsValidator.Validate(
            thresholdBytes: -1,
            partSizeBytes: S3.MinPartSizeBytes,
            S3);
        Assert.NotNull(error);
    }

    [Fact]
    public void Allows_threshold_below_part_size()
    {
        // A threshold below part size means files between threshold and part size
        // become a single-part multipart upload — unusual but legal under S3.
        Assert.Null(MultipartSettingsValidator.Validate(
            thresholdBytes: 6L * 1024 * 1024,
            partSizeBytes: 16L * 1024 * 1024,
            S3));
    }

    [Fact]
    public void S3_capabilities_match_documented_S3_caps()
    {
        // The S3 service caps multipart uploads at 10 000 parts, 5 MiB minimum
        // part, 5 GiB maximum part. Surfacing the constants lets callers compute
        // "is this file size achievable with these settings" against a single
        // source of truth.
        Assert.Equal(S3Backend.MinMultipartPartSizeBytes, S3.MinPartSizeBytes);
        Assert.Equal(S3Backend.MaxMultipartPartSizeBytes, S3.MaxPartSizeBytes);
        Assert.Equal(10_000, S3.MaxPartCount);
    }

    [Fact]
    public void Validator_uses_caller_supplied_capabilities_for_bounds()
    {
        // Stand in for a future backend with a tighter ceiling. The validator
        // must pick its limits up from the capabilities instance, not from any
        // S3-baked constant.
        var tighter = new MultipartCapabilities(
            MinPartSizeBytes: 8L * 1024 * 1024,
            MaxPartSizeBytes: 1L * 1024 * 1024 * 1024,
            MaxPartCount: 5_000);

        // Below S3's 5 MiB minimum but above the tighter 8 MiB minimum.
        var atTighterMin = MultipartSettingsValidator.Validate(
            thresholdBytes: 1L * 1024 * 1024,
            partSizeBytes: tighter.MinPartSizeBytes,
            tighter);
        Assert.Null(atTighterMin);

        var belowTighterMin = MultipartSettingsValidator.Validate(
            thresholdBytes: 1L * 1024 * 1024,
            partSizeBytes: 6L * 1024 * 1024, // legal under S3, illegal under tighter
            tighter);
        Assert.NotNull(belowTighterMin);
        Assert.Contains("8 MiB", belowTighterMin, StringComparison.Ordinal);
    }
}
