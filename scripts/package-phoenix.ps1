param(
    [ValidatePattern('^Phoenix-AI-[A-Za-z0-9._-]+$')][string]$Name,
    [string]$DictateBundle = (Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'NPU Dictate\model-v1'),
    [string]$VoicePythonHome = (Join-Path $env:ProgramFiles 'Python312'),
    [switch]$RuntimeOnly
)

$ErrorActionPreference = 'Stop'
if (!$RuntimeOnly) {
    $compact = Join-Path $PSScriptRoot 'package-compact-phoenix.ps1'
    if ($Name) { & $compact -Name $Name }
    else { & $compact }
    return
}
if (!$Name) { $Name = 'Phoenix-AI-win-x64' }
$project = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$dist = Join-Path $project 'dist'
$release = Join-Path $dist $Name
if (Test-Path -LiteralPath $release) {
    throw 'Choose a fresh release name to avoid overwriting an existing package.'
}

function Copy-Files([string]$From, [string]$To, [string[]]$Names) {
    [void](New-Item -Path $To -ItemType Directory -Force)
    foreach ($file in $Names) {
        Copy-Item -LiteralPath (Join-Path $From $file) -Destination $To -Force
    }
}

function Copy-Tree([string]$From, [string]$To, [string[]]$Extra = @()) {
    $copyArgs = @($From, $To, '/E', '/R:1', '/W:1', '/NFL', '/NDL', '/NJH', '/NJS', '/NC', '/NS',
        '/XD', '__pycache__', 'test', 'tests', 'site-packages',
        '/XF', '*.pyc', '*.pdb', '*.exp', '*.lib') + $Extra
    & robocopy.exe @copyArgs | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "Could not copy $From (robocopy exit $LASTEXITCODE)." }
}

function Copy-Python([string]$From, [string]$To, [string]$Version) {
    $binaries = @('python.exe', 'python3.dll', "python$Version.dll")
    $binaries += @(Get-ChildItem -LiteralPath $From -Filter '*.dll' -File |
        Where-Object Name -notin $binaries | Select-Object -ExpandProperty Name)
    $binaries += @(Get-ChildItem -LiteralPath $From -Filter 'LICENSE*.txt' -File | Select-Object -ExpandProperty Name)
    Copy-Files $From $To $binaries
    Copy-Tree (Join-Path $From 'DLLs') (Join-Path $To 'DLLs')
    Copy-Tree (Join-Path $From 'Lib') (Join-Path $To 'Lib')
    [void](New-Item -Path (Join-Path $To 'Lib\site-packages') -ItemType Directory -Force)
}

function Copy-Packages([string]$From, [string]$To, [string[]]$Patterns) {
    foreach ($entry in Get-ChildItem -LiteralPath $From) {
        if (!($Patterns | Where-Object { $entry.Name -like $_ })) { continue }
        if ($entry.PSIsContainer) { Copy-Tree $entry.FullName (Join-Path $To $entry.Name) }
        else { Copy-Item -LiteralPath $entry.FullName -Destination $To }
    }
}

