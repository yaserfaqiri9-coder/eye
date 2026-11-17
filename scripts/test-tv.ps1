$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../deploy/windows-tv/Common.ps1')
$checks = 0
function Check([bool]$Condition, [string]$Message) { if (-not $Condition) { throw $Message }; $script:checks++ }
function Reject([scriptblock]$Action, [string]$Message) { $failed = $false; try { & $Action | Out-Null } catch { $failed = $true }; Check $failed $Message }
Check ((Get-TvOrigin 'https://hospital.example.com/') -eq 'https://hospital.example.com') 'Origin normalization'
Check ((Get-TvOrigin 'http://localhost:5080') -eq 'http://localhost:5080') 'Local origin'
foreach ($bad in @('file:///C:/Windows','javascript:alert(1)','https://user:pass@hospital.example.com','https://hospital.example.com/patients','https://hospital.example.com/?cmd=calc','https://hospital.example.com/#x','https://hospital.example.com/" --app=evil')) {
    Reject { Get-TvOrigin $bad } ('Accepted unsafe origin: ' + $bad)
}
Assert-TvProtocol 'alshifa-tv://open?origin=https%3A%2F%2Fhospital.example.com' 'https://hospital.example.com'; $checks++
foreach ($bad in @(
 'alshifa-tv://open?origin=https%3A%2F%2Fevil.example',
 'alshifa-tv://open?origin=https%3A%2F%2Fhospital.example.com&cmd=calc',
 'alshifa-tv://open?origin=https%3A%2F%2Fhospital.example.com&origin=https%3A%2F%2Fevil.example',
 'alshifa-tv://execute?origin=https%3A%2F%2Fhospital.example.com',
 'alshifa-tv://open/path?origin=https%3A%2F%2Fhospital.example.com',
 'alshifa-tv://open:123?origin=https%3A%2F%2Fhospital.example.com',
 'alshifa-tv://open?origin=https%3A%2F%2Fhospital.example.com%2Fpatients',
 'alshifa-tv://open?origin=https%3A%2F%2Fhospital.example.com#calc',
 'alshifa-tv://open?origin=https%3A%2F%2Fhospital.example.com%22%20-ConfigPath%20evil',
 'https://open?origin=https%3A%2F%2Fhospital.example.com'
)) { Reject { Assert-TvProtocol $bad 'https://hospital.example.com' } ('Accepted unsafe protocol: ' + $bad) }
$primary = [pscustomobject]@{DeviceName='primary';Primary=$true;X=0;Y=0;Width=1920;Height=1080}
$tv = [pscustomobject]@{DeviceName='tv';Primary=$false;X=-1920;Y=0;Width=1920;Height=1080}
$third = [pscustomobject]@{DeviceName='third';Primary=$false;X=1920;Y=0;Width=1280;Height=720}
Check ((Get-TvDisplay @($primary,$tv) '').X -eq -1920) 'Negative coordinates retained'
Check ((Get-TvDisplay @($primary,$tv,$third) 'tv').DeviceName -eq 'tv') 'Configured TV selected'
Reject { Get-TvDisplay @($primary) '' } 'Primary-only display was accepted'
Reject { Get-TvDisplay @($primary,$tv,$third) '' } 'Ambiguous secondary was accepted'
Reject { Get-TvDisplay @($primary,$tv) 'primary' } 'Primary display was accepted explicitly'
Reject { Get-TvDisplay @($primary,$tv) 'missing' } 'Missing preferred TV was accepted'
& {
    function Get-CimInstance {
        @(
            [pscustomobject]@{ProcessId=11;CommandLine='msedge.exe --user-data-dir="C:\Test User\TV\BrowserProfile" --app=http://hospital/call-screen'},
            [pscustomobject]@{ProcessId=12;CommandLine='msedge.exe "--user-data-dir=C:\Test User\TV\BrowserProfile"'},
            [pscustomobject]@{ProcessId=13;CommandLine='msedge.exe --user-data-dir="C:\Test User\TV\BrowserProfile-other"'},
            [pscustomobject]@{ProcessId=14;CommandLine='msedge.exe --user-data-dir="C:\Test User\Employee"'},
            [pscustomobject]@{ProcessId=15;CommandLine=$null}
        )
    }
    $matched = @(Get-TvProcessIds 'C:\Test User\TV\BrowserProfile')
    Check (($matched -join ',') -eq '11,12') 'Browser matching selected a non-TV profile'
}
$files = Get-ChildItem (Join-Path $PSScriptRoot '../deploy/windows-tv') -Filter '*.ps1'
foreach ($file in $files) {
    $tokens = $null; $parseErrors = $null
    [void][Management.Automation.Language.Parser]::ParseFile($file.FullName, [ref]$tokens, [ref]$parseErrors)
    Check ($parseErrors.Count -eq 0) ('PowerShell parse errors in ' + $file.Name)
}
if (-not ('AlShifaTvNative' -as [type])) { Add-Type -Path (Join-Path $PSScriptRoot '../deploy/windows-tv/Native.cs') }
$nativeDisplays = @([AlShifaTvNative]::Displays())
Check ($nativeDisplays.Count -ge 1) 'Native monitor enumeration failed'
$plan = & (Join-Path $PSScriptRoot '../deploy/windows-tv/Install-TV.ps1') -AppOrigin http://localhost:5173 -EnableStartup -ValidateOnly
Check ($plan.ChangesMade -eq $false) 'Installer dry run mutated configuration'
Check ($plan.ProtocolCommand -match '\-File ".*Launch-TV.ps1" -ProtocolUri "%1"$') 'Unsafe protocol command quoting'
Check ($plan.Startup -eq $true) 'Autostart plan not retained'
Write-Host ("PASS: {0} checks, native helper compiled, {1} monitor(s) enumerated. No registry/display/startup changes." -f $checks,$nativeDisplays.Count)
