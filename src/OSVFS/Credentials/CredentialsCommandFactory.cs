using System.CommandLine;
using System.Runtime.Versioning;
using System.Text;
using OSVFS.Credentials.Sso;
using OSVFS.ObjectStore;

namespace OSVFS.Credentials;

/// <summary>
/// Builds the <c>credentials</c> sub-command tree backing the <see cref="WindowsCredentialStore"/>.
/// Encapsulates the System.CommandLine wiring so <c>Program.cs</c> only deals with composition.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class CredentialsCommandFactory
{
    /// <summary>
    /// Constructs the <c>credentials</c> sub-command and its <c>set/get/list/remove</c> children.
    /// </summary>
    public static Command Build(IAwsCredentialStore store) => Build(store, DefaultSsoServiceFactory);

    /// <summary>
    /// Constructs the <c>credentials</c> sub-command with an injectable SSO-service factory
    /// so tests can drive the <c>sso</c> subcommand without touching real AWS endpoints.
    /// </summary>
    internal static Command Build(IAwsCredentialStore store, Func<SsoLoginParameters, TextWriter, SsoLoginService> ssoFactory)
    {
        var credentials = new Command(
            "credentials",
            "Manage AWS credentials encrypted with DPAPI and stored in Windows Credential Manager.");

        credentials.Subcommands.Add(BuildSetCommand(store));
        credentials.Subcommands.Add(BuildGetCommand(store));
        credentials.Subcommands.Add(BuildRemoveCommand(store));
        credentials.Subcommands.Add(BuildListCommand(store));
        credentials.Subcommands.Add(BuildSsoCommand(ssoFactory));
        return credentials;
    }

    /// <summary>
    /// Default factory used in production: wires the real SDK-backed flow client,
    /// the Windows-Cred-Manager token cache, and the system browser launcher.
    /// </summary>
    private static SsoLoginService DefaultSsoServiceFactory(SsoLoginParameters parameters, TextWriter output)
    {
        var flowClient = new AwsSsoFlowClient(parameters.Region, TimeProvider.System);
        var tokenCache = new WindowsSsoTokenCache();
        var credentialStore = new WindowsCredentialStore();
        var browserLauncher = new DefaultBrowserLauncher();
        return new SsoLoginService(
            flowClient, tokenCache, credentialStore, browserLauncher, TimeProvider.System, output);
    }

    /// <summary>
    /// Builds <c>credentials set --profile &lt;name&gt;</c>. Missing access/secret keys are
    /// read interactively (the secret via masked input).
    /// </summary>
    private static Command BuildSetCommand(IAwsCredentialStore store)
    {
        var profile = new Option<string>("--profile")
        {
            Description = "Profile name to associate with the stored credential.",
            Required = true,
        };
        var accessKey = new Option<string?>("--access-key")
        {
            Description = "AWS access key ID. When omitted, the value is read from stdin.",
        };
        var secretKey = new Option<string?>("--secret-key")
        {
            Description = "AWS secret access key. When omitted, the value is read from stdin (masked).",
        };
        var sessionToken = new Option<string?>("--session-token")
        {
            Description = "Optional STS session token for temporary credentials.",
        };

        var command = new Command("set", "Save (or replace) AWS credentials for a profile.")
        {
            profile,
            accessKey,
            secretKey,
            sessionToken,
        };
        command.SetAction(parseResult =>
        {
            var profileName = parseResult.GetValue(profile)!;
            var ak = parseResult.GetValue(accessKey);
            var sk = parseResult.GetValue(secretKey);
            var st = parseResult.GetValue(sessionToken);

            if (string.IsNullOrEmpty(ak))
            {
                Console.Write("AWS Access Key ID: ");
                ak = Console.ReadLine();
            }
            if (string.IsNullOrEmpty(sk))
            {
                sk = ReadMasked("AWS Secret Access Key: ");
            }
            if (string.IsNullOrWhiteSpace(ak) || string.IsNullOrWhiteSpace(sk))
            {
                Console.Error.WriteLine("Access key and secret are both required.");
                return 1;
            }

            store.Save(profileName, new AwsCredential
            {
                AccessKeyId = ak.Trim(),
                SecretAccessKey = sk,
                SessionToken = string.IsNullOrEmpty(st) ? null : st,
            });
            Console.WriteLine($"Saved profile '{profileName}'.");
            return 0;
        });
        return command;
    }

    /// <summary>
    /// Builds <c>credentials get --profile &lt;name&gt;</c>. Prints the access key id and
    /// session-token presence flag without ever revealing the secret.
    /// </summary>
    private static Command BuildGetCommand(IAwsCredentialStore store)
    {
        var profile = new Option<string>("--profile")
        {
            Description = "Profile name to inspect.",
            Required = true,
        };
        var command = new Command("get", "Print metadata for a stored profile (the secret is never echoed).")
        {
            profile,
        };
        command.SetAction(parseResult =>
        {
            var profileName = parseResult.GetValue(profile)!;
            var credential = store.Load(profileName);
            if (credential is null)
            {
                Console.Error.WriteLine($"No credential found for profile '{profileName}'.");
                return 1;
            }

            Console.WriteLine($"Profile:          {profileName}");
            Console.WriteLine($"AccessKeyId:      {credential.AccessKeyId}");
            Console.WriteLine($"SecretAccessKey:  (hidden, {credential.SecretAccessKey.Length} chars)");
            Console.WriteLine(
                $"SessionToken:     {(string.IsNullOrEmpty(credential.SessionToken) ? "(none)" : "(present)")}");
            return 0;
        });
        return command;
    }

    /// <summary>
    /// Builds <c>credentials remove --profile &lt;name&gt;</c>.
    /// </summary>
    private static Command BuildRemoveCommand(IAwsCredentialStore store)
    {
        var profile = new Option<string>("--profile")
        {
            Description = "Profile name to delete.",
            Required = true,
        };
        var command = new Command("remove", "Delete the stored credential for a profile.")
        {
            profile,
        };
        command.SetAction(parseResult =>
        {
            var profileName = parseResult.GetValue(profile)!;
            if (store.Delete(profileName))
            {
                Console.WriteLine($"Removed profile '{profileName}'.");
                return 0;
            }
            Console.Error.WriteLine($"No credential found for profile '{profileName}'.");
            return 1;
        });
        return command;
    }

    /// <summary>
    /// Builds <c>credentials list</c>, printing every profile owned by OSVFS.
    /// </summary>
    private static Command BuildListCommand(IAwsCredentialStore store)
    {
        var command = new Command("list", "List every profile stored by OSVFS.");
        command.SetAction(_ =>
        {
            var profiles = store.List();
            if (profiles.Count == 0)
            {
                Console.WriteLine("(no profiles stored)");
                return 0;
            }
            foreach (var profile in profiles)
            {
                Console.WriteLine(profile);
            }
            return 0;
        });
        return command;
    }

    /// <summary>
    /// Builds <c>credentials sso --start-url ... --region ... --account-id ... --role-name ... --profile ...</c>
    /// which runs the IAM Identity Center device-authorization flow, opens the browser
    /// to approve the request, and saves the resulting role credentials under the
    /// requested local profile.
    /// </summary>
    private static Command BuildSsoCommand(Func<SsoLoginParameters, TextWriter, SsoLoginService> ssoFactory)
    {
        var startUrl = new Option<string>("--start-url")
        {
            Description = "IAM Identity Center user-portal start URL (e.g. https://my-org.awsapps.com/start).",
            Required = true,
        };
        var region = new Option<string>("--region")
        {
            Description = "AWS region that hosts the IAM Identity Center instance.",
            Required = true,
        };
        var accountId = new Option<string>("--account-id")
        {
            Description = "AWS account ID to retrieve role credentials for.",
            Required = true,
        };
        var roleName = new Option<string>("--role-name")
        {
            Description = "Permission-set role name to assume in the target account.",
            Required = true,
        };
        var profile = new Option<string>("--profile")
        {
            Description = "Local OSVFS profile name to save the resulting role credentials under.",
            Required = true,
        };

        var command = new Command(
            "sso",
            "Sign in via AWS IAM Identity Center (SSO) and store the resulting role credentials.")
        {
            startUrl,
            region,
            accountId,
            roleName,
            profile,
        };
        command.SetAction((parseResult, cancellationToken) =>
        {
            var parameters = new SsoLoginParameters
            {
                StartUrl = parseResult.GetValue(startUrl)!,
                Region = parseResult.GetValue(region)!,
                AccountId = parseResult.GetValue(accountId)!,
                RoleName = parseResult.GetValue(roleName)!,
                ProfileName = parseResult.GetValue(profile)!,
            };

            var service = ssoFactory(parameters, Console.Out);
            return RunSsoLoginAsync(service, parameters, cancellationToken);
        });
        return command;
    }

    /// <summary>
    /// Awaits the SSO login flow and translates known failure modes into terse stderr
    /// messages plus a non-zero exit code.
    /// </summary>
    private static async Task<int> RunSsoLoginAsync(
        SsoLoginService service, SsoLoginParameters parameters, CancellationToken cancellationToken)
    {
        try
        {
            await service.LoginAsync(parameters, cancellationToken).ConfigureAwait(false);
            return 0;
        }
        catch (SsoExpiredTokenException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
        catch (SsoLoginException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("SSO login cancelled.");
            return 1;
        }
    }

    /// <summary>
    /// Reads a line from stdin without echoing the characters. Backspace is honored so the
    /// user can correct typos; the prompt is written before the loop and a newline after.
    /// </summary>
    private static string ReadMasked(string prompt)
    {
        Console.Write(prompt);
        var buffer = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            switch (key.Key)
            {
                case ConsoleKey.Enter:
                    Console.WriteLine();
                    return buffer.ToString();
                case ConsoleKey.Backspace:
                    if (buffer.Length > 0) buffer.Length--;
                    break;
                default:
                    if (key.KeyChar != '\0' && !char.IsControl(key.KeyChar))
                    {
                        buffer.Append(key.KeyChar);
                    }
                    break;
            }
        }
    }
}
