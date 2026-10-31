<#
.SYNOPSIS
  Builds declarad for several operating systems / CPUs and zips every build for distribution.

.DESCRIPTION
  Windows PowerShell 5.1+ or PowerShell 7 (also works on macOS/Linux with pwsh). .NET cross-compiles every target
  from any host. Variants (the zip name says which one):
    declarad-<version>-<rid>-portable.zip   runs on its own, the .NET runtime is included (bigger)
    declarad-<version>-<rid>.zip            small, needs the .NET 10 runtime installed on the machine
  Results: build\dist\*.zip and build\dist\SHA256SUMS (the publish folders are kept in build\out\).
  macOS executables are only ad-hoc signed when built on a Mac; Apple Silicon refuses to run unsigned code.

.EXAMPLE
  .\build\build.ps1
  .\build\build.ps1 -Rids win-x64,linux-x64 -Variants portable
  .\build\build.ps1 -Version 0.4.0 -Clean
#>
param(
    [string[]] $Rids = @('win-x64', 'win-arm64', 'linux-x64', 'linux-arm64', 'osx-x64', 'osx-arm64'),
    [ValidateSet('portable', 'framework')]
    [string[]] $Variants = @('portable', 'framework'),
    [string] $Version,
    [switch] $Clean
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

$Here = $PSScriptRoot
$Root = Split-Path $Here -Parent
$Project = Join-Path $Root 'src\Declarad\Declarad.csproj'
$Out = Join-Path $Here 'out'
$Dist = Join-Path $Here 'dist'

if (-not $Version) { $Version = ([xml](Get-Content $Project)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1 }
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw 'The dotnet SDK 10 was not found on PATH.' }

if ($Clean) { Remove-Item $Out, $Dist -Recurse -Force -ErrorAction SilentlyContinue }
New-Item -ItemType Directory -Force $Out, $Dist | Out-Null

# Zips a folder. Entries listed in $Executable get Unix mode 755, so the zip also works on macOS/Linux.
function New-Zip([string] $SourceParent, [string] $EntryName, [string] $ZipPath, [string[]] $Executable) {
    if (Test-Path $ZipPath) { Remove-Item $ZipPath -Force }
    $zip = [System.IO.Compression.ZipFile]::Open($ZipPath, 'Create')
    try {
        $base = Join-Path $SourceParent $EntryName
        foreach ($file in Get-ChildItem $base -Recurse -File -Force) {
            $rel = $file.FullName.Substring($SourceParent.Length).TrimStart('\', '/').Replace('\', '/')
            $entry = [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $file.FullName, $rel, 'Optimal')
            $mode = if ($Executable -contains $rel) { 0x81ED } else { 0x81A4 }   # 0100755 / 0100644
            $entry.ExternalAttributes = $mode -shl 16
        }
    } finally { $zip.Dispose() }
}

$onMac = $IsMacOS -eq $true
Write-Host "declarad $Version  |  platforms: $($Rids -join ' ')  |  variants: $($Variants -join ' ')"

foreach ($rid in $Rids) {
    foreach ($variant in $Variants) {
        $selfContained = if ($variant -eq 'portable') { 'true' } else { 'false' }
        $suffix = if ($variant -eq 'portable') { '-portable' } else { '' }
        Write-Host "`n== $rid ($variant)"

        $publish = Join-Path $Out "$rid-$variant"
        if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
        # One executable per platform; native libraries are unpacked at run time when needed.
        dotnet publish $Project -c Release -r $rid --self-contained $selfContained `
            "-p:Version=$Version" -p:DebugType=None -p:DebugSymbols=false `
            -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:IncludeSourceRevisionInInformationalVersion=false -o $publish --nologo -v quiet
        if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for $rid ($variant)" }

        $exe = if ($rid -like 'win-*') { 'declarad.exe' } else { 'declarad' }
        if ($rid -like 'osx-*') {
            if ($onMac) {
                codesign --force --sign - (Join-Path $publish $exe) | Out-Null
            } else {
                Write-Warning "$rid is not signed (built off a Mac); sign it on a Mac with: codesign --force --sign - declarad"
            }
        }

        $stage = Join-Path $Out "stage-$rid-$variant"
        if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
        $dir = Join-Path $stage 'declarad'
        New-Item -ItemType Directory -Force $dir | Out-Null
        Copy-Item (Join-Path $publish $exe), (Join-Path $Root 'README.md') $dir
        if (Test-Path (Join-Path $Root 'LICENSE')) { Copy-Item (Join-Path $Root 'LICENSE') $dir }
        $zipFile = Join-Path $Dist "declarad-$Version-$rid$suffix.zip"
        New-Zip $stage 'declarad' $zipFile @("declarad/$exe")
        Remove-Item $stage -Recurse -Force
        Write-Host ("   -> {0} ({1:N1} MB)" -f $zipFile, ((Get-Item $zipFile).Length / 1MB))
    }
}

# Checksums for every zip in dist\, in the format `sha256sum -c` reads (LF line endings).
$sums = Get-ChildItem $Dist -Filter 'declarad-*.zip' | Sort-Object Name | ForEach-Object {
    "{0}  {1}" -f (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), $_.Name
}
[System.IO.File]::WriteAllText((Join-Path $Dist 'SHA256SUMS'), (($sums -join "`n") + "`n"))

Write-Host "`nDone. Zips and SHA256SUMS are in $Dist"
