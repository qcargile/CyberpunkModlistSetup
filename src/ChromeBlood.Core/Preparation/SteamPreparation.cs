using System.Diagnostics;
using System.Security.Cryptography;
using ChromeBlood.Core.Acquisition;
using ChromeBlood.Core.Detection;
using Microsoft.Win32.SafeHandles;

namespace ChromeBlood.Core.Preparation;

public static class SteamPreparation
{
    public static Task SetLaunchOnlyAsync(string gameRoot, CancellationToken cancellationToken)
        => Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (SteamRunning()) throw new InvalidOperationException("Close Steam before applying its Cyberpunk update preference, then prepare again.");
            var context = PreparationMachine.CreateContext();
            GameInstallation? game = new GameInstallationDetector().Classify(gameRoot, context);
            if (game?.Platform != GamePlatform.Steam || game.SteamManifestStatus != ProbeStatus.Available)
                throw new InvalidOperationException("The selected game folder does not have a matching Steam manifest. Its update preference was kept.");
            string parent = Directory.GetParent(gameRoot)?.Parent?.FullName
                ?? throw new InvalidOperationException("The selected game is not in a Steam library.");
            const string manifestName = "appmanifest_1091500.acf";
            using SecureCacheRoot root = new(parent, new WindowsSecureFileApi(), false);
            using SafeFileHandle gate = root.OpenLock("chrome-blood-steam.lock");
            using SecureFileLease original = root.TryOpenReadReplaceable(manifestName)
                ?? throw new FileNotFoundException("The selected Steam manifest is missing.");
            if (original.Length > SteamManifestEditor.MaximumBytes)
                throw new InvalidOperationException("The Steam manifest is unexpectedly large. No preference was changed.");
            byte[] bytes = new byte[(int)original.Length];
            int count = RandomAccess.Read(original.Handle, bytes, 0);
            if (count != bytes.Length) throw new IOException("The Steam manifest could not be read completely. No preference was changed.");
            SteamManifestEdit edit = SteamManifestEditor.SetLaunchOnly(bytes);
            if (edit.OriginalValue == "1") return;
            string id = Guid.NewGuid().ToString("N");
            string before = "chrome-blood-" + id + ".before";
            using (SafeFileHandle saved = root.CreateAtomicPartial(before))
            {
                RandomAccess.Write(saved, bytes, 0);
                RandomAccess.FlushToDisk(saved);
            }
            using (SecureFileLease saved = root.TryOpenRead(before) ?? throw new IOException("The Steam preimage could not be read."))
            {
                byte[] verification = new byte[bytes.Length];
                if (RandomAccess.Read(saved.Handle, verification, 0) != verification.Length
                    || !SHA256.HashData(verification).AsSpan().SequenceEqual(SHA256.HashData(bytes)))
                    throw new IOException("The previous Steam manifest could not be preserved. Its preference was kept.");
            }
            string pending = "chrome-blood-" + id + ".write";
            using SafeFileHandle replacement = root.CreateAtomicPartial(pending);
            RandomAccess.Write(replacement, edit.UpdatedBytes, 0);
            RandomAccess.FlushToDisk(replacement);
            cancellationToken.ThrowIfCancellationRequested();
            using SecureFileLease current = root.TryOpenReadReplaceable(manifestName)
                ?? throw new IOException("The Steam manifest changed during preparation. Its previous contents were retained.");
            byte[] recheck = new byte[bytes.Length];
            if (current.Length != bytes.Length || RandomAccess.Read(current.Handle, recheck, 0) != recheck.Length
                || !recheck.AsSpan().SequenceEqual(bytes) || SteamRunning())
                throw new IOException("Steam reopened or its manifest changed. Close Steam, then prepare again. The previous manifest was retained.");
            new WindowsSecureFileApi().Rename(replacement, Path.Combine(root.Path, manifestName), true);
        }, cancellationToken);

    private static bool SteamRunning()
    {
        Process[] processes = Process.GetProcessesByName("steam");
        try { return processes.Length > 0; }
        finally { foreach (Process process in processes) process.Dispose(); }
    }
}
