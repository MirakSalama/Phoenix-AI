# OpenAI-compatible server for LFM2.5-230M on the Phoenix NPU.
# Uses the precompiled eight-core programs, so no IRON or Visual Studio needed.
# Usage: .\scripts\serve_openai.ps1 [-Port 8645]
param(
    [int]$Port = 8645,
    [string]$BindAddress = '127.0.0.1'
)
$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$iron = Join-Path $repoRoot 'cache\iron'
$python = Join-Path $iron 'mlir-aie\ironenv\Scripts\python.exe'
$programs = Join-Path $repoRoot 'cache\phoenix-portable-programs'
if (-not (Test-Path -LiteralPath $python -PathType Leaf)) {
    Write-Host "Python environment missing at $python"
    exit 1
}
if (-not (Test-Path -LiteralPath (Join-Path $programs 'decode-4096-head.xclbin') -PathType Leaf)) {
    Write-Host "Precompiled NPU programs missing at $programs"
    exit 1
}
$env:PATH = "$iron\xrt_sdk\xrt\lib;$iron\xrt_sdk\xrt;C:\Windows\System32\AMD;$env:PATH"
$env:PYTHONPATH = "$iron\xrt_sdk\xrt\python"
$env:PHOENIX_NPU_PROGRAMS = $programs
$env:PYTHONIOENCODING = 'utf-8'
Push-Location $repoRoot
try {
    & $python 'experiments\openai_server.py' --host $BindAddress --port $Port
    $result = $LASTEXITCODE
} finally {
    Pop-Location
}
exit $result
