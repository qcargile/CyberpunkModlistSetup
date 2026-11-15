# Conflict Studio bridge source

The Core, Mo2Bridge and App projects are copied without source changes from [Cyberpunk Conflict Studio](https://github.com/qcargile/Cyberpunk-Conflict-Studio), commit `adf25286144732777ec7f61545ad64932a3404c0`.

They provide the archive scanner, relationship projection, guarded profile-order writer, JSON-lines bridge and optional Studio companion. The Vortex extension is not part of this package. Both the bridge and companion use Conflict Studio 1.0.2. The companion's informational version also identifies the Archives package.

Build `src/ConflictStudio.Mo2Bridge/ConflictStudio.Mo2Bridge.csproj` with .NET 10 on Windows. Keep the complete self-contained output together. This source snapshot and its MIT license are included so the release can be built without a separate checkout.
