# Steam 2.5 file reference

`steam-2.5.json` contains 5,125 files, their sizes and Steam SHA-1 hashes, and 980 directories for Cyberpunk 2077 Steam English public build `26161058`, including REDmod and Phantom Liberty. Total file size is 82,066,017,811 bytes. Its SHA-256 is `1dd7286671f59b8c2d91bec1c21716d7181b1ddb07876c2841fce92d09e77ea0`; Core checks that pin before using the embedded reference.

The inputs came from the local Steam client's `depotcache`. Their IDs match the installed `appmanifest_1091500.acf`. The import checks their pinned SHA-256 values, section framing, payload CRC-32, plaintext names, depot identities, file hashes, size totals and Windows path validity. It uses SteamKit2 3.5.0, source commit `49387aec55c24104946af7a7b160148358e538b4`, licensed LGPL-2.1-only. SteamKit2 is a maintainer-tool dependency; Core and Setup do not depend on it.

| Depot | Manifest | Files | Bytes | Raw SHA-256 |
| --- | --- | ---: | ---: | --- |
| 1091501 | 3524634124112875626 | 155 | 66,818,683,782 | e4299d31a2c8f92517139946e444cf5f65e0098d4406aecdf0ab6d527b3eb3b8 |
| 2060310 | 4793018690565322422 | 4,943 | 634,683,704 | 3815b138a0bf3d383398c6634a554c3b25c2579c5f51d5826bb28e316a65b8dc |
| 2138330 | 5565496786254603194 | 27 | 14,612,650,325 | 47dcf532980b6572e9a88f5411881faf2a86ab4c1b05f6147824b6f6d9d6c2a5 |

These checks establish reproducibility and consistency with the observed Steam cache. The import did not download manifests from a Steam CDN or authenticate a Steam cryptographic signature. Installed game files were never used to create reference hashes.

From the repository root, reproduce and compare the existing reference:

```powershell
dotnet restore tools/ChromeBlood.GameIndex --locked-mode
dotnet run --project tools/ChromeBlood.GameIndex -- --depot-cache 'C:\Launchers\depotcache' --app-manifest 'D:\SteamLibrary\steamapps\appmanifest_1091500.acf' --output fixtures/vanilla/steam-2.5.json --verify
```

Omit `--verify` and choose a new output path to write a fresh reference. Existing files are never overwritten. The tool rejects output under the supplied Steam `steamapps` or `depotcache` directories and rejects reparse points in input and output ancestry.

The runtime scanner requires completed-install `StateFlags=4` and refuses to classify installations with different build, branch, language or installed depot IDs. Its preview checks paths and sizes. Only explicit hash verification checks official file contents; same-size files remain unverified during preview. Two empty official files have all-zero manifest hash fields; their required zero length establishes their empty content during verification. Extra paths and empty directories are reported throughout the game directory. Reparse points, unreadable entries and resource limits leave the scan incomplete. The scanner makes no game changes.
