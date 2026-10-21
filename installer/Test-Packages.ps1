[CmdletBinding()]
param([string]$PackageDirectory = 'artifacts/packages', [switch]$Install)
$ErrorActionPreference = 'Stop'
$directory = (Resolve-Path -LiteralPath $PackageDirectory).Path
$verifiedNames = @()
foreach ($line in Get-Content "$directory/SHA256SUMS.txt") {
    if ($line -notmatch '^([a-f0-9]{64})  ([^\\/]+)$') { throw 'Invalid checksum entry.' }
    $name = $Matches[2]
    if ($name -in $verifiedNames) { throw "Duplicate checksum entry: $name" }
    $hash = Get-FileHash -LiteralPath (Join-Path $directory $name) -Algorithm SHA256
    if ($hash.Hash -ne $Matches[1]) { throw "Checksum mismatch: $($hash.Path)" }
    $verifiedNames += $name
}
function Invoke-PackageProcess([string]$Path, [string[]]$Arguments) {
    $process = Start-Process -FilePath $Path -ArgumentList $Arguments -PassThru -WindowStyle Hidden
    $null = $process.Handle
    if (!$process.WaitForExit(120000)) { $process.Kill(); throw "Timed out: $Path" }
    if ($process.ExitCode -notin @(0, 3010)) { throw "$Path exited with $($process.ExitCode)." }
}
$portable = @(Get-ChildItem "$directory/*portable*.exe" -File)
$msi = @(Get-ChildItem "$directory/*.msi" -File)
$setup = @(Get-ChildItem "$directory/*setup*.exe" -File)
$zip = @(Get-ChildItem "$directory/*portable*.zip" -File)
if ($portable.Count -ne 1 -or $msi.Count -ne 1 -or $setup.Count -ne 1 -or $zip.Count -ne 1) {
    throw 'Expected one portable EXE, one MSI, one setup EXE and one portable ZIP.'
}
foreach ($package in @($portable[0], $msi[0], $setup[0], $zip[0])) {
    if ($package.Name -notin $verifiedNames) { throw "Package missing from checksums: $($package.Name)" }
}
$windowsInstaller = New-Object -ComObject WindowsInstaller.Installer
$database = $windowsInstaller.OpenDatabase($msi[0].FullName, 0)
foreach ($option in @('DESKTOPSHORTCUT', 'STARTWITHWINDOWS')) {
    $view = $database.OpenView("SELECT ``Property`` FROM ``Control`` WHERE ``Dialog_`` = 'OptionsDlg' AND ``Property`` = '$option'")
    $view.Execute()
    if (!$view.Fetch()) { throw "Installer checkbox missing: $option" }
    $view.Close()
    $view = $database.OpenView("SELECT ``Value`` FROM ``Property`` WHERE ``Property`` = '$option'")
    $view.Execute()
    $record = $view.Fetch()
    if ($record -and $record.StringData(1) -eq '1') { throw "Installer option must default to off: $option" }
    $view.Close()
}
[Runtime.InteropServices.Marshal]::FinalReleaseComObject($database) | Out-Null
[Runtime.InteropServices.Marshal]::FinalReleaseComObject($windowsInstaller) | Out-Null
Invoke-PackageProcess $portable[0].FullName @('--verify-package')
# Verify the archive's own payload, not the publish folder or standalone portable EXE.
$extractDirectory = Join-Path $directory ('zip-smoke-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $extractDirectory -ErrorAction Stop | Out-Null
try {
    Expand-Archive -LiteralPath $zip[0].FullName -DestinationPath $extractDirectory
    foreach ($file in @('Limiter.exe', 'WinDivert.dll', 'WinDivert64.sys', 'coreclr.dll', 'LICENSE', 'NOTICE', 'WinDivert-LICENSE.txt')) {
        if (!(Test-Path -LiteralPath (Join-Path $extractDirectory $file) -PathType Leaf)) { throw "ZIP payload missing: $file" }
    }
    Invoke-PackageProcess (Join-Path $extractDirectory 'Limiter.exe') @('--verify-package')
}
finally {
    Remove-Item -LiteralPath $extractDirectory -Recurse -Force
}
if ($Install) {
    $principal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
    if (!$principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Install smoke test requires an elevated disposable runner.' }
    $installedDirectory = Join-Path $env:ProgramFiles 'Limiter'
    $installedExe = Join-Path $installedDirectory 'Limiter.exe'
    $desktopLink = Join-Path ([Environment]::GetFolderPath('CommonDesktopDirectory')) 'Limiter.lnk'
    $taskName = 'Limiter.Startup.' + [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    # Exercise separate clean installation lifecycles; the bundle must not merely repair the MSI.
    foreach ($kind in @('MSI', 'Setup')) {
        if (Test-Path $installedDirectory) { throw 'Refusing to replace an existing Limiter installation.' }
        if (Test-Path $desktopLink) { throw 'Refusing to replace an existing desktop shortcut.' }
        if (Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue) { throw 'Refusing to replace an existing startup task.' }
        try {
            if ($kind -eq 'MSI') {
                Invoke-PackageProcess 'msiexec.exe' @('/i', ('"' + $msi[0].FullName + '"'), '/qn', '/norestart', 'DESKTOPSHORTCUT=1', 'STARTWITHWINDOWS=1', '/l*v', ('"' + "$directory/msi-install.log" + '"'))
            }
            else {
                # The bundle's silent mode uses MSI defaults (both optional checkboxes off).
                Invoke-PackageProcess $setup[0].FullName @('/install', '/quiet', '/norestart', '/log', ('"' + "$directory/setup-install.log" + '"'))
            }
            if (!(Test-Path $installedExe)) { throw "$kind did not install the executable at the fixed Program Files path." }
            $task = Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
            if ($kind -eq 'MSI') {
                if (!(Test-Path $desktopLink)) { throw 'Missing installed desktop shortcut.' }
                if (!$task -or @($task.Actions).Count -ne 1 -or $task.Actions.Execute -ne $installedExe -or $task.Actions.Arguments -ne '--tray' -or $task.Principal.RunLevel -ne 'Highest') { throw 'Unexpected startup task definition.' }
            }
            elseif ((Test-Path $desktopLink) -or $task) { throw 'Silent setup enabled an opt-in desktop shortcut or startup task.' }
            Invoke-PackageProcess $installedExe @('--verify-package')
        }
        finally {
            # Always attempt cleanup, including an installation that failed partway through.
            if ($kind -eq 'MSI') {
                Invoke-PackageProcess 'msiexec.exe' @('/x', ('"' + $msi[0].FullName + '"'), '/qn', '/norestart', '/l*v', ('"' + "$directory/msi-uninstall.log" + '"'))
            }
            else {
                Invoke-PackageProcess $setup[0].FullName @('/uninstall', '/quiet', '/norestart', '/log', ('"' + "$directory/setup-uninstall.log" + '"'))
            }
        }
        if ((Test-Path $installedDirectory) -or (Test-Path $desktopLink) -or (Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue)) { throw "$kind uninstall left installed files, desktop shortcut or startup task." }
        Write-Output "PASS: $kind independent installation, package verification and uninstall cleanup."
    }
}
Write-Output 'PASS: all four package checksums, installer checkbox defaults, portable EXE and extracted ZIP native library.'
