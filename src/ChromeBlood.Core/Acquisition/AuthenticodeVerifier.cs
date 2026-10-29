using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace ChromeBlood.Core.Acquisition;

[SuppressMessage("Naming", "CA1720:Identifier contains type name")]
public enum AuthenticodeStatus
{
    Trusted,
    Untrusted,
    Unsigned,
    SignerMismatch,
    Unavailable,
    NotRequired
}

public sealed record AuthenticodeVerdict(AuthenticodeStatus Status, string? SignerIdentity, int NativeError);

public interface IAuthenticodeVerifier
{
    AuthenticodeVerdict Verify(SafeFileHandle handle, string path, string? expectedSigner, CancellationToken cancellationToken);
}

internal sealed record NativeAuthenticodeResult(
    AuthenticodeStatus Status,
    string? SignerIdentity,
    int NativeError,
    uint VerifiedSignatureIndex,
    uint SecondarySignatureCount);

internal interface IAuthenticodeTrustApi
{
    NativeAuthenticodeResult Verify(SafeFileHandle handle, string path, uint signatureIndex, bool discoverSecondaryCount);
}

internal interface IWinTrustNativeApi
{
    int Verify(ref Guid action, ref WinTrustData data);

    string? ReadSignerIdentity(nint stateData);
}

[StructLayout(LayoutKind.Sequential)]
internal struct WinTrustFileInfo
{
    public uint StructureSize;
    public nint FilePath;
    public nint FileHandle;
    public nint KnownSubject;
}

[StructLayout(LayoutKind.Sequential)]
internal struct WinTrustSignatureSettings
{
    public uint StructureSize;
    public uint Index;
    public uint Flags;
    public uint SecondarySignatures;
    public uint VerifiedSignatureIndex;
    public nint CryptoPolicy;
}

[StructLayout(LayoutKind.Sequential)]
internal struct WinTrustData
{
    public uint StructureSize;
    public nint PolicyCallbackData;
    public nint SipClientData;
    public uint UiChoice;
    public uint RevocationChecks;
    public uint UnionChoice;
    public nint FileInfo;
    public uint StateAction;
    public nint StateData;
    public nint UrlReference;
    public uint ProviderFlags;
    public uint UiContext;
    public nint SignatureSettings;
}

public sealed class AuthenticodeVerifier : IAuthenticodeVerifier
{
    private const uint MaximumSignatureCount = 32;
    private readonly IAuthenticodeTrustApi _trustApi;

    public AuthenticodeVerifier()
        : this(new WindowsAuthenticodeTrustApi())
    {
    }

    internal AuthenticodeVerifier(IAuthenticodeTrustApi trustApi)
    {
        ArgumentNullException.ThrowIfNull(trustApi);
        _trustApi = trustApi;
    }

    public AuthenticodeVerdict Verify(string path, string? expectedSigner)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string fullPath = Path.GetFullPath(path);
        WindowsSecureFileApi api = new();
        SafeFileHandle? handle;
        try
        {
            handle = api.Open(new SecureFileOpenRequest(
                fullPath,
                SecureFileAccess.Read,
                FileShare.Read,
                SecureFileDisposition.OpenExisting,
                SecureFileOpenOptions.OpenReparsePoint | SecureFileOpenOptions.Sequential));
        }
        catch (SecureFileOpenException exception)
        {
            return new AuthenticodeVerdict(AuthenticodeStatus.Unavailable, null, exception.NativeError);
        }

        if (handle is null)
        {
            return new AuthenticodeVerdict(AuthenticodeStatus.Unavailable, null, 2);
        }

