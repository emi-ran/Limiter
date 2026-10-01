[CmdletBinding()]
param(
    [string]$PublishDirectory = 'artifacts/publish',
    [string]$OutputDirectory = 'artifacts/packages'
)
$ErrorActionPreference = 'Stop'
$repository = Split-Path $PSScriptRoot -Parent
$publish = (Resolve-Path -LiteralPath $PublishDirectory).Path
$output = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $output -Force | Out-Null
[xml]$project = Get-Content -LiteralPath "$repository/Limiter/Limiter.csproj" -Raw
$version = [string]$project.Project.PropertyGroup.Version
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw 'Project Version must have three numeric components.' }
if ($env:RELEASE_TAG -and [version]$env:RELEASE_TAG.TrimStart('v') -ne [version]$version) {
    # Two-component release tags such as v0.14 are equivalent to project version 0.14.0.
    $tagParts = $env:RELEASE_TAG.TrimStart('v').Split('.')
    if ($tagParts.Count -eq 2) { $tagParts += '0' }
    if (($tagParts -join '.') -ne $version) { throw 'Release tag does not match project Version.' }
}
foreach ($file in @('Limiter.exe', 'WinDivert.dll', 'WinDivert64.sys', 'LICENSE', 'NOTICE', 'WinDivert-LICENSE.txt', 'coreclr.dll')) {
    if (!(Test-Path -LiteralPath "$publish/$file")) { throw "Missing self-contained payload file: $file" }
}
Copy-Item "$repository/README.md", "$repository/README.tr.md" $publish
Copy-Item "$PSScriptRoot/WiX-LICENSE.txt" $publish
New-Item -ItemType Directory -Path "$publish/docs" -Force | Out-Null
Copy-Item "$repository/docs/images", "$repository/docs/releases" "$publish/docs/" -Recurse -Force
$licenseText = (Get-Content "$repository/LICENSE" -Raw) + "`r`n`r`n" + (Get-Content "$repository/NOTICE" -Raw)
$escaped = $licenseText.Replace('\', '\\').Replace('{', '\{').Replace('}', '\}').Replace("`r", '').Replace("`n", '\par ')
$licenseRtf = Join-Path $output 'License.rtf'
[IO.File]::WriteAllText($licenseRtf, '{\rtf1\ansi\deff0{\fonttbl{\f0 Courier New;}}\f0\fs16 ' + $escaped + '}', [Text.Encoding]::ASCII)
$icon = "$repository/Limiter/Assets/Limiter.ico"
$msi = Join-Path $output "Limiter-$version-win-x64.msi"
$exe = Join-Path $output "Limiter-$version-setup-win-x64.exe"
$portable = Join-Path $output "Limiter-$version-portable-win-x64.zip"
$portableExe = Join-Path $output "Limiter-$version-portable-win-x64.exe"
function Invoke-Wix {
    & dotnet tool run wix -- @args
    if ($LASTEXITCODE -ne 0) { throw "WiX failed with exit code $LASTEXITCODE" }
}
Invoke-Wix build "$PSScriptRoot/Package.wxs" "$PSScriptRoot/Options.wxs" -arch x64 -ext WixToolset.UI.wixext -d "Version=$version" -d "PublishDirectory=$publish" -d "IconPath=$icon" -d "LicenseRtf=$licenseRtf" -o $msi
Invoke-Wix build "$PSScriptRoot/Bundle.wxs" -arch x64 -ext WixToolset.BootstrapperApplications.wixext -d "Version=$version" -d "MsiPath=$msi" -d "IconPath=$icon" -d "LicenseRtf=$licenseRtf" -o $exe
$singleFileOutput = Join-Path $output 'single-file'
& dotnet publish "$repository/Limiter/Limiter.csproj" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:IncludeAllContentForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=None -p:DebugSymbols=false -o $singleFileOutput
if ($LASTEXITCODE -ne 0) { throw 'Portable executable publish failed.' }
Copy-Item "$singleFileOutput/Limiter.exe" $portableExe -Force
Compress-Archive -Path "$publish/*" -DestinationPath $portable -Force
$files = @($exe, $msi, $portableExe, $portable)
$checksums = foreach ($file in $files) {
    if ((Get-Item -LiteralPath $file).Length -eq 0) { throw "Empty package: $file" }
    $hash = Get-FileHash -LiteralPath $file -Algorithm SHA256
    "$($hash.Hash.ToLowerInvariant())  $([IO.Path]::GetFileName($file))"
}
$checksums | Set-Content -LiteralPath "$output/SHA256SUMS.txt" -Encoding ascii
