param([Parameter(Mandatory=$true)][string]$Version)
$ErrorActionPreference = 'Stop'
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw 'Use a release version such as 1.2.1.' }
$taskProject = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$taskName = "Capy Bara's Mod Manager v" + $Version
$taskExecutable = 'CapyBaraModManager.exe'
$taskSource = Join-Path (Join-Path $taskProject 'artifacts') ('CapyBara-Mod-Manager-v' + $Version)
if (-not (Test-Path -LiteralPath (Join-Path $taskSource $taskExecutable))) { throw 'Publish the release app first.' }
$taskDesktop = [Environment]::GetFolderPath('Desktop')
$taskTarget = Join-Path $taskDesktop $taskName
$taskSuffix = 2
while (Test-Path -LiteralPath $taskTarget) { $taskTarget = Join-Path $taskDesktop ($taskName + ' (' + $taskSuffix + ')'); $taskSuffix++ }
New-Item -ItemType Directory -Path $taskTarget | Out-Null
$taskFiles = @(Get-ChildItem -LiteralPath $taskSource -Recurse -File)
foreach ($taskFile in $taskFiles) {
    $taskRelative = [IO.Path]::GetRelativePath($taskSource, $taskFile.FullName)
    $taskDestination = [IO.Path]::GetFullPath((Join-Path $taskTarget $taskRelative))
    if (-not $taskDestination.StartsWith($taskTarget + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'App file escapes the new app folder.' }
    New-Item -ItemType Directory -Path (Split-Path $taskDestination -Parent) -Force | Out-Null
    Copy-Item -LiteralPath $taskFile.FullName -Destination $taskDestination
    if ((Get-FileHash -LiteralPath $taskFile.FullName).Hash -ne (Get-FileHash -LiteralPath $taskDestination).Hash) { throw 'App copy verification failed.' }
}
$taskShortcut = Join-Path $taskDesktop "Capy Bara's Mod Manager.lnk"
$taskLegacyShortcut = Join-Path $taskDesktop 'MX Sim Mod Manager.lnk'
if (Test-Path -LiteralPath $taskShortcut) {
    Copy-Item -LiteralPath $taskShortcut -Destination (Join-Path $taskTarget 'Previous version.lnk')
    if (Test-Path -LiteralPath $taskLegacyShortcut) { Move-Item -LiteralPath $taskLegacyShortcut -Destination (Join-Path $taskTarget 'Previous legacy Desktop shortcut.lnk') }
} elseif (Test-Path -LiteralPath $taskLegacyShortcut) {
    Copy-Item -LiteralPath $taskLegacyShortcut -Destination (Join-Path $taskTarget 'Previous version.lnk')
    Move-Item -LiteralPath $taskLegacyShortcut -Destination $taskShortcut
}
$taskShell = New-Object -ComObject WScript.Shell
$taskLink = $taskShell.CreateShortcut($taskShortcut)
$taskLink.TargetPath = Join-Path $taskTarget $taskExecutable
$taskLink.Arguments = ''
$taskLink.WorkingDirectory = $taskTarget
$taskLink.IconLocation = $taskLink.TargetPath + ',0'
$taskLink.Description = "Capy Bara's Mod Manager v" + $Version
$taskLink.Save()
$taskReport = [pscustomobject]@{AppFolder=$taskTarget; Executable=$taskLink.TargetPath; Shortcut=$taskShortcut; FilesVerified=$taskFiles.Count; PreviousVersionPreserved=$true; ManagerDataChanged=$false; GameFilesInstalled=$false}
$taskReport | ConvertTo-Json -Depth 5 | Tee-Object -FilePath (Join-Path $taskProject ('artifacts\desktop-deployment-v' + $Version + '.json'))
