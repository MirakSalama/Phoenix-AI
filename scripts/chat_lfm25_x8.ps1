# Interactive LFM2.5-230M chat on the Phoenix NPU (experiment 010 engine).
# Usage: .\scripts\chat_lfm25_x8.ps1 [-Message "text"] [-MaxNewTokens 256]
param(
    [Alias('Prompt')][string]$Message,
    [int]$MaxNewTokens = 256,
    [switch]$Gui,
    [switch]$WinUI
)
$ErrorActionPreference = 'Stop'
function Write-LauncherLog([string]$Message) {
    if (!$WinUI -or !$env:NPU_CHAT_LOG) { return }
    try {
        Add-Content -LiteralPath $env:NPU_CHAT_LOG -Encoding UTF8 -Value "$(Get-Date -Format o) [launcher] $Message"
    } catch {
        Write-Verbose "Could not write the launch log: $($_.Exception.Message)"
    }
}

if ($WinUI) {
    Remove-Item Env:\NPU_CHAT_LOG -ErrorAction SilentlyContinue
    try {
        $logFolder = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'NpuChat\Logs'
        New-Item -ItemType Directory -Path $logFolder -Force | Out-Null
        $stamp = [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss.fff')
        $env:NPU_CHAT_LOG = Join-Path $logFolder "npu-chat-$stamp-$PID.log"
        Write-LauncherLog "Starting WinUI launcher PID $PID"
    } catch {
        Write-Verbose "Could not create a launch log: $($_.Exception.Message)"
    }
}
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$python = Join-Path $repoRoot 'cache\iron\mlir-aie\ironenv\Scripts\python.exe'
$ironEnv = Join-Path $repoRoot 'cache\iron\mlir-aie\iron_env.ps1'
$model = Join-Path $repoRoot 'cache\lfm25-230m\model.safetensors'
if (-not $WinUI -and -not (Test-Path -LiteralPath $model -PathType Leaf)) {
    Write-LauncherLog "LFM model weights missing at $model"
    Write-Host "LFM2.5 model weights are missing. Expected $model"
    exit 1
}
$hasIron = (Test-Path -LiteralPath $python -PathType Leaf) -and
    (Test-Path -LiteralPath $ironEnv -PathType Leaf)
if (-not $WinUI -and -not $hasIron) {
    Write-LauncherLog "IRON setup missing. Python: $python. Activation script: $ironEnv"
    Write-Host "The Windows XRT and IRON environment is not set up. See $repoRoot\docs\setup.md"
    exit 1
}

$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (Test-Path -LiteralPath $vswhere -PathType Leaf) {
    $devShell = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 `
        -find 'Common7\Tools\Launch-VsDevShell.ps1' | Select-Object -First 1
}
if (-not $WinUI -and -not $devShell) {
    Write-LauncherLog 'Visual Studio C++ build tools were not found'
    Write-Host 'Visual Studio with the C++ build tools was not found.'
    exit 1
}
# Build the arguments first: the venv activation script sets its own $Prompt.
if ($WinUI) {
    $gpuPython = $python
    if (-not (Test-Path -LiteralPath $gpuPython -PathType Leaf)) {
        $gpuPython = Join-Path $repoRoot 'cache\gemma-iron-env\Scripts\python.exe'
    }
    if (-not (Test-Path -LiteralPath $gpuPython -PathType Leaf)) {
        $gpuPython = (Get-Command python.exe -ErrorAction SilentlyContinue |
            Where-Object Source -NotLike '*\WindowsApps\*' | Select-Object -First 1).Source
    }
    if (-not $gpuPython) {
        Write-Host 'A local Python installation is required to run the chat workers.'
        exit 1
    }
    $app = Join-Path $repoRoot 'apps\npu_chat_winui\bin\winui-workspace\PhoenixAI.exe'
    if (-not (Test-Path -LiteralPath $app -PathType Leaf)) {
        Write-LauncherLog "WinUI binary missing at $app"
        Write-Host 'Build the WinUI app first with dotnet build apps\npu_chat_winui\NpuChat.csproj -c Release -o apps\npu_chat_winui\bin\winui-workspace.'
        exit 1
    }
} elseif ($Gui) {
    $arguments = @('experiments\lfm25_chat_ui.py', '--max-new-tokens', "$MaxNewTokens")
} else {
    $arguments = @('experiments\010_lfm25_npu_chat.py', '--max-new-tokens', "$MaxNewTokens")
    if ($PSBoundParameters.ContainsKey('Message')) { $arguments += @('--prompt', $Message) }
}
Push-Location $repoRoot
try {
    $npuPython = $null
    if ($hasIron -and $devShell) {
        try {
            Write-LauncherLog "Loading Visual Studio environment from $devShell and IRON environment from $ironEnv"
            & $devShell -Arch amd64 -SkipAutomaticLocation | Out-Null
            . $ironEnv
            $npuPython = $python
        } catch {
            if (-not $WinUI) { throw }
            Write-LauncherLog "NPU environment unavailable: $($_.Exception.Message)"
        }
    }
    $env:PYTHONIOENCODING = 'utf-8'
    if (-not $env:NPU_CACHE_HOME) {
        $env:NPU_CACHE_HOME = Join-Path $repoRoot 'cache\npu-programs'
    }
    if ($WinUI) {
        $env:NPU_CHAT_PYTHON = $npuPython
        $env:NPU_CHAT_GPU_PYTHON = $gpuPython
        Write-LauncherLog "Launching $app. NPU Python: $npuPython. GPU Python: $gpuPython. XRT: $env:XRT_ROOT"
        Start-Process -FilePath $app -WorkingDirectory $repoRoot
        $result = 0
    } else {
        Write-Host 'Loading LFM2.5. Compiling NPU programs on the first run can take a few minutes.'
        # Not piped, so the "You:" prompt and streamed tokens appear immediately.
        & $python @arguments
        $result = $LASTEXITCODE
    }
} catch {
    Write-LauncherLog "Launch failed: $($_.Exception.Message)"
    throw
} finally {
    Pop-Location
}
exit $result
