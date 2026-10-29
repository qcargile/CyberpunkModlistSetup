


namespace ChromeBlood.Core.Acquisition;

public static class VcRuntimePackage
{
    public const string PrerequisiteId = "vc-runtime-x64";
    public const string Version = "14.51.36247.0";
    public const string Sha256 = "843068991daaa1f73ad9f6239bce4d0f6a07a51f18c37ea2a867e9beca71295c";
    public const string Signer = "CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US";
    public const long MaximumBytes = 20_000_000;

    public static DownloadRequest Download => new(
        new Uri("https://download.visualstudio.microsoft.com/download/pr/ebdab8e5-1d7b-4d9f-a11b-cbb1720c3b12/843068991DAAA1F73AD9F6239BCE4D0F6A07A51F18C37EA2A867E9BECA71295C/VC_redist.x64.exe"),
        Sha256,
        "win-x64",
        Version,
        SetupAcquisitionKind.Direct,
        true,
        Signer,
        MaximumBytes);

    public static string CacheRoot => Path.Combine(Preparation.PreparationPackages.StateRoot, "packages");

    public static string CacheId => PackageCache.GetCacheId(Download);
}

