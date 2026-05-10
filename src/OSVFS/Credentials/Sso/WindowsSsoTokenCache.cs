using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;

namespace OSVFS.Credentials.Sso;

/// <summary>
/// SSO bearer-token cache backed by Windows Credential Manager and DPAPI. Each entry is
/// keyed by the IAM Identity Center start URL and stored under the <c>OSVFS:sso-cache:</c>
/// prefix so it shares the same surface as the rest of the OSVFS-managed credentials but
/// stays distinguishable from per-profile AWS access keys.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed partial class WindowsSsoTokenCache : ISsoTokenCache
{
    /// <summary>Target-name prefix that namespaces every SSO-cache entry written by OSVFS.</summary>
    internal const string TargetPrefix = "OSVFS:sso-cache:";

    /// <summary>UI-visible comment stamped onto every entry.</summary>
    private const string CredentialComment = "OSVFS-managed SSO bearer-token cache";

    /// <summary>CRED_TYPE_GENERIC: arbitrary application credential.</summary>
    private const uint CredTypeGeneric = 1;

    /// <summary>CRED_PERSIST_LOCAL_MACHINE: survives logon sessions on this host but does not roam.</summary>
    private const uint CredPersistLocalMachine = 2;

    /// <summary>Win32 ERROR_NOT_FOUND.</summary>
    private const int ErrorNotFound = 1168;

    /// <summary>Maximum size of CREDENTIAL_BLOB per the wincred contract.</summary>
    private const int CredentialBlobSizeLimit = 5 * 512;

    /// <summary>
    /// DPAPI entropy distinct from <c>WindowsCredentialStore</c>'s entropy so a stray
    /// access-key blob cannot be misread as an SSO cache entry, and vice versa.
    /// </summary>
    private static readonly byte[] DpapiEntropy = "OSVFS:sso-cache:v1"u8.ToArray();

    /// <inheritdoc/>
    public SsoCachedToken? Load(string startUrl)
    {
        ValidateStartUrl(startUrl);

        var target = BuildTargetName(startUrl);
        if (!CredRead(target, CredTypeGeneric, 0, out var credPtr))
        {
            var err = Marshal.GetLastPInvokeError();
            if (err == ErrorNotFound) return null;
            throw new Win32Exception(err, $"CredRead failed for '{target}'.");
        }

        try
        {
            var cred = Marshal.PtrToStructure<Credential>(credPtr);
            var encrypted = ReadBlob(cred.CredentialBlob, cred.CredentialBlobSize);
            byte[] decrypted;
            try
            {
                decrypted = ProtectedData.Unprotect(encrypted, DpapiEntropy, DataProtectionScope.CurrentUser);
            }
            catch (CryptographicException ex)
            {
                throw new InvalidOperationException(
                    $"Failed to decrypt SSO cache for '{startUrl}'. The entry may have been written by another user.",
                    ex);
            }

            try
            {
                var payload = JsonSerializer.Deserialize(
                    decrypted, SsoTokenCacheJsonContext.Default.SsoTokenCachePayload)
                    ?? throw new InvalidOperationException($"SSO cache for '{startUrl}' is malformed.");
                return new SsoCachedToken
                {
                    ClientId = payload.ClientId,
                    ClientSecret = payload.ClientSecret,
                    ClientSecretExpiresAt = payload.ClientSecretExpiresAtUnix is { } secretExp
                        ? DateTimeOffset.FromUnixTimeSeconds(secretExp)
                        : null,
                    AccessToken = payload.AccessToken,
                    AccessTokenExpiresAt = DateTimeOffset.FromUnixTimeSeconds(payload.AccessTokenExpiresAtUnix),
                };
            }
            finally
            {
                Array.Clear(decrypted);
            }
        }
        finally
        {
            CredFree(credPtr);
        }
    }

    /// <inheritdoc/>
    public void Save(string startUrl, SsoCachedToken token)
    {
        ValidateStartUrl(startUrl);
        ArgumentNullException.ThrowIfNull(token);

        var payload = new SsoTokenCachePayload
        {
            ClientId = token.ClientId,
            ClientSecret = token.ClientSecret,
            ClientSecretExpiresAtUnix = token.ClientSecretExpiresAt?.ToUnixTimeSeconds(),
            AccessToken = token.AccessToken,
            AccessTokenExpiresAtUnix = token.AccessTokenExpiresAt.ToUnixTimeSeconds(),
        };

        var json = JsonSerializer.SerializeToUtf8Bytes(payload, SsoTokenCacheJsonContext.Default.SsoTokenCachePayload);
        var encrypted = ProtectedData.Protect(json, DpapiEntropy, DataProtectionScope.CurrentUser);
        if (encrypted.Length > CredentialBlobSizeLimit)
        {
            throw new InvalidOperationException(
                $"Encrypted SSO cache blob exceeds the Cred Manager limit ({CredentialBlobSizeLimit} bytes).");
        }

        WriteCredential(BuildTargetName(startUrl), startUrl, encrypted);
        Array.Clear(json);
    }

    /// <inheritdoc/>
    public bool Delete(string startUrl)
    {
        ValidateStartUrl(startUrl);

        if (CredDelete(BuildTargetName(startUrl), CredTypeGeneric, 0)) return true;
        var err = Marshal.GetLastPInvokeError();
        if (err == ErrorNotFound) return false;
        throw new Win32Exception(err, $"CredDelete failed for '{startUrl}'.");
    }

    /// <summary>Builds the namespaced Cred Manager target name from a start URL.</summary>
    private static string BuildTargetName(string startUrl) => TargetPrefix + startUrl;

    /// <summary>
    /// Validates that the start URL is non-empty and contains no characters that
    /// would conflict with Cred Manager filter wildcards or our prefix delimiter.
    /// </summary>
    private static void ValidateStartUrl(string startUrl)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(startUrl);
        if (startUrl.AsSpan().IndexOfAny('*', '?', '\0') >= 0)
        {
            throw new ArgumentException(
                "Start URL must not contain wildcard or null characters.", nameof(startUrl));
        }
    }

    /// <summary>
    /// Allocates and writes a generic credential, freeing the unmanaged buffers we own
    /// regardless of whether the API call succeeds.
    /// </summary>
    private static void WriteCredential(string target, string userName, byte[] encryptedBlob)
    {
        var targetPtr = Marshal.StringToHGlobalUni(target);
        var userPtr = Marshal.StringToHGlobalUni(userName);
        var commentPtr = Marshal.StringToHGlobalUni(CredentialComment);
        var blobPtr = Marshal.AllocHGlobal(encryptedBlob.Length);
        try
        {
            Marshal.Copy(encryptedBlob, 0, blobPtr, encryptedBlob.Length);

            var cred = new Credential
            {
                Type = CredTypeGeneric,
                TargetName = targetPtr,
                Comment = commentPtr,
                CredentialBlobSize = (uint)encryptedBlob.Length,
                CredentialBlob = blobPtr,
                Persist = CredPersistLocalMachine,
                UserName = userPtr,
            };

            if (!CredWrite(ref cred, 0))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError(), $"CredWrite failed for '{target}'.");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(targetPtr);
            Marshal.FreeHGlobal(userPtr);
            Marshal.FreeHGlobal(commentPtr);
            if (blobPtr != IntPtr.Zero)
            {
                for (var i = 0; i < encryptedBlob.Length; i++)
                {
                    Marshal.WriteByte(blobPtr, i, 0);
                }
                Marshal.FreeHGlobal(blobPtr);
            }
        }
    }

    /// <summary>Copies <paramref name="size"/> bytes from <paramref name="ptr"/> into a managed array.</summary>
    private static byte[] ReadBlob(IntPtr ptr, uint size)
    {
        if (ptr == IntPtr.Zero || size == 0) return [];
        var buffer = new byte[size];
        Marshal.Copy(ptr, buffer, 0, (int)size);
        return buffer;
    }

    /// <summary>CREDENTIALW interop layout — one-to-one with wincred.h.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct Credential
    {
        public uint Flags;
        public uint Type;
        public IntPtr TargetName;
        public IntPtr Comment;
        public long LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public IntPtr TargetAlias;
        public IntPtr UserName;
    }

    /// <summary>CredWriteW: persists or replaces a credential entry.</summary>
    [LibraryImport("advapi32.dll", EntryPoint = "CredWriteW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CredWrite(ref Credential credential, uint flags);

    /// <summary>CredReadW: looks up a credential by exact target name.</summary>
    [LibraryImport("advapi32.dll", EntryPoint = "CredReadW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CredRead(string target, uint type, uint reservedFlag, out IntPtr credentialPtr);

    /// <summary>CredDeleteW: removes a credential by exact target name.</summary>
    [LibraryImport("advapi32.dll", EntryPoint = "CredDeleteW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CredDelete(string target, uint type, uint flags);

    /// <summary>CredFree: releases buffers returned by CredRead.</summary>
    [LibraryImport("advapi32.dll", EntryPoint = "CredFree")]
    private static partial void CredFree(IntPtr buffer);
}
