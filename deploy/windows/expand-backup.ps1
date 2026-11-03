# Decrypts a backup into a folder (db.sql, storage\, keys\), for a real recovery. See DEPLOYMENT-WINDOWS.md.
# The result contains patient data in the clear: use an encrypted disk and delete the folder afterwards.
# Without these scripts: open the .gpg file in Kleopatra with the printed password, then unzip the result.
param(
    [Parameter(Mandatory)][string]$BackupFile,
    [Parameter(Mandatory)][string]$OutputDir,
    [Parameter(Mandatory)][string]$PasswordFile,
    [string]$Gpg
)

. (Join-Path $PSScriptRoot 'common.ps1')

if ((Test-Path $OutputDir) -and (Get-ChildItem $OutputDir -Force | Select-Object -First 1)) { throw "$OutputDir is not empty." }
Expand-Backup (Find-Gpg $Gpg) $BackupFile $OutputDir $PasswordFile
"Decrypted into $OutputDir"
