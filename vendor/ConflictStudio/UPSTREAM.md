# Conflict Studio bridge source

The Core, Mo2Bridge and App projects are copied without source changes from [Cyberpunk Conflict Studio](https://github.com/qcargile/Cyberpunk-Conflict-Studio), commit `2e02413`.

They provide the archive scanner, relationship projection, guarded profile-order writer, JSON-lines bridge and optional Studio companion. The Vortex extension is not part of this package. The companion's informational version identifies the C&B Tools build separately from the older standalone Studio 1.0.1 release.

Build `src/ConflictStudio.Mo2Bridge/ConflictStudio.Mo2Bridge.csproj` with .NET 10 on Windows. Keep the complete self-contained output together. This source snapshot and its MIT license are included so the release can be built without a separate checkout.
