<#
.SYNOPSIS
    Downloads a local STT model into the repo's .\models folder.

.DESCRIPTION
    By default the model lands in the repo's .\models folder. The app finds it there
    without any build-time copy: relative model paths are probed against the executable
    directory, %LOCALAPPDATA%\PushToTalkDictation, and the directories above the
    executable - see Stt\ModelPathResolver.cs.

    `dotnet publish` copies .\models next to the published executable, so a published
    folder is movable as a unit. To add a model to an existing published install instead,
    pass -Destination <install>\models.

.EXAMPLE
    .\download-models.ps1                       # Moonshine Base (English) - the default engine
    .\download-models.ps1 -Model moonshine-tiny
    .\download-models.ps1 -Model parakeet
    .\download-models.ps1 -Model whisper-base-q5
    .\download-models.ps1 -Model distil-whisper-small

.EXAMPLE
    # Drop a model into an already-published install.
    .\download-models.ps1 -Destination 'C:\Program Files\PushToTalkDictation\models'
#>
[CmdletBinding()]
param(
    [ValidateSet('moonshine-base', 'moonshine-tiny', 'parakeet', 'whisper-base-q5', 'distil-whisper-small')]
    [string]$Model = 'moonshine-base',

    [string]$Destination = (Join-Path $PSScriptRoot '..\models')
)

$ErrorActionPreference = 'Stop'

$sherpaRelease = 'https://github.com/k2-fsa/sherpa-onnx/releases/download/asr-models'

$archives = @{
    'moonshine-base' = "$sherpaRelease/sherpa-onnx-moonshine-base-en-int8.tar.bz2"
    'moonshine-tiny' = "$sherpaRelease/sherpa-onnx-moonshine-tiny-en-int8.tar.bz2"
    'parakeet'       = "$sherpaRelease/sherpa-onnx-nemo-parakeet-tdt-0.6b-v2-int8.tar.bz2"
}

$singleFiles = @{
    'whisper-base-q5'       = 'https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-base.en-q5_1.bin'
    'distil-whisper-small'  = 'https://huggingface.co/distil-whisper/distil-small.en/resolve/main/ggml-distil-small.en.bin'
}

New-Item -ItemType Directory -Force -Path $Destination | Out-Null
$Destination = (Resolve-Path $Destination).Path

if ($archives.ContainsKey($Model)) {
    $url     = $archives[$Model]
    $archive = Join-Path $env:TEMP ([System.IO.Path]::GetFileName($url))

    Write-Host "Downloading $url" -ForegroundColor Cyan
    Invoke-WebRequest -Uri $url -OutFile $archive -UseBasicParsing

    Write-Host "Extracting into $Destination" -ForegroundColor Cyan
    # Windows 10+ ships bsdtar, which reads .tar.bz2 natively.
    tar -xf $archive -C $Destination
    Remove-Item $archive -Force

    $folder = Join-Path $Destination ([System.IO.Path]::GetFileNameWithoutExtension($archive) -replace '\.tar$', '')
    Get-ChildItem -Path $folder -Filter 'test_wavs' -Directory -ErrorAction SilentlyContinue |
        Remove-Item -Recurse -Force

    Write-Host ""
    Write-Host "Installed into $folder" -ForegroundColor Green
    Write-Host "Done. Point appsettings.json at it:" -ForegroundColor Green
    Write-Host "  SpeechToText.Engine                    = `"SherpaOnnx`""
    if ($Model -eq 'parakeet') {
        Write-Host "  SpeechToText.SherpaOnnx.ModelKind      = `"Transducer`""
    } else {
        Write-Host "  SpeechToText.SherpaOnnx.ModelKind      = `"Moonshine`""
    }
    Write-Host "  SpeechToText.SherpaOnnx.ModelDirectory = `"models/$(Split-Path $folder -Leaf)`""
}
else {
    $url  = $singleFiles[$Model]
    $file = Join-Path $Destination ([System.IO.Path]::GetFileName($url))

    Write-Host "Downloading $url" -ForegroundColor Cyan
    Invoke-WebRequest -Uri $url -OutFile $file -UseBasicParsing

    Write-Host ""
    Write-Host "Installed into $file" -ForegroundColor Green
    Write-Host "Done. Point appsettings.json at it:" -ForegroundColor Green
    Write-Host "  SpeechToText.Engine                 = `"WhisperNet`""
    Write-Host "  SpeechToText.WhisperNet.ModelPath   = `"models/$(Split-Path $file -Leaf)`""
}

$defaultDestination = (Join-Path $PSScriptRoot '..\models')
if (-not (Test-Path $defaultDestination) -or
    $Destination -ne (Resolve-Path $defaultDestination).Path) {
    Write-Host ""
    Write-Host "Note: this is not the repo's .\models folder. If the app cannot find the" -ForegroundColor Yellow
    Write-Host "model there, set SpeechToText.ModelRoot to:" -ForegroundColor Yellow
    Write-Host "  $(Split-Path $Destination -Parent)"
}
