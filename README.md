# Chrome & Blood Tools

Preparation and MO2 plugins for Chrome & Blood, the Cyberpunk 2077 Wabbajack list.

[Download version 1.0](https://github.com/qcargile/CyberpunkModlistSetup/releases/tag/v1.0)

| Download | Use it for |
| --- | --- |
| `ChromeBlood-Setup-1.0.zip` | Prepare the game and Windows, then open Chrome & Blood in Wabbajack. |
| `ChromeBlood-Archives-1.0.zip` | Drag archives into order in MO2, with conflict highlights and an order saved per profile. |
| `ChromeBlood-Update-Notice-1.0.zip` | Get a notification in MO2 when a newer Chrome & Blood list version is available. |

The plugins install independently. Setup does not install them. C&B list authors can include them in the Wabbajack package.

Setup 1.0 targets Cyberpunk 2077 2.5. Use it with a C&B revision that supports that game version; it is not intended for older 2.31a revisions. Releasing these tools does not update the modlist itself.

## Setup

Extract the Setup ZIP and run `ChromeBloodSetup.exe`. Keep its `Assets` and `licenses` folders with it. A [.NET 10-supported Windows x64 version](https://github.com/dotnet/core/blob/main/release-notes/10.0/supported-os.md) is required; the runtime is included.

Setup finds Cyberpunk 2077, checks the required game version and DLC, and selects missing Visual C++ runtimes, Windows long paths and Wabbajack for preparation. Optional settings resets, shared crash-report cleanup, Steam update preferences and legacy DirectX stay separate. Review the selected actions before they run. Windows requests administrator permission only for the steps that need it.

For Steam English public build **26161058, Cyberpunk 2077 2.5**, including Phantom Liberty and REDmod, cleanup compares the game folder with a pinned Steam file inventory. Extra files move to retained backups. Changed official files receive backup copies and stay in place for Steam to repair. Missing or damaged official files require store repair and another check. Other storefronts, languages and depot sets use the more limited known-location cleanup and are not reported as fully verified.

DLSS replacements named `nvngx_dlss.dll`, `nvngx_dlssd.dll` and `nvngx_dlssg.dll` in `bin/x64` and `tools/redmod/bin` are kept when their signatures verify as NVIDIA and their signed product names match the component. Their verified `.dlsss` backups are also kept for DLSS Swapper. These files appear as preserved customizations, not repair requirements. A missing component, invalid signature or wrong component still needs attention. A valid signature identifies the publisher; it does not guarantee compatibility with the game.

Close the game and mod managers before cleaning, and close Steam when cleaning its game folder. Purge Vortex deployments through Vortex first. Saved Games and MO2/download folders outside the selected game folder are untouched. Extra files inside that folder, including a misplaced MO2 or downloads folder, can move to backup. Resetting settings and clearing shared REDengine diagnostics require separate choices. Interrupted cleanup offers continuation from its recorded transaction. There is no automatic backup-restore screen; backup locations are shown in Details and backups.

Cleanup currently requires free backup capacity equal to the selected data, even for moves on the same drive. Cross-volume cleanup is not supported.

Setup ends at **Open C&B in Wabbajack**. Nexus sign-in, download and installation folders, archive reuse, list installation and updates belong to Wabbajack.

The executable is unsigned. The release includes `SHA256SUMS.txt` for identifying the exact downloads. A checksum is not a malware scan.

## MO2 plugins

Close MO2 before extracting either plugin ZIP into the folder containing `ModOrganizer.exe`. These are MO2 plugins, not mods to install through its Downloads tab.

- [Archives installation and use](MO2/ARCHIVES.md)
- [Update notification installation and use](MO2/UPDATE-NOTICE.md)

Archives requires **MO2 2.5.2 on Windows x64**, with its Python plugin support and Cyberpunk 2077 game support. Its native helper is included; a separate Conflict Studio installation is not required. The notification is specific to Chrome & Blood list metadata.

## Build from source

Use the .NET SDK selected by `global.json` on Windows. The archive bridge's Conflict Studio source is included under `vendor/ConflictStudio`; its origin and license are recorded there.

```powershell
dotnet build ChromeBlood.Setup.slnx -c Release
dotnet test --solution ChromeBlood.Setup.slnx -c Release -- --filter "TestCategory!=CleanupScale&TestCategory!=GameIndexLive&TestCategory!=CleanupVisual"
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/package.ps1
```

The packaging command creates the three ZIPs and their checksums in `dist/1.0`. It refuses an existing output directory. MO2 plugin tests under `MO2/tests` use MO2's own Python and Qt libraries; the parser-only notification tests also run with ordinary Python.

The Steam reference contains file names, sizes and hashes, not game content. Its provenance and import procedure are in [fixtures/vanilla/README.md](fixtures/vanilla/README.md). A different game build needs a matching reference before whole-folder cleanup can classify it.

Third-party notices ship with each affected package. No GitHub or Nexus credentials are needed by these tools.
