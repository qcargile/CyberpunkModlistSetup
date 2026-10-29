using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using ChromeBlood.Core.Acquisition;
using Microsoft.Win32;

namespace ChromeBlood.Core.Preparation;

public static class PreparationPackages
{
    public const string WabbajackSigner = "CN=Timothy Baldridge, O=Timothy Baldridge, L=Highlands Ranch, S=Colorado, C=US";
    public static string StateRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ChromeBloodSetup");
    private static string CacheRoot => Path.Combine(StateRoot, "packages");
    private sealed record LaunchIntent(string Verb, string? CacheId, string? Sha256, DateTimeOffset CreatedUtc, string State,
        int? ExitCode = null, int? ProcessId = null, DateTimeOffset? ProcessStartUtc = null, string? Image = null);

    public static async Task<string> DownloadWabbajackAsync(string wjDir, IProgress<PreparationProgress>? progress,
        CancellationToken cancellationToken = default)
    {
        if (!PreparationMachine.IsAllowedWabbajackDirectory(wjDir)) throw new IOException("Choose a safe Wabbajack app folder first.");
        progress?.Report(new("Finding the official Wabbajack release"));
        DownloadRequest request = ParseWabbajackRelease(await ReadReleaseAsync(cancellationToken).ConfigureAwait(false));
        using PackageDownloader downloader = new();
        using PackageCache cache = new(CacheRoot, downloader, new AuthenticodeVerifier());
        PackageCacheEntry package = await cache.AcquireAsync(request, null,
            new DownloadProgress(progress, "Downloading Wabbajack"), cancellationToken).ConfigureAwait(false);
        using SecureCacheRoot source = new(CacheRoot);
        using SecureFileLease input = source.TryOpenRead(Path.GetFileName(package.Path)) ?? throw new IOException("The verified Wabbajack download disappeared.");
        using SecureCacheRoot destination = new(wjDir);
        using var writeLock = destination.OpenLock("wabbajack-publish.lock");
        string partial = "Wabbajack-" + Guid.NewGuid().ToString("N") + ".partial";
        try
        {
            await using (var output = new FileStream(destination.CreatePartial(partial), FileAccess.Write, 81920, true))
            {
                byte[] buffer = new byte[81920];
                long offset = 0;
                while (offset < input.Length)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int read = await RandomAccess.ReadAsync(input.Handle, buffer, offset, cancellationToken).ConfigureAwait(false);
                    if (read == 0) throw new IOException("The verified Wabbajack download changed.");
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    offset += read;
                }
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                output.Flush(true);
            }
            using SecureFileLease staged = destination.TryOpenReadDelete(partial) ?? throw new IOException("The staged Wabbajack download disappeared.");
            FileHashResult hash = await new FileHandleHasher().HashAsync(staged.Handle, request.MaximumBytes, cancellationToken).ConfigureAwait(false);
            var trust = new AuthenticodeVerifier().Verify(staged.Handle, staged.FinalPath, WabbajackSigner, cancellationToken);
            if (hash.Sha256 != request.ExpectedSha256 || trust.Status != AuthenticodeStatus.Trusted)
                throw new IOException("Wabbajack failed SHA-256 or publisher verification. The existing app was kept.");
            cancellationToken.ThrowIfCancellationRequested();
            destination.RevalidateRoot();
            staged.Rename(Path.Combine(destination.Path, "Wabbajack.exe"), true);
            return Path.Combine(destination.Path, "Wabbajack.exe");
        }
        finally { destination.DeleteIfPresent(partial); }
    }

    public static DownloadRequest ParseWabbajackRelease(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        var release = document.RootElement;
        string? tag = release.GetProperty("tag_name").GetString();
        if (release.GetProperty("draft").GetBoolean() || release.GetProperty("prerelease").GetBoolean() ||
            string.IsNullOrWhiteSpace(tag) || !tag.All(value => char.IsAsciiLetterOrDigit(value) || value is '.' or '-'))
            throw new IOException("The official Wabbajack release is invalid.");
        foreach (var asset in release.GetProperty("assets").EnumerateArray())
        {
            if (asset.GetProperty("name").GetString() != "Wabbajack.exe") continue;
            string expected = $"https://github.com/wabbajack-tools/wabbajack/releases/download/{tag}/Wabbajack.exe";
            string? url = asset.GetProperty("browser_download_url").GetString();
            string? digest = asset.GetProperty("digest").GetString();
            if (url != expected || digest is null || digest.Length != 71 || !digest.StartsWith("sha256:", StringComparison.Ordinal) ||
                !digest.Skip(7).All(char.IsAsciiHexDigit)) throw new IOException("The official Wabbajack asset has no valid SHA-256 digest.");
            return new(new Uri(url), digest[7..].ToLowerInvariant(), "win-x64", tag, SetupAcquisitionKind.Direct, true, WabbajackSigner, 100_000_000);
        }
        throw new IOException("The official release does not include Wabbajack.exe.");
    }

    public static async Task<bool> InstallPrerequisitesAsync(PreparationOptions options, IProgress<PreparationProgress>? progress,
        CancellationToken cancellationToken = default, Action<bool>? rebootChanged = null)
    {
        List<string> verbs = [];
        if (options.InstallRuntimes)
        {
            if (!VcRuntimePresence.IsInstalled("x64")) verbs.Add("vcredist-x64");
            if (!VcRuntimePresence.IsInstalled("x86")) verbs.Add("vcredist-x86");
        }
        if (options.EnableLongPaths && !PreparationMachine.LongPathsEnabled) verbs.Add("longpaths");
        if (options.LegacyDirectX && new WindowsDirectXRuntimePresenceProbe().Observe().Status != DirectXRuntimeStatus.Satisfied) verbs.Add("directx");
        bool reboot = false;
        foreach (string verb in verbs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using SecureCacheRoot intents = new(StateRoot);
            using var actionLock = intents.OpenLock(verb + ".lock");
            RequirePreviousProcessFinished(intents, verb);
            DownloadRequest? request = PackageFor(verb);
            PackageCacheEntry? package = null;
            if (request is not null)
            {
                using PackageDownloader downloader = new();
                using PackageCache cache = new(CacheRoot, downloader, new AuthenticodeVerifier());
                package = await cache.AcquireAsync(request, null, new DownloadProgress(progress, "Downloading Microsoft prerequisites"), cancellationToken).ConfigureAwait(false);
            }
            LaunchIntent intent = new(verb, package?.CacheId, package?.Sha256, DateTimeOffset.UtcNow, "prepared");
            SaveIntent(intent);
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new("Administrator preparation", "Approve the Windows prompt. Setup waits for running installers to finish before cancelling."));
            int code;
            if (verb == "longpaths")
            {
                string self = Environment.ProcessPath ?? throw new IOException("Cannot locate the setup program.");
                code = await RunVendorAsync(self, ["--prerequisite", "longpaths"], cancellationToken, intent, true).ConfigureAwait(false);
            }
            else
            {
                using SecureCacheRoot cache = new(CacheRoot);
                using SecureFileLease launch = await OpenLaunchPackageAsync(cache, package!, request!, verb, cancellationToken).ConfigureAwait(false);
                code = verb == "directx" ? await InstallDirectXAsync(launch.FinalPath, intent, cancellationToken).ConfigureAwait(false) :
                    await RunVendorAsync(launch.FinalPath, ["/install", "/quiet", "/norestart"], cancellationToken, intent, true).ConfigureAwait(false);
                if ((code is 0 or 1638 or 3010) && verb.StartsWith("vcredist-", StringComparison.Ordinal) && !VcRuntimePresence.IsInstalled(verb[9..])) code = 1603;
            }
            if (code == 3010) { reboot = true; rebootChanged?.Invoke(true); }
            SaveIntent(intent with { State = "completed", ExitCode = code });
            if (code is not (0 or 1638 or 3010)) throw new IOException($"The {verb} step failed (Windows result {code}). Check preparation and retry.");
            cancellationToken.ThrowIfCancellationRequested();
        }
        return reboot;
    }

    public static Task<int> RunPrerequisiteAsync(string verb, CancellationToken cancellationToken = default)
    {
        if (verb != "longpaths") return Task.FromResult(87);
        cancellationToken.ThrowIfCancellationRequested();
        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator)) return Task.FromResult(740);
        try
        {
            bool changed = !PreparationMachine.LongPathsEnabled;
            using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = machine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\FileSystem", true) ?? throw new IOException("Cannot open the Windows long-path setting.");
            key.SetValue("LongPathsEnabled", 1, RegistryValueKind.DWord);
            return Task.FromResult(PreparationMachine.LongPathsEnabled ? changed ? 3010 : 0 : 5);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or Win32Exception)
        {
            return Task.FromResult(5);
        }
    }

    private static DownloadRequest? PackageFor(string verb) => verb switch
    {
        "vcredist-x64" => VcRuntimePackage.Download,
        "vcredist-x86" => new(new Uri("https://aka.ms/vs/17/release/vc_redist.x86.exe"), null, "win-x86", "latest", SetupAcquisitionKind.Direct, true, VcRuntimePackage.Signer, 30_000_000),
        "directx" => DirectXRuntimePackage.Download,
        _ => null
    };

    private static void RequirePreviousProcessFinished(SecureCacheRoot root, string verb)
    {
        using SecureFileLease? file = root.TryOpenRead(verb + ".json");
        if (file is null) return;
        if (file.Length > 4096) throw new IOException("The previous preparation record is invalid.");
        byte[] bytes = new byte[(int)file.Length];
        if (RandomAccess.Read(file.Handle, bytes, 0) != bytes.Length) throw new IOException("Cannot read the previous preparation record.");
        LaunchIntent? intent = JsonSerializer.Deserialize<LaunchIntent>(bytes);
        if (intent?.State is not ("running" or "launching") || intent.Image is null) return;
        Process[] candidates;
        if (intent.ProcessId is int id)
        {
            try { candidates = [Process.GetProcessById(id)]; }
            catch (ArgumentException) { return; }
        }
        else candidates = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(intent.Image));
        try
        {
            foreach (Process process in candidates)
            {
                try
                {
                    if (process.HasExited) continue;
                    DateTimeOffset started = process.StartTime.ToUniversalTime();
                    if (intent.ProcessStartUtc is { } previous && started != previous) continue;
                    if (started < intent.CreatedUtc) continue;
                    string? image = process.MainModule?.FileName;
                    if (string.Equals(image, intent.Image, StringComparison.OrdinalIgnoreCase))
                        throw new IOException("The previous Microsoft installer is still running. Wait for it to finish, then check preparation again.");
                }
                catch (Win32Exception exception)
                {
                    throw new IOException("Windows cannot inspect the previous administrator process yet. Wait for it to finish, then check preparation again.", exception);
                }
                catch (InvalidOperationException) { }
            }
        }
        finally { foreach (Process process in candidates) process.Dispose(); }
    }

    private static async Task<SecureFileLease> OpenLaunchPackageAsync(SecureCacheRoot root, PackageCacheEntry package,
        DownloadRequest request, string verb, CancellationToken token)
    {
        string name = package.CacheId + "-" + verb + ".exe";
        SecureFileLease? existing = root.TryOpenRead(name);
        if (existing is not null)
        {
            try
            {
                await VerifyLaunchPackageAsync(existing, package, request, token).ConfigureAwait(false);
                return existing;
            }
            catch { existing.Dispose(); throw; }
        }
        using SecureFileLease input = root.TryOpenRead(Path.GetFileName(package.Path)) ?? throw new IOException("The verified Microsoft package disappeared.");
        string partial = name + "-" + Guid.NewGuid().ToString("N") + ".partial";
        try
        {
            await using (FileStream output = new(root.CreatePartial(partial), FileAccess.Write, 81920, true))
            {
                byte[] buffer = new byte[81920];
                long offset = 0;
                while (offset < input.Length)
                {
                    int read = await RandomAccess.ReadAsync(input.Handle, buffer, offset, token).ConfigureAwait(false);
                    if (read == 0) throw new IOException("The Microsoft package changed while staging.");
                    await output.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                    offset += read;
                }
                await output.FlushAsync(token).ConfigureAwait(false);
                output.Flush(true);
            }
            using (SecureFileLease staged = root.TryOpenReadDelete(partial) ?? throw new IOException("The staged Microsoft package disappeared."))
            {
                await VerifyLaunchPackageAsync(staged, package, request, token).ConfigureAwait(false);
                staged.Rename(Path.Combine(root.Path, name));
            }
            SecureFileLease launch = root.TryOpenRead(name) ?? throw new IOException("The staged Microsoft installer disappeared.");
            try
            {
                await VerifyLaunchPackageAsync(launch, package, request, token).ConfigureAwait(false);
                return launch;
            }
            catch { launch.Dispose(); throw; }
        }
        finally { root.DeleteIfPresent(partial); }
    }

    private static async Task VerifyLaunchPackageAsync(SecureFileLease file, PackageCacheEntry package, DownloadRequest request, CancellationToken token)
    {
        FileHashResult hash = await new FileHandleHasher().HashAsync(file.Handle, request.MaximumBytes, token).ConfigureAwait(false);
        var trust = new AuthenticodeVerifier().Verify(file.Handle, file.FinalPath, request.ExpectedSigner, token);
        if (hash.Sha256 != package.Sha256 || request.ExpectedSha256 is not null && hash.Sha256 != request.ExpectedSha256 || trust.Status != AuthenticodeStatus.Trusted)
            throw new IOException("The Microsoft installer failed SHA-256 or publisher verification.");
    }

    private static void SaveIntent(LaunchIntent intent)
    {
        using SecureCacheRoot root = new(StateRoot);
        string name = intent.Verb + "-" + Guid.NewGuid().ToString("N") + ".write";
        try
        {
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(intent);
            using (var handle = root.CreateAtomicPartial(name))
            {
                RandomAccess.Write(handle, bytes, 0);
                RandomAccess.FlushToDisk(handle);
            }
            using var staged = root.TryOpenReadDelete(name) ?? throw new IOException("Cannot save the administrator action.");
            staged.Rename(Path.Combine(root.Path, intent.Verb + ".json"), true);
        }
        finally { root.DeleteIfPresent(name); }
    }

    private static async Task<int> RunVendorAsync(string executable, IReadOnlyList<string> arguments, CancellationToken token,
        LaunchIntent? intent = null, bool elevated = false)
    {
        token.ThrowIfCancellationRequested();
        ProcessStartInfo start = new(executable) { UseShellExecute = elevated, Verb = elevated ? "runas" : "", CreateNoWindow = !elevated,
            WindowStyle = ProcessWindowStyle.Hidden, WorkingDirectory = Path.GetDirectoryName(executable)! };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        if (intent is not null) SaveIntent(intent with { State = "launching", Image = executable });
        Process process;
        try { process = Process.Start(start) ?? throw new IOException("The Microsoft installer did not start."); }
        catch (Win32Exception exception) when (exception.NativeErrorCode == 1223)
        {
            if (intent is not null) SaveIntent(intent with { State = "declined" });
            throw new IOException("Administrator permission was declined. This Windows step was not started.", exception);
        }
        using (process)
        {
            return await DrainVendorAsync(process, active =>
            {
                if (intent is not null) SaveIntent(intent with { State = "running", ProcessId = active.Id,
                    ProcessStartUtc = active.StartTime.ToUniversalTime(), Image = executable });
            }).ConfigureAwait(false);
        }
    }

    internal static async Task<int> DrainVendorAsync(Process process, Action<Process> recordStarted)
    {
        try { recordStarted(process); }
        finally { await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false); }
        return process.ExitCode;
    }

    private static async Task<int> InstallDirectXAsync(string executable, LaunchIntent intent, CancellationToken token)
    {
        string directory = Path.Combine(CacheRoot, "directx-" + Guid.NewGuid().ToString("N"));
        List<SecureFileLease> files = [];
        using SecureCacheRoot root = new(directory);
        try
        {
            int extracted = await RunVendorAsync(executable, ["/Q", "/C", "/T:" + directory], token).ConfigureAwait(false);
            if (extracted != 0) return extracted;
            var names = Directory.EnumerateFileSystemEntries(directory).Take(DirectXRuntimePackage.PayloadFileCount + 1).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray();
            if (!names.SequenceEqual(DirectXRuntimePackage.PayloadFileNames, StringComparer.Ordinal)) throw new IOException("The DirectX extraction has unexpected files.");
            using var aggregate = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long bytes = 0;
            foreach (string? name in names)
            {
                SecureFileLease file = root.TryOpenRead(name!) ?? throw new IOException("The DirectX payload changed.");
                files.Add(file);
                var hash = await new FileHandleHasher().HashAsync(file.Handle, DirectXRuntimePackage.MaximumBytes, token).ConfigureAwait(false);
                bytes += hash.Length;
                aggregate.AppendData(Encoding.UTF8.GetBytes(name + "\0" + hash.Length.ToString(CultureInfo.InvariantCulture) + "\0" + hash.Sha256 + "\n"));
                if (name is "DXSETUP.exe" or "DSETUP.dll" or "dsetup32.dll")
                {
                    if (new AuthenticodeVerifier().Verify(file.Handle, file.FinalPath, DirectXRuntimePackage.SetupSigner, token).Status != AuthenticodeStatus.Trusted ||
                        name == "DXSETUP.exe" && hash.Sha256 != DirectXRuntimePackage.SetupSha256) throw new IOException("The extracted DirectX installer failed verification.");
                }
            }
            if (bytes != DirectXRuntimePackage.PayloadBytes || Convert.ToHexStringLower(aggregate.GetHashAndReset()) != DirectXRuntimePackage.PayloadSha256)
                throw new IOException("The DirectX payload does not match its verified inventory.");
            root.RevalidateRoot();
            int result = await RunVendorAsync(Path.Combine(directory, "DXSETUP.exe"), ["/silent"], token, intent, true).ConfigureAwait(false);
            if ((result is 0 or 3010) && new WindowsDirectXRuntimePresenceProbe().Observe().Status != DirectXRuntimeStatus.Satisfied) return 1603;
            return result;
        }
        finally
        {
            foreach (var file in files) file.Dispose();
            foreach (string name in DirectXRuntimePackage.PayloadFileNames)
            {
                try { root.DeleteIfPresent(name); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            root.Dispose();
            try { Directory.Delete(directory, false); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static async Task<string> ReadReleaseAsync(CancellationToken token)
    {
        using var handler = new HttpClientHandler { AllowAutoRedirect = false };
        using HttpClient client = new(handler) { Timeout = TimeSpan.FromSeconds(45) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("ChromeBloodSetup/1.0");
        using var response = await client.GetAsync("https://api.github.com/repos/wabbajack-tools/wabbajack/releases/latest", HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > 1_000_000) throw new IOException("The Wabbajack release response is too large.");
        await using var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var bytes = new MemoryStream();
        byte[] buffer = new byte[8192];
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(45));
        int read;
        while ((read = await input.ReadAsync(buffer, deadline.Token).ConfigureAwait(false)) != 0)
        {
            if (bytes.Length + read > 1_000_000) throw new IOException("The Wabbajack release response is too large.");
            bytes.Write(buffer, 0, read);
        }
        return Encoding.UTF8.GetString(bytes.ToArray());
    }

    private sealed class DownloadProgress(IProgress<PreparationProgress>? progress, string title) : IProgress<PackageDownloadProgress>
    {
        private long _lastUpdate;
        private PackageDownloadProgressState? _lastState;
        public void Report(PackageDownloadProgress value)
        {
            long now = Environment.TickCount64;
            if (now - _lastUpdate < 100 && _lastState == value.State && value.BytesReceived != value.TotalBytes) return;
            _lastUpdate = now;
            _lastState = value.State;
            progress?.Report(new(title,
            value.State == PackageDownloadProgressState.WaitingToRetry ? "Connection interrupted. Retrying the verified download." : "Verifying SHA-256 and publisher before use.",
            value.TotalBytes is > 0 ? (double)value.BytesReceived / value.TotalBytes.Value : null));
        }
    }
}