        using (handle)
        {
            try
            {
                SecureFileMetadata metadata = api.GetMetadata(handle);
                if (metadata.IsDirectory || metadata.IsReparsePoint)
                {
                    return new AuthenticodeVerdict(AuthenticodeStatus.Unavailable, null, 0);
                }

                return Verify(handle, SecureCacheRoot.NormalizeFinalPath(api.GetFinalPath(handle)), expectedSigner, CancellationToken.None);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ExternalException)
            {
                return new AuthenticodeVerdict(AuthenticodeStatus.Unavailable, null, exception.HResult);
            }
        }
    }

    public AuthenticodeVerdict Verify(SafeFileHandle handle, string path, string? expectedSigner, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (handle.IsInvalid || handle.IsClosed)
        {
            throw new ArgumentException("The package file handle must be open and valid.", nameof(handle));
        }

        if (expectedSigner is not null && string.IsNullOrWhiteSpace(expectedSigner))
        {
            throw new ArgumentException("The expected signer must not be blank.", nameof(expectedSigner));
        }

        cancellationToken.ThrowIfCancellationRequested();
        NativeAuthenticodeResult primary = VerifyNative(handle, path, 0, expectedSigner is not null);
        cancellationToken.ThrowIfCancellationRequested();
        if (primary.VerifiedSignatureIndex != 0)
        {
            return Unavailable(primary.NativeError);
        }

        if (expectedSigner is null)
        {
            return Verdict(primary);
        }

        if (primary.Status == AuthenticodeStatus.Unsigned)
        {
            return Verdict(primary);
        }

        if (primary.SecondarySignatureCount > MaximumSignatureCount)
        {
            return Unavailable(primary.NativeError);
        }

        string normalizedExpected = NormalizeSignerIdentity(expectedSigner);
        bool trustedMismatch = false;
        bool untrusted = false;
        bool unavailable = false;
        string? mismatchSigner = null;
        NativeAuthenticodeResult last = primary;
        for (uint index = 0; index <= primary.SecondarySignatureCount; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            NativeAuthenticodeResult current = index == 0
                ? primary
                : VerifyNative(handle, path, index, false);
            cancellationToken.ThrowIfCancellationRequested();
            last = current;
            if (current.VerifiedSignatureIndex != index)
            {
                unavailable = true;
            }
            else if (current.Status == AuthenticodeStatus.Trusted)
            {
                if (current.SignerIdentity is null)
                {
                    unavailable = true;
                }
                else if (string.Equals(NormalizeSignerIdentity(current.SignerIdentity), normalizedExpected, StringComparison.OrdinalIgnoreCase))
                {
                    return Verdict(current);
                }
                else
                {
                    trustedMismatch = true;
                    mismatchSigner = current.SignerIdentity;
                }
            }
            else if (current.Status == AuthenticodeStatus.Unavailable)
            {
                unavailable = true;
            }
            else if (current.Status == AuthenticodeStatus.Untrusted)
            {
                untrusted = true;
            }
        }

        if (unavailable)
        {
            return Unavailable(last.NativeError);
        }

        if (trustedMismatch)
        {
            return new AuthenticodeVerdict(AuthenticodeStatus.SignerMismatch, mismatchSigner, last.NativeError);
        }

        return new AuthenticodeVerdict(untrusted ? AuthenticodeStatus.Untrusted : last.Status, last.SignerIdentity, last.NativeError);
    }

    internal static AuthenticodeStatus ClassifyNativeError(int error)
        => error switch
        {
            0 => AuthenticodeStatus.Trusted,
            unchecked((int)0x800B0100) => AuthenticodeStatus.Unsigned,
            unchecked((int)0x800B0001) => AuthenticodeStatus.Unavailable,
            unchecked((int)0x800B0002) => AuthenticodeStatus.Unavailable,
            unchecked((int)0x800B0003) => AuthenticodeStatus.Unavailable,
            unchecked((int)0x80092003) => AuthenticodeStatus.Unavailable,
            unchecked((int)0x80092011) => AuthenticodeStatus.Unavailable,
            unchecked((int)0x80092012) => AuthenticodeStatus.Unavailable,
            unchecked((int)0x80092013) => AuthenticodeStatus.Unavailable,
            unchecked((int)0x800B010E) => AuthenticodeStatus.Unavailable,
            unchecked((int)0x80070002) => AuthenticodeStatus.Unavailable,
            unchecked((int)0x80070003) => AuthenticodeStatus.Unavailable,
            unchecked((int)0x80070005) => AuthenticodeStatus.Unavailable,
            _ => AuthenticodeStatus.Untrusted
        };

    internal static string NormalizeSignerIdentity(string value)
        => string.Join(' ', value.Normalize(NormalizationForm.FormKC).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private NativeAuthenticodeResult VerifyNative(SafeFileHandle handle, string path, uint index, bool discoverSecondaryCount)
    {
        try
        {
            return _trustApi.Verify(handle, path, index, discoverSecondaryCount);
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or UnauthorizedAccessException or ExternalException)
        {
            return new NativeAuthenticodeResult(AuthenticodeStatus.Unavailable, null, exception.HResult, index, 0);
        }
    }

    private static AuthenticodeVerdict Verdict(NativeAuthenticodeResult result)
        => new(result.Status, result.SignerIdentity, result.NativeError);

    private static AuthenticodeVerdict Unavailable(int error)
        => new(AuthenticodeStatus.Unavailable, null, error);
}

