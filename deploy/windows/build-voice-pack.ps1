# Prepares the local voice pack for patient calling. Daily calling never uses this script or the internet.
#   Check     Reports missing clips in -VoiceDir. No network.
#   Export    Writes every clip name and its text to -ExportFile, for producing the audio elsewhere. No network.
#   Generate  Creates missing clips with your own Azure Speech resource. Needs internet, $env:AZURE_SPEECH_KEY and -Region. Billed by Azure.
#   Import    Copies clips you already hold a licence for from -SourceDir into -VoiceDir. No network.
# Runs on Windows PowerShell 5.1. The spoken text lives in voice-phrases.json so this file stays plain ASCII.
param(
    [ValidateSet('Check', 'Export', 'Generate', 'Import')][string]$Mode = 'Check',
    [string]$VoiceDir = 'C:\AlShifa\storage\voice',
    [string]$Region,
    [string]$SourceDir,
    [string]$ExportFile,
    [string]$Endpoint
)

$ErrorActionPreference = 'Stop'
$phrases = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'voice-phrases.json'), [Text.Encoding]::UTF8) | ConvertFrom-Json

function Get-NumberWords([int]$Number) {
    $parts = @()
    $hundreds = [Math]::Floor($Number / 100)
    $rest = $Number % 100
    if ($hundreds -gt 0) { $parts += $phrases.hundreds[$hundreds] }
    if ($rest -ge 20) {
        $parts += $phrases.tens[[Math]::Floor($rest / 10)]
        if ($rest % 10 -gt 0) { $parts += $phrases.units[$rest % 10] }
    } elseif ($rest -gt 0) {
        $parts += $phrases.units[$rest]
    }
    return $parts -join ' ' + [char]0x0648 + ' '
}

function Get-Clips {
    $clips = New-Object 'System.Collections.Generic.List[object]'
    foreach ($n in 1..[int]$phrases.maxNumber) {
        $clips.Add([pscustomobject]@{ File = ('number-{0:D4}.mp3' -f $n); Text = ($phrases.number -f (Get-NumberWords $n)) })
    }
    $places = @()
    foreach ($section in $phrases.sections.PSObject.Properties) { $places += [pscustomobject]@{ Key = $section.Name; Label = $section.Value } }
    foreach ($room in 1..[int]$phrases.maxRoom) { $places += [pscustomobject]@{ Key = ('room-{0:D2}' -f $room); Label = ($phrases.room -f (Get-NumberWords $room)) } }
    foreach ($place in $places) {
        $clips.Add([pscustomobject]@{ File = "to-$($place.Key).mp3"; Text = ($phrases.call -f $place.Label) })
        $clips.Add([pscustomobject]@{ File = "again-$($place.Key).mp3"; Text = ($phrases.recall -f $place.Label) })
    }
    return $clips
}

# ID3 tag or an MPEG audio frame. Stops an HTML error page or a renamed file from reaching the speaker.
function Test-Mp3([string]$Path) {
    $item = Get-Item -LiteralPath $Path
    if ($item.Length -lt 200) { return $false }
    $stream = [IO.File]::OpenRead($Path)
    try {
        $head = New-Object byte[] 3
        [void]$stream.Read($head, 0, 3)
    } finally {
        $stream.Dispose()
    }
    return ($head[0] -eq 0x49 -and $head[1] -eq 0x44 -and $head[2] -eq 0x33) -or ($head[0] -eq 0xFF -and ($head[1] -band 0xE0) -eq 0xE0)
}

