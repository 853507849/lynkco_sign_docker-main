<#
  ============================================================
   领克自动签到 · Windows 桌面版 —— 生成安装包 (setup.exe)
  ============================================================
  三步走:
     1) dotnet publish 自包含发布到 dist\LynkSignGUI-<rid>
     2) 凭据闸门: 确认发布产物里没有 config.json / logs / token 缓存
     3) 用 Inno Setup 的 ISCC.exe 压成一个 setup.exe

  用法:
     powershell -ExecutionPolicy Bypass -File LynkSign.WinForms\packaging\build-installer.ps1

  可选参数:
     -Version 1.2.0                覆盖版本号 (默认取 csproj 的 <Version>)
     -RuntimeIdentifier win-arm64  换架构 (默认 win-x64)
     -SkipPublish                  跳过发布, 拿现有 dist 目录重新打包

  产物:
     LynkSign.WinForms\dist\LynkSignGUI-Setup-<版本>.exe
     双击即装, 不需要管理员权限, 目标机器不需要预装 .NET。
#>
[CmdletBinding()]
param(
    [string]$Version,
    [string]$RuntimeIdentifier = 'win-x64',
    [switch]$SkipPublish
)

$ErrorActionPreference = 'Stop'

# ---------- 路径 ----------
$ScriptDir   = Split-Path -Parent $MyInvocation.MyCommand.Path
$ProjectDir  = Split-Path -Parent $ScriptDir
$ProjectFile = Join-Path $ProjectDir 'LynkSign.WinForms.csproj'
$PublishDir  = Join-Path $ProjectDir "dist\LynkSignGUI-$RuntimeIdentifier"
$DistDir     = Join-Path $ProjectDir 'dist'
$IssFile     = Join-Path $ScriptDir 'installer.iss'
$IconFile    = Join-Path $ProjectDir 'app.ico'

function Write-Step([string]$text) { Write-Host ''; Write-Host "=== $text ===" -ForegroundColor Cyan }
function Write-Ok([string]$text)   { Write-Host "  [OK] $text" -ForegroundColor Green }
function Write-Note([string]$text) { Write-Host "  [!]  $text" -ForegroundColor Yellow }
function Stop-Here([string]$text)  { Write-Host "  [X]  $text" -ForegroundColor Red; exit 1 }

# ---------- 版本号 ----------
if (-not $Version) {
    [xml]$proj = Get-Content -LiteralPath $ProjectFile -Raw -Encoding UTF8
    $Version = @($proj.Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { $_ }) | Select-Object -First 1
    if (-not $Version) { $Version = '1.0.0' }
}

Write-Host "项目:   $ProjectFile"
Write-Host "版本:   $Version"
Write-Host "架构:   $RuntimeIdentifier"

# ============================================================
#  1/3 发布
# ============================================================
if (-not $SkipPublish) {
    Write-Step '1/3 发布 (自包含, 目标机无需预装 .NET)'

    if (Test-Path -LiteralPath $PublishDir) {
        Remove-Item -LiteralPath $PublishDir -Recurse -Force
    }

    & dotnet publish $ProjectFile `
        -c Release `
        -r $RuntimeIdentifier `
        --self-contained true `
        -o $PublishDir `
        -p:Version=$Version `
        -p:DebugType=none `
        --nologo

    if ($LASTEXITCODE -ne 0) { Stop-Here 'dotnet publish 失败' }

    Write-Ok "已发布: $PublishDir"
}
else {
    Write-Step '1/3 发布 (已跳过)'
    if (-not (Test-Path -LiteralPath $PublishDir)) {
        Stop-Here "找不到发布目录 $PublishDir, 去掉 -SkipPublish 重跑"
    }
}

# ============================================================
#  2/3 凭据闸门
#  运行期数据（config.json 里是明文 refreshToken）绝不能混进安装包。
#  正常情况下数据在 %APPDATA%\LynkSign，但只要有谁把 config.json 丢进
#  项目目录，默认通配符就可能把它带进发布产物 —— 所以这里再兜一层。
# ============================================================
Write-Step '2/3 凭据闸门'

$findings = New-Object System.Collections.Generic.List[string]

foreach ($name in @('config.json', 'config.json.tmp', '.lynk_access_token.json')) {
    Get-ChildItem -LiteralPath $PublishDir -Recurse -File -Filter $name -ErrorAction SilentlyContinue |
        ForEach-Object { $findings.Add("命中文件: $($_.FullName)") }
}

Get-ChildItem -LiteralPath $PublishDir -Recurse -Directory -Filter 'logs' -ErrorAction SilentlyContinue |
    ForEach-Object { $findings.Add("命中目录: $($_.FullName)") }

foreach ($file in (Get-ChildItem -LiteralPath $PublishDir -Recurse -File -Include '*.json', '*.log', '*.txt' -ErrorAction SilentlyContinue)) {
    $text = Get-Content -LiteralPath $file.FullName -Raw -ErrorAction SilentlyContinue
    # ca_native_secret 也是凭据（原生通道的 app secret），跟 refresh_token 同等对待 ——
    # 只查 refresh_token 的话，一份带密钥的 config.json 会从这里溜过去。
    if ($text -and $text -match '"(refresh_token|ca_native_secret)"\s*:\s*"[^"]{8,}"') {
        $findings.Add("内容含明文凭据: $($file.FullName)")
    }
}

if ($findings.Count -gt 0) {
    foreach ($item in $findings) { Write-Note $item }
    Stop-Here '发布产物疑似包含运行期凭据, 已中止打包'
}

Write-Ok '干净: 未发现配置文件 / 日志 / token'

# ============================================================
#  3/3 Inno Setup
# ============================================================
Write-Step '3/3 用 Inno Setup 生成安装包'

$isccCandidates = @(
    (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
    (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe'),
    (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe')
) | Where-Object { $_ -and (Test-Path -LiteralPath $_) }

$iscc = $isccCandidates | Select-Object -First 1
if (-not $iscc) {
    Stop-Here '找不到 ISCC.exe。请先安装 Inno Setup 6: https://jrsoftware.org/isdl.php'
}
Write-Ok "编译器: $iscc"

if (-not (Test-Path -LiteralPath $DistDir)) {
    New-Item -ItemType Directory -Path $DistDir | Out-Null
}

& $iscc `
    "/DMyAppVersion=$Version" `
    "/DMySourceDir=$PublishDir" `
    "/DMyOutputDir=$DistDir" `
    "/DMyIconFile=$IconFile" `
    $IssFile

if ($LASTEXITCODE -ne 0) { Stop-Here 'ISCC 编译失败' }

# ---------- 结果 ----------
$setup = Get-ChildItem -LiteralPath $DistDir -Filter 'LynkSignGUI-Setup-*.exe' |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1

Write-Host ''
Write-Host '安装包已生成:' -ForegroundColor Green
Write-Host "  $($setup.FullName)"
Write-Host ('  大小: {0:N1} MB' -f ($setup.Length / 1MB))
Write-Host ''
Write-Host '分发: 把这个 setup.exe 发给用户, 双击即装(免管理员), 装完从开始菜单或桌面运行。' -ForegroundColor Gray
Write-Host '数据目录: %APPDATA%\LynkSign  (配置与日志, 卸载不会删除)' -ForegroundColor Gray
