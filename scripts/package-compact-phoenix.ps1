param(
    [ValidatePattern('^Phoenix-AI-[A-Za-z0-9._-]+$')][string]$Name = 'Phoenix-AI-Portable-win-x64',
    [string]$RuntimeFrom
)

$ErrorActionPreference = 'Stop'
$project = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$dist = Join-Path $project 'dist'
$cache = Join-Path $project 'cache'
$sources = Join-Path $PSScriptRoot 'portable'
$release = Join-Path $dist $Name
$installedApp = Join-Path $release 'App'
$installedModels = Join-Path $release 'Models'
$refresh = Test-Path -LiteralPath $release
$stage = Join-Path $dist ".$Name-stage"
$build = Join-Path $stage 'LauncherBuild'
if ($refresh) {
    if (!(Test-Path -LiteralPath (Join-Path $installedApp 'phoenix-compact.release')) -or
        !(Test-Path -LiteralPath $installedModels -PathType Container) -or
        (Get-Item -LiteralPath $release).Attributes.HasFlag([IO.FileAttributes]::ReparsePoint) -or
        (Get-Item -LiteralPath $installedApp).Attributes.HasFlag([IO.FileAttributes]::ReparsePoint)) {
        throw 'Use a fresh release name or select an existing compact Phoenix release.'
    }
}
if (!$RuntimeFrom) {
    $defaultApp = Join-Path $dist 'Phoenix-AI-Portable-win-x64\App'
    $RuntimeFrom = if ($refresh) { $installedApp }
        elseif (Test-Path -LiteralPath (Join-Path $defaultApp 'phoenix-compact.release')) { $defaultApp }
        else { Join-Path $dist 'Phoenix-AI-win-x64' }
}
$runtimeSource = (Resolve-Path -LiteralPath $RuntimeFrom).Path
if (!(Test-Path -LiteralPath (Join-Path $runtimeSource 'phoenix-portable.release'))) {
    throw 'Supply an existing Phoenix portable runtime with -RuntimeFrom.'
}
if (Test-Path -LiteralPath $stage) {
    throw "A previous build is staged at $stage."
}

function Assert-AppClosed {
    $innerExe = Join-Path $installedApp 'PhoenixAI.exe'
    if (Get-Process -Name PhoenixAI -ErrorAction SilentlyContinue | Where-Object Path -EQ $innerExe) {
        throw 'Close this copy of Phoenix AI before updating its App folder.'
    }
}

