# Run a Python script in the IRON environment with Visual Studio tools loaded.
# Usage: & .\scripts\iron_python.ps1 experiments\script.py [args...]
$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$python = Join-Path $repoRoot 'cache\iron\mlir-aie\ironenv\Scripts\python.exe'
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$devShell = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 `
    -find 'Common7\Tools\Launch-VsDevShell.ps1' | Select-Object -First 1
if (-not $devShell) { throw 'Visual Studio with the C++ build tools was not found.' }
Push-Location $repoRoot
try {
    & $devShell -Arch amd64 -SkipAutomaticLocation | Out-Null
    . .\cache\iron\mlir-aie\iron_env.ps1
    $env:PYTHONIOENCODING = 'utf-8'
    $ErrorActionPreference = 'Continue'
    & $python @args 2>&1 | ForEach-Object { "$_" }
    if ($LASTEXITCODE -ne 0) { throw "Python exited with code $LASTEXITCODE." }
} finally {
    Pop-Location
}