$cache = Join-Path $project 'cache'
$sources = Join-Path $PSScriptRoot 'portable'
$cpuEncoder = Join-Path $cache 'phoenix-portable-speech\encoder-model.int8.onnx'
$programs = Join-Path $cache 'phoenix-portable-programs'
$manifest = Get-Content -LiteralPath (Join-Path $DictateBundle 'manifest.json') -Raw | ConvertFrom-Json
if ($manifest.version -ne 1 -or $manifest.device -notmatch 'npu1') { throw 'Expected the XDNA 1 Parakeet v1 bundle.' }
foreach ($property in $manifest.files.PSObject.Properties) {
    if ([IO.Path]::GetFileName($property.Name) -ne $property.Name) { throw 'Invalid Parakeet bundle path.' }
    $file = Join-Path $DictateBundle $property.Name
    if ((Get-Item -LiteralPath $file).Length -ne $property.Value.bytes -or
        (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ne $property.Value.sha256) {
        throw "Parakeet bundle checksum mismatch for $($property.Name)."
    }
}
if ((Get-FileHash -LiteralPath $cpuEncoder -Algorithm SHA256).Hash -ne
    '3e0581fda6ab843888b51e56d7ee78b6d5bc3237ec113af1f732d1d5286aa155') {
    throw 'The Parakeet CPU encoder does not match the published checksum.'
}
$programFiles = foreach ($tier in @(64, 256, 1024, 4096)) {
    foreach ($variant in @("decode-$tier-head", "decode-$tier-body", "prefill-$tier")) {
        foreach ($extension in @('xclbin', 'bin')) {
            $file = "$variant.$extension"
            if ((Get-Item -LiteralPath (Join-Path $programs $file)).Length -le 0) { throw "Missing NPU artifact $file." }
            $file
        }
    }
}

Write-Host 'Publishing the self-contained Windows application'
$csproj = Join-Path $project 'apps\npu_chat_winui\NpuChat.csproj'
$userPackages = Join-Path $env:USERPROFILE '.nuget\packages'
$restore = @('restore', $csproj, '-r', 'win-x64', '-p:PhoenixPortable=true', '-p:SelfContained=true',
    '-p:NuGetAudit=false', '--source', (Join-Path $cache 'nuget'), '--source', $userPackages)
& dotnet @restore
if ($LASTEXITCODE -ne 0) { throw 'Offline restore failed. The Windows x64 .NET 10 runtime packs must be cached.' }
$publish = @('publish', $csproj, '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true',
    '-p:PhoenixPortable=true', '--no-restore', '-o', $release)
& dotnet @publish
if ($LASTEXITCODE -ne 0) { throw 'Phoenix publish failed.' }
$compiled = Join-Path $project 'apps\npu_chat_winui\bin\Release\net10.0-windows10.0.19041.0\win-x64'
# The WinUI publish target can omit the app's compiled XAML resources.
Copy-Files $compiled $release @('PhoenixAI.pri', 'App.xbf', 'MainWindow.xbf', 'VoiceOrb.xbf')
foreach ($file in @('PhoenixAI.exe', 'PhoenixAI.pri', 'MainWindow.xbf', 'coreclr.dll', 'hostfxr.dll', 'Microsoft.UI.Xaml.dll')) {
    if (!(Test-Path -LiteralPath (Join-Path $release $file))) { throw "Incomplete app runtime. Missing $file." }
}

Write-Host 'Staging the built-in chat and speech engines'
$runtime = Join-Path $release 'runtime'
$python = Join-Path $runtime 'python'
$voice = Join-Path $runtime 'voice-python'
$pythonHome = Join-Path $cache 'iron\python313'
Copy-Python $pythonHome $python '313'
Copy-Python $VoicePythonHome $voice '312'
foreach ($dll in Get-ChildItem -LiteralPath (Join-Path $pythonHome 'Library\bin') -File -Filter '*.dll') {
    if (!(Test-Path -LiteralPath (Join-Path $python $dll.Name))) {
        Copy-Item -LiteralPath $dll.FullName -Destination $python
    }
}
$pythonPackages = Join-Path $python 'Lib\site-packages'
$voicePackages = Join-Path $voice 'Lib\site-packages'
Copy-Packages (Join-Path $cache 'iron\mlir-aie\ironenv\Lib\site-packages') $pythonPackages @(
    'numpy*', 'ml_dtypes*', 'tokenizers*', 'huggingface_hub*', 'onnxruntime*', 'sounddevice*', '_sounddevice*',
    'cffi*', '_cffi_backend*', 'flatbuffers*', 'google', 'protobuf-*', 'packaging*', 'pycparser*',
    'filelock*', 'fsspec*', 'httpx*', 'httpcore*', 'h11*', 'anyio*', 'certifi*', 'idna*', 'tqdm*',
    'typing_extensions*', 'yaml', 'pyyaml*', '_yaml'
)
Copy-Files (Join-Path $cache 'iron\xrt_sdk\xrt\python') $pythonPackages @('pyxrt.pyd')
Copy-Packages (Join-Path $cache 'gemma-iron-env\Lib\site-packages') $pythonPackages @('jinja2*', 'markupsafe*')
Copy-Packages (Join-Path $cache 'voice\.venv\Lib\site-packages') $voicePackages @(
    'attr*', 'cloudpickle*', 'dlinfo*', 'espeakng_loader*', 'flatbuffers*', 'google', 'joblib*',
    'kokoro_onnx*', 'numpy*', 'onnxruntime*', 'packaging*', 'pedalboard*', 'phonemizer*',
    'protobuf-*', 'typing_extensions*'
)

$bundled = Join-Path $release 'cache'
Copy-Files (Join-Path $cache 'lfm25-230m') (Join-Path $bundled 'lfm25-230m') @(
    'LFM2.5-230M-Q4_K_M.gguf', 'model.safetensors', 'tokenizer.json', 'chat_template.jinja', 'LICENSE'
)
foreach ($model in @('lfm25-350m', 'lfm3-1.7b', 'qwen3.5-2b', 'gemma4-e2b')) {
    Copy-Files (Join-Path $cache $model) (Join-Path $bundled $model) @('chat_template.jinja')
}
Copy-Files $programs (Join-Path $bundled 'phoenix-portable-programs') $programFiles
$parakeet = Join-Path $bundled 'parakeet'
Copy-Files $DictateBundle $parakeet (@('manifest.json') + @($manifest.files.PSObject.Properties.Name))
Copy-Item -LiteralPath $cpuEncoder -Destination $parakeet
Copy-Files (Join-Path $cache 'voice') (Join-Path $bundled 'voice') @('kokoro-v1.0.onnx', 'voices-v1.0.bin')
$llama = Join-Path $cache 'llama-vulkan'
$llamaFiles = @('llama-server.exe', 'llama-bench.exe', 'llama-fit-params.exe', 'LICENSE-LLVM-OpenMP') +
    @(Get-ChildItem -LiteralPath $llama -File -Filter '*.dll' | Select-Object -ExpandProperty Name)
$llamaRuntime = Join-Path $bundled 'llama-vulkan'
Copy-Files $llama $llamaRuntime $llamaFiles

$vswhere = Join-Path ([Environment]::GetEnvironmentVariable('ProgramFiles(x86)')) 'Microsoft Visual Studio\Installer\vswhere.exe'
$vs = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
$crt = Get-ChildItem -LiteralPath (Join-Path $vs 'VC\Redist\MSVC') -Directory |
    Where-Object Name -Match '^\d+(\.\d+){1,3}$' |
    Sort-Object { [version]$_.Name } -Descending |
    ForEach-Object { Join-Path $_.FullName 'x64\Microsoft.VC143.CRT' } |
    Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (!$crt) { throw 'The Visual C++ x64 redistributable DLLs are missing from the build machine.' }
$crtFiles = @(Get-ChildItem -LiteralPath $crt -File -Filter '*.dll' | Select-Object -ExpandProperty Name)
foreach ($folder in @($release, $llamaRuntime, $voice)) { Copy-Files $crt $folder $crtFiles }

Copy-Files (Join-Path $project 'experiments') (Join-Path $release 'experiments') @(
    'winui_bridge.py', 'dictate_input.py', 'kokoro_voice.py', '010_lfm25_npu_chat.py', 'lfm25_x8_model.py',
    'lfm25_x8.py', 'lfm25_checkpoint.py', 'local_reasoning.py', 'local_context.py', 'gguf_context.py', 'phoenix_paths.py',
    'npu_direct.py', 'parakeet_pipeline.py',
    'llama_vulkan_chat.py', 'lfm_iron_chat.py', 'gemma4_npu_chat.py', 'phoenix_mcp_example.py', 'phoenix_mcp_chrome.py',
    'phoenix_generated_mcp.py', 'attachment_document.py', 'attachment-pypdf.zip',
    'lfm25_x8_350m_chat.py', 'lfm3_x8_chat.py', 'lfm3_x8_model.py', 'gemma4_x8_chat.py', 'gemma4_x8_model.py'
)
Copy-Files (Join-Path $project 'apps\npu_dictate') (Join-Path $release 'apps\npu_dictate') @(
    'chunker.py', 'engine.py', 'npu_runtime.py', 'win32.py'
)
$downloadables = Join-Path $release 'Downloadables'
Copy-Files $sources $downloadables @('Get-Model.cmd', 'Get-Model.ps1', 'Downloadables.md')
Copy-Files $sources $release @('Read me.md', 'Third-party notices.md')
Copy-Item -LiteralPath (Join-Path $cache 'iron\xrt_sdk\xrt\license\LICENSE') -Destination (Join-Path $release 'XRT-LICENSE.txt')
$net = Join-Path $cache 'nuget\microsoft.netcore.app.runtime.win-x64'
$netPack = Get-ChildItem -LiteralPath $net -Directory | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
foreach ($file in @('LICENSE.TXT', 'THIRD-PARTY-NOTICES.TXT')) {
    Copy-Item -LiteralPath (Join-Path $netPack.FullName $file) -Destination (Join-Path $release ".NET-$file")
}
Set-Content -LiteralPath (Join-Path $release 'phoenix-portable.release') -Value 'Phoenix AI portable win-x64' -Encoding Ascii

Get-Item -LiteralPath (Join-Path $release 'PhoenixAI.exe') | Select-Object FullName