function Invoke-Speech([string]$Url, [string]$Key, [string]$Text, [string]$OutFile) {
    $ssml = "<speak version='1.0' xml:lang='$($phrases.language)'><voice name='$($phrases.voice)'><prosody rate='$($phrases.rate)'>$([Security.SecurityElement]::Escape($Text))</prosody></voice></speak>"
    $headers = @{ 'Ocp-Apim-Subscription-Key' = $Key; 'X-Microsoft-OutputFormat' = 'audio-24khz-96kbitrate-mono-mp3'; 'User-Agent' = 'AlShifaVoicePack' }
    foreach ($attempt in 1..5) {
        try {
            Invoke-WebRequest -Uri $Url -Method Post -Headers $headers -ContentType 'application/ssml+xml; charset=utf-8' -Body ([Text.Encoding]::UTF8.GetBytes($ssml)) -OutFile $OutFile -UseBasicParsing | Out-Null
            return
        } catch {
            $code = if ($_.Exception.Response) { [int]$_.Exception.Response.StatusCode } else { 0 }
            # A refused key or region will not change on retry; anything else may be a rate limit or a passing fault.
            if ($code -in 400, 401, 403, 404 -or $attempt -eq 5) { throw "Speech service failed with HTTP ${code}: $($_.Exception.Message)" }
            Start-Sleep -Seconds ($attempt * 5)
        }
    }
}

$clips = Get-Clips

if ($Mode -eq 'Export') {
    if (-not $ExportFile) { throw 'Export needs -ExportFile.' }
    $clips | Export-Csv -LiteralPath $ExportFile -NoTypeInformation -Encoding UTF8
    "Wrote $($clips.Count) clips to $ExportFile"
    return
}

New-Item -ItemType Directory -Force $VoiceDir | Out-Null

if ($Mode -eq 'Import') {
    if (-not $SourceDir) { throw 'Import needs -SourceDir.' }
    $copied = 0
    foreach ($clip in $clips) {
        $source = Join-Path $SourceDir $clip.File
        if (-not (Test-Path -LiteralPath $source)) { continue }
        if (-not (Test-Mp3 $source)) { throw "$source is not a valid MP3 file." }
        Copy-Item -LiteralPath $source (Join-Path $VoiceDir $clip.File) -Force
        $copied++
    }
    "Copied $copied clips."
}

if ($Mode -eq 'Generate') {
    $key = $env:AZURE_SPEECH_KEY
    if (-not $key) { throw 'Set $env:AZURE_SPEECH_KEY to your Azure Speech key. It is never written to disk.' }
    if (-not $Endpoint) {
        if (-not $Region) { throw 'Generate needs -Region, the region of your Azure Speech resource.' }
        $Endpoint = "https://$Region.tts.speech.microsoft.com/cognitiveservices/v1"
    }
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

    # Existing clips are skipped, so an interrupted run resumes without paying for the same text twice.
    $todo = @($clips | Where-Object { -not (Test-Path -LiteralPath (Join-Path $VoiceDir $_.File)) })
    "Generating $($todo.Count) clips, $(($todo | ForEach-Object { $_.Text.Length } | Measure-Object -Sum).Sum) characters, voice $($phrases.voice)."
    $done = 0
    foreach ($clip in $todo) {
        $target = Join-Path $VoiceDir $clip.File
        $partial = "$target.part"
        Invoke-Speech $Endpoint $key $clip.Text $partial
        if (-not (Test-Mp3 $partial)) {
            Remove-Item -LiteralPath $partial
            throw "The service returned something other than MP3 audio for $($clip.File)."
        }
        Move-Item -LiteralPath $partial $target
        $done++
        if ($done % 100 -eq 0) { "  $done of $($todo.Count)" }
    }
}

$missing = @($clips | Where-Object { -not (Test-Path -LiteralPath (Join-Path $VoiceDir $_.File)) })
$invalid = @($clips | Where-Object { (Test-Path -LiteralPath (Join-Path $VoiceDir $_.File)) -and -not (Test-Mp3 (Join-Path $VoiceDir $_.File)) })
"$($clips.Count) clips required, $($missing.Count) missing, $($invalid.Count) invalid, in $VoiceDir"
if ($missing.Count -or $invalid.Count) {
    ($missing + $invalid | Select-Object -First 10 | ForEach-Object { "  $($_.File)" }) -join "`n"
    exit 1
}
"Voice pack is complete. Daily calling needs no internet."