internal sealed class WindowsAuthenticodeTrustApi : IAuthenticodeTrustApi
{
    internal const uint WholeChainRevocation = 1;
    internal const uint VerifySpecific = 0x00000001;
    internal const uint GetSecondarySignatureCount = 0x00000002;
    internal const uint StateActionVerify = 0x00000001;
    internal const uint StateActionClose = 0x00000002;
    internal const uint OfflineChainProviderFlags = 0x00000080 | 0x00001000;
    private const uint UiNone = 2;
    private const uint ChoiceFile = 1;
    private static readonly Guid GenericVerifyAction = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");
    private readonly IWinTrustNativeApi _native;

    public WindowsAuthenticodeTrustApi()
        : this(new WinTrustNativeApi())
    {
    }

    internal WindowsAuthenticodeTrustApi(IWinTrustNativeApi native)
    {
        ArgumentNullException.ThrowIfNull(native);
        _native = native;
    }

    public NativeAuthenticodeResult Verify(SafeFileHandle handle, string path, uint signatureIndex, bool discoverSecondaryCount)
    {
        nint pathPointer = Marshal.StringToCoTaskMemUni(path);
        nint fileInfoPointer = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustFileInfo>());
        nint signatureSettingsPointer = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustSignatureSettings>());
        bool handleReference = false;
        WinTrustData trustData = default;
        int verifyError = 0;
        int closeError = 0;
        int signerError = 0;
        bool signerExtractionFailed = false;
        string? signer = null;
        WinTrustSignatureSettings returnedSettings = default;
        try
        {
            handle.DangerousAddRef(ref handleReference);
            WinTrustFileInfo fileInfo = new()
            {
                StructureSize = (uint)Marshal.SizeOf<WinTrustFileInfo>(),
                FilePath = pathPointer,
                FileHandle = handle.DangerousGetHandle()
            };
            WinTrustSignatureSettings signatureSettings = new()
            {
                StructureSize = (uint)Marshal.SizeOf<WinTrustSignatureSettings>(),
                Index = signatureIndex,
                Flags = VerifySpecific | (discoverSecondaryCount ? GetSecondarySignatureCount : 0)
            };
            Marshal.StructureToPtr(fileInfo, fileInfoPointer, false);
            Marshal.StructureToPtr(signatureSettings, signatureSettingsPointer, false);
            trustData = new WinTrustData
            {
                StructureSize = (uint)Marshal.SizeOf<WinTrustData>(),
                UiChoice = UiNone,
                RevocationChecks = WholeChainRevocation,
                UnionChoice = ChoiceFile,
                FileInfo = fileInfoPointer,
                StateAction = StateActionVerify,
                ProviderFlags = OfflineChainProviderFlags,
                SignatureSettings = signatureSettingsPointer
            };
            Guid action = GenericVerifyAction;
            verifyError = _native.Verify(ref action, ref trustData);
            returnedSettings = Marshal.PtrToStructure<WinTrustSignatureSettings>(signatureSettingsPointer);
            if (verifyError == 0 && returnedSettings.VerifiedSignatureIndex == signatureIndex)
            {
                try
                {
                    signer = _native.ReadSignerIdentity(trustData.StateData);
                }
                catch (Exception exception) when (exception is CryptographicException or ArgumentException or OverflowException or InvalidOperationException or ExternalException)
                {
                    signerExtractionFailed = true;
                    signerError = exception.HResult;
                }
            }
        }
        finally
        {
            try
            {
                trustData.StateAction = StateActionClose;
                Guid action = GenericVerifyAction;
                closeError = _native.Verify(ref action, ref trustData);
            }
            finally
            {
                if (handleReference)
                {
                    handle.DangerousRelease();
                }

                Marshal.FreeHGlobal(signatureSettingsPointer);
                Marshal.FreeHGlobal(fileInfoPointer);
                Marshal.FreeCoTaskMem(pathPointer);
            }
        }

        if (closeError != 0)
        {
            return new NativeAuthenticodeResult(AuthenticodeStatus.Unavailable, null, closeError, returnedSettings.VerifiedSignatureIndex, returnedSettings.SecondarySignatures);
        }

        if (signerExtractionFailed)
        {
            return new NativeAuthenticodeResult(AuthenticodeStatus.Unavailable, null, signerError, returnedSettings.VerifiedSignatureIndex, returnedSettings.SecondarySignatures);
        }

        AuthenticodeStatus status = AuthenticodeVerifier.ClassifyNativeError(verifyError);
        if (status == AuthenticodeStatus.Trusted && signer is null)
        {
            status = AuthenticodeStatus.Unavailable;
        }

        return new NativeAuthenticodeResult(status, signer, verifyError, returnedSettings.VerifiedSignatureIndex, returnedSettings.SecondarySignatures);
    }

    internal static string? SignerCertificateSubject(X509Certificate2 certificate)
    {
        string subject = certificate.Subject;
        return string.IsNullOrWhiteSpace(subject) ? null : subject;
    }

    private sealed class WinTrustNativeApi : IWinTrustNativeApi
    {
        public int Verify(ref Guid action, ref WinTrustData data)
            => WinVerifyTrust(new nint(-1), ref action, ref data);

        public string? ReadSignerIdentity(nint stateData)
        {
            if (stateData == 0)
            {
                return null;
            }

            nint providerData = WTHelperProvDataFromStateData(stateData);
            if (providerData == 0)
            {
                return null;
            }

            nint signer = WTHelperGetProvSignerFromChain(providerData, 0, false, 0);
            if (signer == 0)
            {
                return null;
            }

            nint providerCertificate = WTHelperGetProvCertFromChain(signer, 0);
            if (providerCertificate == 0)
            {
                return null;
            }

            CryptProviderCertificate certificate = Marshal.PtrToStructure<CryptProviderCertificate>(providerCertificate);
            if (certificate.CertificateContext == 0)
            {
                return null;
            }

            CertificateContext context = Marshal.PtrToStructure<CertificateContext>(certificate.CertificateContext);
            byte[] encoded = new byte[checked((int)context.EncodedLength)];
            Marshal.Copy(context.EncodedBytes, encoded, 0, encoded.Length);
            using X509Certificate2 signerCertificate = X509CertificateLoader.LoadCertificate(encoded);
            return SignerCertificateSubject(signerCertificate);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct CryptProviderCertificate
        {
            public uint StructureSize;
            public nint CertificateContext;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct CertificateContext
        {
            public uint EncodingType;
            public nint EncodedBytes;
            public uint EncodedLength;
            public nint CertificateInfo;
            public nint CertificateStore;
        }

        [SuppressMessage("Interoperability", "SYSLIB1054:Use LibraryImportAttribute instead of DllImportAttribute")]
        [DllImport("wintrust.dll", PreserveSig = true)]
        private static extern int WinVerifyTrust(nint window, ref Guid action, ref WinTrustData trustData);

        [SuppressMessage("Interoperability", "SYSLIB1054:Use LibraryImportAttribute instead of DllImportAttribute")]
        [DllImport("wintrust.dll")]
        private static extern nint WTHelperProvDataFromStateData(nint stateData);

        [SuppressMessage("Interoperability", "SYSLIB1054:Use LibraryImportAttribute instead of DllImportAttribute")]
        [DllImport("wintrust.dll")]
        private static extern nint WTHelperGetProvSignerFromChain(nint providerData, uint signerIndex, [MarshalAs(UnmanagedType.Bool)] bool counterSigner, uint counterSignerIndex);

        [SuppressMessage("Interoperability", "SYSLIB1054:Use LibraryImportAttribute instead of DllImportAttribute")]
        [DllImport("wintrust.dll")]
        private static extern nint WTHelperGetProvCertFromChain(nint signer, uint certificateIndex);
    }
}
