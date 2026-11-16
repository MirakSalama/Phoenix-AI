param(
    [ValidateSet('lfm350', 'lfm3', 'qwen', 'gemma', 'd1')][string]$Model,
    [string]$FromFile
)
$ErrorActionPreference = 'Stop'
$catalog = [ordered]@{
    lfm350 = @{
        Folder = 'lfm25-350m'; File = 'LFM2.5-350M-Q4_K_M.gguf'; Bytes = 229312224L
        Source = 'https://huggingface.co/LiquidAI/LFM2.5-350M-GGUF/resolve/bb7ee58b243e4cede04187e323e760b04f8a0091'
        Hash = '7e6f72643caafc9a68256686638c4d7916f2cec76d1df478d4c3ddcd95a6aed4'
    }
    lfm3 = @{
        Folder = 'lfm3-1.7b'; File = 'LFM3-1.7B-QAD-Q4_0.gguf'; Bytes = 1049615680L
        Source = 'https://huggingface.co/LiquidAI/LFM3-1.7B-GGUF/resolve/b3f2ae5d73cd3c91bf40e8ba752c648aa05dffdf'
        Hash = '43a7d09a0e9cba91b63eb7052488b9564569bddfd65754d2e426465e572a7452'
    }
    qwen = @{
        Folder = 'qwen3.5-2b'; File = 'Qwen3.5-2B-Q4_K_M.gguf'; Bytes = 1270808032L
        Source = 'https://huggingface.co/lmstudio-community/Qwen3.5-2B-GGUF/resolve/bb84e11355a036e28f080c7793fa6d22b7c4e344'
        Hash = '0bfe35afc9f05b7fac3fa04925e051ac7939a42a8a17ea11afc99701bea826cc'
    }
    gemma = @{
        Folder = 'gemma4-e2b'; File = 'gemma-4-E2B-it-Q4_0.gguf'; Bytes = 2841481184L
        Source = 'https://huggingface.co/ggml-org/gemma-4-E2B-it-GGUF/resolve/64ef033dc9f85a88f88e70cceb0a7457366bea64'
        Hash = '8e30dff3ac4c8434c49a7036fa15564bdbb6044e42bf04550bf1a096ad7e6a52'
    }
    d1 = @{
        Folder = 'd1-omni-600m'; File = 'd1-omni-600M-Q8_0.gguf'; Bytes = 407207584L
        Source = 'https://huggingface.co/LiquidAI/d1-omni-600M-GGUF/resolve/04397145ed8381350403aa556db0ec6a49dd8c07'
        Hash = 'cd94463f4cac9c700ec6750df8df005bb6846de410e159d1f5e985e1171693d3'
    }
}
$root = Split-Path -Parent $PSScriptRoot
if (Test-Path -LiteralPath (Join-Path $root 'App\phoenix-compact.release')) {
    $modelRoot = $PSScriptRoot
} elseif (Test-Path -LiteralPath (Join-Path $root 'phoenix-portable.release')) {
    $modelRoot = Join-Path $root 'cache'
} else {
    throw 'Run this from the Models or Downloadables folder inside the extracted Phoenix package.'
}
if (!$Model) {
    $choices = @($catalog.Keys)
    for ($i = 0; $i -lt $choices.Count; $i++) {
        $entry = $catalog[$choices[$i]]
        Write-Host ("{0}. {1} ({2:N2} GB)" -f ($i + 1), $entry.File, ($entry.Bytes / 1e9))
    }
    $selection = Read-Host 'Select a model by number or press Enter to close'
    if ($selection -notmatch '^[1-9][0-9]?$' -or [int]$selection -gt $choices.Count) { exit 0 }
    $Model = $choices[[int]$selection - 1]
}
$entry = $catalog[$Model]
$destination = Join-Path $modelRoot $entry.Folder
$file = Join-Path $destination $entry.File
$url = "$($entry.Source)/$($entry.File)"
function Test-Model([string]$Path) {
    if (!(Test-Path -LiteralPath $Path -PathType Leaf)) { return $false }
    return ((Get-Item -LiteralPath $Path).Length -eq $entry.Bytes) -and
        ((Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash -eq $entry.Hash)
}
if (Test-Model $file) {
    if ($Model -eq 'd1') {
        Write-Host 'The d1-omni-600M router is already installed. Find its toggle in Phoenix Settings.'
    } else {
        Write-Host 'This model is already installed. Restart Phoenix to refresh its model list.'
    }
    exit 0
}
if ($FromFile) {
    if (!(Test-Model $FromFile)) { throw 'The selected file does not match the publisher checksum and size.' }
} else {
    Write-Host ("File     {0}" -f $entry.File)
    Write-Host ("Source   {0}" -f $url)
    Write-Host ("Size     {0:N0} bytes ({1:N2} GB)" -f $entry.Bytes, ($entry.Bytes / 1e9))
    if ($entry.Bytes -gt 2000000000) { Write-Host 'This file exceeds 2 GB.' }
    if ((Read-Host 'Type DOWNLOAD to approve this file') -cne 'DOWNLOAD') { exit 0 }
}
[void](New-Item -Path $destination -ItemType Directory -Force)
$temporary = "$file.$([guid]::NewGuid().ToString('N')).partial"
try {
    if ($FromFile) { Copy-Item -LiteralPath $FromFile -Destination $temporary }
    else {
        $transfer = @('--fail', '--location', '--proto', '=https', '--proto-redir', '=https',
            '--connect-timeout', '30', '--progress-bar', '--max-filesize', $entry.Bytes,
            '--output', $temporary, ($url + '?download=true'))
        & curl.exe @transfer
        if ($LASTEXITCODE -ne 0) { throw "Download failed (curl exit code $LASTEXITCODE)." }
    }
    if (!(Test-Model $temporary)) { throw 'The downloaded file does not match the publisher checksum and size.' }
    if (Test-Path -LiteralPath $file) {
        [IO.File]::Replace($temporary, $file, $null)
    } else {
        [IO.File]::Move($temporary, $file)
    }
    if ($Model -eq 'd1') {
        Write-Host 'Installed the d1-omni-600M router. Its toggle is in Phoenix Settings.'
    } else {
        Write-Host "Installed $($entry.File). Restart Phoenix and choose it in the model picker."
    }
} finally {
    if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force }
}
