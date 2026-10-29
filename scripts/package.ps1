[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$root = Split-Path -Parent $PSScriptRoot
$properties = [xml](Get-Content -LiteralPath (Join-Path $root 'Directory.Build.props') -Raw)
$version = [version]$properties.Project.PropertyGroup.Version
$label = if ($version.Build -eq 0) { $version.ToString(2) } else { $version.ToString(3) }
$output = Join-Path $root "dist\$label"
if (Test-Path -LiteralPath $output) { throw "Output already exists: $output" }
if (-not (Test-Path -LiteralPath (Join-Path $root 'LICENSE'))) { throw 'The project license is missing.' }
New-Item -ItemType Directory -Path $output | Out-Null
$setup = Join-Path $output 'Setup'
$archives = Join-Path $output 'Archives'
$notice = Join-Path $output 'Update-Notice'
$studio = Join-Path $archives 'Tools\Conflict Studio'
$bridge = Join-Path $archives 'Tools\Conflict Studio\Mo2Bridge'

& dotnet publish (Join-Path $root 'src\ChromeBlood.Setup\ChromeBlood.Setup.csproj') -c Release -r win-x64 --self-contained true '-p:PublishSingleFile=true' '-p:IncludeNativeLibrariesForSelfExtract=true' '-p:EnableCompressionInSingleFile=true' -o $setup
if ($LASTEXITCODE -ne 0) { throw 'Setup publish failed.' }
& dotnet publish (Join-Path $root 'vendor\ConflictStudio\src\ConflictStudio.Mo2Bridge\ConflictStudio.Mo2Bridge.csproj') -c Release -r win-x64 --self-contained true -o $bridge
if ($LASTEXITCODE -ne 0) { throw 'Archive bridge publish failed.' }
& dotnet publish (Join-Path $root 'vendor\ConflictStudio\src\ConflictStudio.App\ConflictStudio.App.csproj') -c Release -r win-x64 --self-contained true '-p:PublishSingleFile=true' '-p:IncludeNativeLibrariesForSelfExtract=true' '-p:EnableCompressionInSingleFile=true' "-p:InformationalVersion=1.0.1+chrome-blood-tools.$label" -o $studio
if ($LASTEXITCODE -ne 0) { throw 'Conflict Studio companion publish failed.' }

$dotnetRoot = Split-Path -Parent (Get-Command dotnet -ErrorAction Stop).Source
foreach ($destination in @($setup, $bridge, $studio)) {
    $licenses = Join-Path $destination 'licenses'
    New-Item -ItemType Directory -Path $licenses -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $dotnetRoot 'LICENSE.txt') -Destination (Join-Path $licenses 'DOTNET-LICENSE.txt')
    Copy-Item -LiteralPath (Join-Path $dotnetRoot 'ThirdPartyNotices.txt') -Destination (Join-Path $licenses 'DOTNET-THIRD-PARTY-NOTICES.txt')
}
Copy-Item -LiteralPath (Join-Path $root 'LICENSE') -Destination $setup
Copy-Item -LiteralPath (Join-Path $root 'README.md') -Destination $setup
Copy-Item -LiteralPath (Join-Path $root 'THIRD-PARTY-NOTICES.md') -Destination $setup
foreach ($destination in @($bridge, $studio)) {
    Copy-Item -LiteralPath (Join-Path $root 'vendor\ConflictStudio\LICENSE') -Destination (Join-Path $destination 'licenses\CONFLICT-STUDIO-LICENSE.txt')
    Copy-Item -LiteralPath (Join-Path $root 'vendor\ConflictStudio\THIRD-PARTY-NOTICES.txt') -Destination (Join-Path $destination 'licenses\CONFLICT-STUDIO-NOTICES.txt')
}

foreach ($package in @(
    @{ Root = $archives; Plugin = 'chrome_blood_archives'; Readme = 'ARCHIVES.md'; File = 'ChromeBlood-Archives-README.md' },
    @{ Root = $notice; Plugin = 'chrome_blood_update_notice'; Readme = 'UPDATE-NOTICE.md'; File = 'ChromeBlood-Update-Notice-README.md' }
)) {
    $destination = Join-Path $package.Root ('plugins\' + $package.Plugin)
    New-Item -ItemType Directory -Path $destination -Force | Out-Null
    Get-ChildItem -LiteralPath (Join-Path $root ('MO2\plugins\' + $package.Plugin)) -File -Filter '*.py' | ForEach-Object {
        Copy-Item -LiteralPath $_.FullName -Destination $destination
    }
    Copy-Item -LiteralPath (Join-Path $root 'LICENSE') -Destination $destination
    Copy-Item -LiteralPath (Join-Path $root ('MO2\' + $package.Readme)) -Destination (Join-Path $package.Root $package.File)
}

$packages = @(
    @{ Root = $setup; Name = "ChromeBlood-Setup-$label.zip" },
    @{ Root = $archives; Name = "ChromeBlood-Archives-$label.zip" },
    @{ Root = $notice; Name = "ChromeBlood-Update-Notice-$label.zip" }
)
foreach ($package in $packages) {
    if (Get-ChildItem -LiteralPath $package.Root -Recurse -File | Where-Object { $_.Extension -in @('.pdb', '.pyc') }) { throw 'Debug or Python cache files entered a package.' }
    $zip = [IO.Compression.ZipFile]::Open((Join-Path $output $package.Name), [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($file in Get-ChildItem -LiteralPath $package.Root -Recurse -File | Sort-Object FullName) {
            $relative = $file.FullName.Substring($package.Root.Length + 1).Replace('\', '/')
            [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $file.FullName, $relative) | Out-Null
        }
    } finally { $zip.Dispose() }
}
$sums = foreach ($package in $packages) {
    $hash = (Get-FileHash -LiteralPath (Join-Path $output $package.Name) -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $($package.Name)"
}
[IO.File]::WriteAllLines((Join-Path $output 'SHA256SUMS.txt'), $sums, [Text.UTF8Encoding]::new($false))
Write-Output $output
