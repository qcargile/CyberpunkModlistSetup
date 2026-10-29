


namespace ChromeBlood.Core.Acquisition;

public static class DirectXRuntimePackage
{
    public const string PrerequisiteId = "directx-june-2010";
    public const string Version = "9.29.1974.1";
    public const string Sha256 = "053f76dcbb28802e23341b6a787e3b0791c0fa5c8d4d011b1044172dbf89c73b";
    public const string Signer = "CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US";
    public const long Bytes = 100_275_120;
    public const long MaximumBytes = 110_000_000;
    public const int PayloadFileCount = 157;
    public const long PayloadBytes = 102_930_955;
    public const string PayloadSha256 = "9d8432a6bbca48c9e9720b04c74d853876155f9d59b0816a81fed9d6eed39fdb";
    public const string SetupSha256 = "8f47d7121ef6532ad9ad9901e44e237f5c30448b752028c58a9d19521414e40d";
    public const string SetupSigner = "CN=Microsoft Corporation, OU=MOPR, O=Microsoft Corporation, L=Redmond, S=Washington, C=US";
    public static IReadOnlyList<string> PayloadFileNames { get; } = Array.AsReadOnly<string>(
    [
        "APR2007_XACT_x64.cab", "APR2007_XACT_x86.cab", "APR2007_d3dx10_33_x64.cab",
        "APR2007_d3dx10_33_x86.cab", "APR2007_d3dx9_33_x64.cab", "APR2007_d3dx9_33_x86.cab",
        "APR2007_xinput_x64.cab", "APR2007_xinput_x86.cab", "AUG2006_XACT_x64.cab",
        "AUG2006_XACT_x86.cab", "AUG2006_xinput_x64.cab", "AUG2006_xinput_x86.cab",
        "AUG2007_XACT_x64.cab", "AUG2007_XACT_x86.cab", "AUG2007_d3dx10_35_x64.cab",
        "AUG2007_d3dx10_35_x86.cab", "AUG2007_d3dx9_35_x64.cab", "AUG2007_d3dx9_35_x86.cab",
        "Apr2005_d3dx9_25_x64.cab", "Apr2005_d3dx9_25_x86.cab", "Apr2006_MDX1_x86.cab",
        "Apr2006_MDX1_x86_Archive.cab", "Apr2006_XACT_x64.cab", "Apr2006_XACT_x86.cab",
        "Apr2006_d3dx9_30_x64.cab", "Apr2006_d3dx9_30_x86.cab", "Apr2006_xinput_x64.cab",
        "Apr2006_xinput_x86.cab", "Aug2005_d3dx9_27_x64.cab", "Aug2005_d3dx9_27_x86.cab",
        "Aug2008_XACT_x64.cab", "Aug2008_XACT_x86.cab", "Aug2008_XAudio_x64.cab",
        "Aug2008_XAudio_x86.cab", "Aug2008_d3dx10_39_x64.cab", "Aug2008_d3dx10_39_x86.cab",
        "Aug2008_d3dx9_39_x64.cab", "Aug2008_d3dx9_39_x86.cab", "Aug2009_D3DCompiler_42_x64.cab",
        "Aug2009_D3DCompiler_42_x86.cab", "Aug2009_XACT_x64.cab", "Aug2009_XACT_x86.cab",
        "Aug2009_XAudio_x64.cab", "Aug2009_XAudio_x86.cab", "Aug2009_d3dcsx_42_x64.cab",
        "Aug2009_d3dcsx_42_x86.cab", "Aug2009_d3dx10_42_x64.cab", "Aug2009_d3dx10_42_x86.cab",
        "Aug2009_d3dx11_42_x64.cab", "Aug2009_d3dx11_42_x86.cab", "Aug2009_d3dx9_42_x64.cab",
        "Aug2009_d3dx9_42_x86.cab", "DEC2006_XACT_x64.cab", "DEC2006_XACT_x86.cab",
        "DEC2006_d3dx10_00_x64.cab", "DEC2006_d3dx10_00_x86.cab", "DEC2006_d3dx9_32_x64.cab",
        "DEC2006_d3dx9_32_x86.cab", "DSETUP.dll", "DXSETUP.exe", "Dec2005_d3dx9_28_x64.cab",
        "Dec2005_d3dx9_28_x86.cab", "FEB2007_XACT_x64.cab", "FEB2007_XACT_x86.cab",
        "Feb2005_d3dx9_24_x64.cab", "Feb2005_d3dx9_24_x86.cab", "Feb2006_XACT_x64.cab",
        "Feb2006_XACT_x86.cab", "Feb2006_d3dx9_29_x64.cab", "Feb2006_d3dx9_29_x86.cab",
        "Feb2010_X3DAudio_x64.cab", "Feb2010_X3DAudio_x86.cab", "Feb2010_XACT_x64.cab",
        "Feb2010_XACT_x86.cab", "Feb2010_XAudio_x64.cab", "Feb2010_XAudio_x86.cab",
        "JUN2006_XACT_x64.cab", "JUN2006_XACT_x86.cab", "JUN2007_XACT_x64.cab",
        "JUN2007_XACT_x86.cab", "JUN2007_d3dx10_34_x64.cab", "JUN2007_d3dx10_34_x86.cab",
        "JUN2007_d3dx9_34_x64.cab", "JUN2007_d3dx9_34_x86.cab", "JUN2008_X3DAudio_x64.cab",
        "JUN2008_X3DAudio_x86.cab", "JUN2008_XACT_x64.cab", "JUN2008_XACT_x86.cab",
        "JUN2008_XAudio_x64.cab", "JUN2008_XAudio_x86.cab", "JUN2008_d3dx10_38_x64.cab",
        "JUN2008_d3dx10_38_x86.cab", "JUN2008_d3dx9_38_x64.cab", "JUN2008_d3dx9_38_x86.cab",
        "Jun2005_d3dx9_26_x64.cab", "Jun2005_d3dx9_26_x86.cab", "Jun2010_D3DCompiler_43_x64.cab",
        "Jun2010_D3DCompiler_43_x86.cab", "Jun2010_XACT_x64.cab", "Jun2010_XACT_x86.cab",
        "Jun2010_XAudio_x64.cab", "Jun2010_XAudio_x86.cab", "Jun2010_d3dcsx_43_x64.cab",
        "Jun2010_d3dcsx_43_x86.cab", "Jun2010_d3dx10_43_x64.cab", "Jun2010_d3dx10_43_x86.cab",
        "Jun2010_d3dx11_43_x64.cab", "Jun2010_d3dx11_43_x86.cab", "Jun2010_d3dx9_43_x64.cab",
        "Jun2010_d3dx9_43_x86.cab", "Mar2008_X3DAudio_x64.cab", "Mar2008_X3DAudio_x86.cab",
        "Mar2008_XACT_x64.cab", "Mar2008_XACT_x86.cab", "Mar2008_XAudio_x64.cab",
        "Mar2008_XAudio_x86.cab", "Mar2008_d3dx10_37_x64.cab", "Mar2008_d3dx10_37_x86.cab",
        "Mar2008_d3dx9_37_x64.cab", "Mar2008_d3dx9_37_x86.cab", "Mar2009_X3DAudio_x64.cab",
        "Mar2009_X3DAudio_x86.cab", "Mar2009_XACT_x64.cab", "Mar2009_XACT_x86.cab",
        "Mar2009_XAudio_x64.cab", "Mar2009_XAudio_x86.cab", "Mar2009_d3dx10_41_x64.cab",
        "Mar2009_d3dx10_41_x86.cab", "Mar2009_d3dx9_41_x64.cab", "Mar2009_d3dx9_41_x86.cab",
        "NOV2007_X3DAudio_x64.cab", "NOV2007_X3DAudio_x86.cab", "NOV2007_XACT_x64.cab",
        "NOV2007_XACT_x86.cab", "Nov2007_d3dx10_36_x64.cab", "Nov2007_d3dx10_36_x86.cab",
        "Nov2007_d3dx9_36_x64.cab", "Nov2007_d3dx9_36_x86.cab", "Nov2008_X3DAudio_x64.cab",
        "Nov2008_X3DAudio_x86.cab", "Nov2008_XACT_x64.cab", "Nov2008_XACT_x86.cab",
        "Nov2008_XAudio_x64.cab", "Nov2008_XAudio_x86.cab", "Nov2008_d3dx10_40_x64.cab",
        "Nov2008_d3dx10_40_x86.cab", "Nov2008_d3dx9_40_x64.cab", "Nov2008_d3dx9_40_x86.cab",
        "OCT2006_XACT_x64.cab", "OCT2006_XACT_x86.cab", "OCT2006_d3dx9_31_x64.cab",
        "OCT2006_d3dx9_31_x86.cab", "Oct2005_xinput_x64.cab", "Oct2005_xinput_x86.cab",
        "dsetup32.dll", "dxdllreg_x86.cab", "dxupdate.cab"
    ]);

    public static DownloadRequest Download => new(
        new Uri("https://download.microsoft.com/download/8/4/a/84a35bf1-dafe-4ae8-82af-ad2ae20b6b14/directx_Jun2010_redist.exe"),
        Sha256,
        "win-x64",
        Version,
        SetupAcquisitionKind.Direct,
        true,
        Signer,
        MaximumBytes);

    public static string CacheRoot => Path.Combine(Preparation.PreparationPackages.StateRoot, "packages");
    public static string CacheId => PackageCache.GetCacheId(Download);
    public static string PayloadId => "directx-" + PayloadSha256;
}

