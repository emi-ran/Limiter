[CmdletBinding()]
param([string]$PackageDirectory = 'artifacts/packages', [switch]$Install)
$ErrorActionPreference = 'Stop'
$directory = (Resolve-Path -LiteralPath $PackageDirectory).Path
foreach ($line in Get-Content "$directory/SHA256SUMS.txt") {
    if ($line -notmatch '^([a-f0-9]{64})  ([^\\/]+)$') { throw 'Invalid checksum entry.' }
    $hash = Get-FileHash -LiteralPath (Join-Path $directory $Matches[2]) -Algorithm SHA256
    if ($hash.Hash -ne $Matches[1]) { throw "Checksum mismatch: $($hash.Path)" }
}
function Invoke-PackageProcess([string]$Path, [string[]]$Arguments) {
    $process = Start-Process -FilePath $Path -ArgumentList $Arguments -PassThru -WindowStyle Hidden
    $null = $process.Handle
    if (!$process.WaitForExit(120000)) { $process.Kill(); throw "Timed out: $Path" }
    if ($process.ExitCode -notin @(0, 3010)) { throw "$Path exited with $($process.ExitCode)." }
}
$portable = @(Get-ChildItem "$directory/*portable*.exe")
$msi = @(Get-ChildItem "$directory/*.msi")
if ($portable.Count -ne 1 -or $msi.Count -ne 1) { throw 'Expected one portable EXE and one MSI.' }
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
if ($Install) {
    $principal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
    if (!$principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Install smoke test requires an elevated disposable runner.' }
    $installedExe = Join-Path $env:ProgramFiles 'Limiter/Limiter.exe'
    if (Test-Path $installedExe) { throw 'Refusing to replace an existing Limiter installation.' }
    $desktopLink = Join-Path ([Environment]::GetFolderPath('CommonDesktopDirectory')) 'Limiter.lnk'
    if (Test-Path $desktopLink) { throw 'Refusing to replace an existing desktop shortcut.' }
    $taskName = 'Limiter.Startup.' + [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    if (Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue) { throw 'Refusing to replace an existing startup task.' }
    try {
        Invoke-PackageProcess 'msiexec.exe' @('/i', ('"' + $msi[0].FullName + '"'), '/qn', '/norestart', 'DESKTOPSHORTCUT=1', 'STARTWITHWINDOWS=1', '/l*v', ('"' + "$directory/install.log" + '"'))
        if (!(Test-Path $installedExe) -or !(Test-Path $desktopLink)) { throw 'Missing installed executable or desktop shortcut.' }
        $task = Get-ScheduledTask -TaskName $taskName
        if ($task.Actions.Execute -ne $installedExe -or $task.Principal.RunLevel -ne 'Highest') { throw 'Unexpected startup task definition.' }
        Invoke-PackageProcess $installedExe @('--verify-package')
    }
    finally {
        if (Test-Path $installedExe) {
            Invoke-PackageProcess 'msiexec.exe' @('/x', ('"' + $msi[0].FullName + '"'), '/qn', '/norestart', '/l*v', ('"' + "$directory/uninstall.log" + '"'))
        }
    }
    if ((Test-Path $installedExe) -or (Test-Path $desktopLink) -or (Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue)) { throw 'Uninstall left installed files, desktop shortcut or startup task.' }
}
Write-Output 'PASS: package checksums, installer checkbox defaults and portable native library.'
if ($Install) { Write-Output 'PASS: MSI installation, desktop shortcut, elevated startup task and uninstall cleanup.' }