function Remove-StagingTree([string]$Path) {
    $resolved = (Resolve-Path -LiteralPath $Path).Path
    if ($resolved -ne $Path -or ($resolved -ne $stage -and $resolved -ne $build) -or
        !$resolved.StartsWith($dist + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
        (Get-Item -LiteralPath $resolved).Attributes.HasFlag([IO.FileAttributes]::ReparsePoint)) {
        throw 'Unexpected staging directory.'
    }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}

function Copy-Asset([string]$From, [string]$To) {
    $item = Get-Item -LiteralPath $From
    $parent = Split-Path -Parent $To
    if (!(Test-Path -LiteralPath $parent)) { [void](New-Item -ItemType Directory -Path $parent -Force) }
    if ($item.Length -ge 16MB) {
        [void](New-Item -ItemType HardLink -Path $To -Target $item.FullName)
    } else {
        Copy-Item -LiteralPath $item.FullName -Destination $To -Force
    }
}

function Copy-Assets([string]$From, [string]$To) {
    $base = (Resolve-Path -LiteralPath $From).Path
    foreach ($item in Get-ChildItem -LiteralPath $base -Recurse -File) {
        if ($item.Extension -in '.pdb', '.pyc') { continue }
        $relative = $item.FullName.Substring($base.Length + 1)
        Copy-Asset $item.FullName (Join-Path $To $relative)
    }
}

function Build-Launcher {
    $vswhere = Join-Path ([Environment]::GetEnvironmentVariable('ProgramFiles(x86)')) 'Microsoft Visual Studio\Installer\vswhere.exe'
    $vs = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
    if (!$vs) { throw 'The Windows x64 Visual C++ build tools are required to build the launcher.' }
    $tools = Join-Path $vs 'Common7\Tools\VsDevCmd.bat'
    [void](New-Item -ItemType Directory -Path $build -Force)
    Copy-Item -LiteralPath (Join-Path $sources 'PhoenixLauncher.cpp') -Destination $build
    Copy-Item -LiteralPath (Join-Path $project 'apps\npu_chat_winui\Assets\Phoenix.ico') -Destination $build
    Set-Content -LiteralPath (Join-Path $build 'PhoenixLauncher.rc') -Value '101 ICON "Phoenix.ico"' -Encoding Ascii
    @(
        '@echo off'
        ('call "{0}" -no_logo -arch=x64 -host_arch=x64' -f $tools)
        'if errorlevel 1 exit /b 1'
        'rc.exe /nologo PhoenixLauncher.rc'
        'if errorlevel 1 exit /b 1'
        'cl.exe /nologo /W4 /O2 /MT /EHsc /std:c++17 PhoenixLauncher.cpp PhoenixLauncher.res user32.lib /link /SUBSYSTEM:WINDOWS /OUT:PhoenixLauncher.exe'
    ) | Set-Content -LiteralPath (Join-Path $build 'build.cmd') -Encoding Ascii
    Push-Location -LiteralPath $build
    try {
        & $env:ComSpec /d /c build.cmd
        if ($LASTEXITCODE -ne 0) { throw 'Phoenix launcher build failed.' }
    } finally { Pop-Location }
    Copy-Item -LiteralPath (Join-Path $build 'PhoenixLauncher.exe') -Destination (Join-Path $stage 'Phoenix AI.exe')
    Remove-StagingTree $build
}

if ($refresh) { Assert-AppClosed }
$app = Join-Path $stage 'App'
$models = Join-Path $stage 'Models'
$appCache = Join-Path $app 'cache'
$sourceCache = Join-Path $runtimeSource 'cache'
$modelSource = if (Test-Path -LiteralPath (Join-Path $runtimeSource 'phoenix-compact.release')) {
    Join-Path (Split-Path -Parent $runtimeSource) 'Models'
} else { $sourceCache }
$optional = @('lfm25-350m', 'lfm3-1.7b', 'qwen3.5-2b', 'gemma4-e2b')

Write-Host 'Publishing Phoenix and building the launcher'
$csproj = Join-Path $project 'apps\npu_chat_winui\NpuChat.csproj'
$restore = @('restore', $csproj, '-r', 'win-x64', '-p:PhoenixPortable=true', '-p:SelfContained=true',
    '-p:NuGetAudit=false', '--source', (Join-Path $cache 'nuget'), '--source', (Join-Path $env:USERPROFILE '.nuget\packages'))
& dotnet @restore
if ($LASTEXITCODE -ne 0) { throw 'Offline restore failed.' }
& dotnet publish $csproj -c Release -r win-x64 --self-contained true -p:PhoenixPortable=true --no-restore -o $app
if ($LASTEXITCODE -ne 0) { throw 'Phoenix publish failed.' }
$compiled = Join-Path $project 'apps\npu_chat_winui\bin\Release\net10.0-windows10.0.19041.0\win-x64'
foreach ($component in @('PhoenixAI.pri', 'App.xbf', 'MainWindow.xbf', 'VoiceOrb.xbf')) {
    Copy-Asset (Join-Path $compiled $component) (Join-Path $app $component)
}
Get-ChildItem -LiteralPath $runtimeSource -File | Where-Object Name -Match '^(msvcp140|vcruntime140|concrt140|vccorlib140).*\.dll$' |
    Copy-Item -Destination $app -Force
Build-Launcher

Write-Host 'Staging the app runtime'
Copy-Assets (Join-Path $runtimeSource 'runtime') (Join-Path $app 'runtime')
Copy-Assets (Join-Path $runtimeSource 'apps') (Join-Path $app 'apps')
foreach ($file in Get-ChildItem -LiteralPath (Join-Path $runtimeSource 'experiments') -File -Filter '*.py') {
    Copy-Asset (Join-Path $project "experiments\$($file.Name)") (Join-Path $app "experiments\$($file.Name)")
}
Copy-Asset (Join-Path $project 'experiments\phoenix_paths.py') (Join-Path $app 'experiments\phoenix_paths.py')
Copy-Asset (Join-Path $project 'experiments\phoenix_mcp_chrome.py') (Join-Path $app 'experiments\phoenix_mcp_chrome.py')
Copy-Asset (Join-Path $project 'experiments\phoenix_generated_mcp.py') (Join-Path $app 'experiments\phoenix_generated_mcp.py')
foreach ($attachmentComponent in @('attachment_document.py', 'attachment-pypdf.zip')) {
    Copy-Asset (Join-Path $project "experiments\$attachmentComponent") (Join-Path $app "experiments\$attachmentComponent")
}
foreach ($component in @('llama-vulkan', 'phoenix-portable-programs')) {
    Copy-Assets (Join-Path $sourceCache $component) (Join-Path $appCache $component)
}
foreach ($component in @('XRT-LICENSE.txt', '.NET-LICENSE.TXT', '.NET-THIRD-PARTY-NOTICES.TXT', 'phoenix-portable.release')) {
    Copy-Asset (Join-Path $runtimeSource $component) (Join-Path $app $component)
}
Copy-Asset (Join-Path $sources 'Third-party notices.md') (Join-Path $app 'Third-party notices.md')
Set-Content -LiteralPath (Join-Path $app 'phoenix-compact.release') -Value 'Phoenix AI compact layout' -Encoding Ascii

if ($refresh) {
    Assert-AppClosed
    Write-Host 'Updating the App folder and launcher'
    $backup = Join-Path $stage 'PreviousApp'
    [IO.Directory]::Move($installedApp, $backup)
    try {
        [IO.Directory]::Move($app, $installedApp)
        [IO.File]::Move((Join-Path $stage 'Phoenix AI.exe'), (Join-Path $release 'Phoenix AI.exe'), $true)
    } catch {
        if (Test-Path -LiteralPath $installedApp) { [IO.Directory]::Move($installedApp, $app) }
        [IO.Directory]::Move($backup, $installedApp)
        throw
    }
    Remove-StagingTree $stage
} else {
    Write-Host 'Adding bundled and already installed models'
    foreach ($component in @('lfm25-230m', 'parakeet', 'voice')) {
        Copy-Assets (Join-Path $modelSource $component) (Join-Path $models $component)
    }
    foreach ($component in (@('d1-omni-600m') + $optional)) {
        $folder = Join-Path $modelSource $component
        if (Test-Path -LiteralPath $folder) { Copy-Assets $folder (Join-Path $models $component) }
    }
    $router = Join-Path $models 'd1-omni-600m'
    if ((Test-Path -LiteralPath $router) -and !(Test-Path -LiteralPath (Join-Path $router 'LICENSE'))) {
        Copy-Asset (Join-Path $modelSource 'lfm25-230m\LICENSE') (Join-Path $router 'LICENSE')
    }
    foreach ($component in @('Get-Model.cmd', 'Get-Model.ps1', 'Downloadables.md', 'Read me.md')) {
        Copy-Asset (Join-Path $sources $component) (Join-Path $models $component)
    }
    [IO.Directory]::Move($stage, $release)
}
Get-Item -LiteralPath (Join-Path $release 'Phoenix AI.exe') | Select-Object FullName, Length
