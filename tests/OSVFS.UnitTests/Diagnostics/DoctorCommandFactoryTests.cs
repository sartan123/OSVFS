using OSVFS.Credentials;
using OSVFS.Diagnostics;
using OSVFS.UnitTests.Credentials;
using System.CommandLine;
using Xunit;

namespace OSVFS.UnitTests.Diagnostics;

/// <summary>
/// CLI-wiring smoke tests for <c>osvfs doctor</c>. The full action body needs
/// real ProjFS / network state to be meaningful, so this fixture only verifies
/// that the command is constructed with the documented option surface and that
/// each option parses to the correct value. The behavioral checks live in the
/// dedicated tests for each <see cref="IDoctorCheck"/> implementation and in
/// <see cref="OsvfsDoctorTests"/>.
/// </summary>
public sealed class DoctorCommandFactoryTests
{
    [Fact]
    public void Build_creates_a_doctor_command_with_the_documented_options()
    {
        var command = DoctorCommandFactory.Build(new FakeCredentialStore(), new MountCliOptions());

        Assert.Equal("doctor", command.Name);
        var optionNames = command.Options.Select(o => o.Name).ToHashSet(StringComparer.Ordinal);
        Assert.Contains("--bucket", optionNames);
        Assert.Contains("--region", optionNames);
        Assert.Contains("--profile", optionNames);
        Assert.Contains("--endpoint-url", optionNames);
        // Process-level flags from MountCliOptions are also attached so the operator
        // can run e.g. `osvfs doctor --verbose --config some.toml` without surprise.
        Assert.Contains("--verbose", optionNames);
        Assert.Contains("--log-format", optionNames);
        Assert.Contains("--config", optionNames);
    }

    [Fact]
    public void Bucket_region_profile_endpoint_url_parse_to_string_values()
    {
        var command = DoctorCommandFactory.Build(new FakeCredentialStore(), new MountCliOptions());

        var parsed = command.Parse([
            "--bucket", "demo-bucket",
            "--region", "eu-central-1",
            "--profile", "personal",
            "--endpoint-url", "http://localhost:4566",
        ]);

        // The parser surface is what the operator types; verifying it parses cleanly
        // catches accidental option-name regressions.
        Assert.Empty(parsed.Errors);
        Assert.Equal("demo-bucket", parsed.GetValue<string?>("--bucket"));
        Assert.Equal("eu-central-1", parsed.GetValue<string?>("--region"));
        Assert.Equal("personal", parsed.GetValue<string?>("--profile"));
        Assert.Equal("http://localhost:4566", parsed.GetValue<string?>("--endpoint-url"));
    }
}
