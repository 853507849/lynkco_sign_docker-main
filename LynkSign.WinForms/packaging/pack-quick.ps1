<#
  ============================================================
   领克自动签到 · 快速打包 (日常迭代用)
  ============================================================

  一句话:
      powershell -ExecutionPolicy Bypass -File LynkSign.WinForms\packaging\pack-quick.ps1
  或直接双击同目录的 pack-quick.bat

  和 build-installer.ps1 (正式发版用) 的区别:
      build-installer.ps1   先清空发布目录再全量发布 —— 慢一点 (约 30s)，但产物绝对干净，
                            适合对外发版、或发布目录里攒了一堆旧文件的时候。
      pack-quick.ps1        能跳就跳 + 覆盖式发布 + 自动处理占用 —— 日常改一版打一个 (约 10~30s)。

  它按顺序做四件事:
      1) 看源码/版本号有没有变，没变就跳过 dotnet publish (省掉大头)
      2) 看发布目录里的 LynkSignGUI.exe 是不是被正在运行的程序占着
         (占着的话无论怎么打包都必然失败 —— 这是最容易踩的一个坑)
      3) 凭据闸门: 确认发布产物里没混进 config.json / logs / 明文密钥
      4) Inno Setup 压成一个 setup.exe

  参数:
      -Version 1.1.0           覆盖版本号 (默认取 csproj 的 <Version>)
      -RuntimeIdentifier win-arm64
      -OutDir <目录>            发布目录与安装包的落盘根目录 (默认 <项目>\dist)
                               想试跑一次又不碰现有 dist 时用得上
      -Force                   忽略增量判断，强制重新发布
      -Kill                    自动结束占着发布目录的 LynkSignGUI 进程
                               不加则只提示并退出 (不会偷偷关掉你正在用的程序)

  产物: <OutDir>\LynkSignGUI-Setup-<版本>.exe
#>
[CmdletBinding()]
param(
    [string]$Version,
    [string]$RuntimeIdentifier = 'win-x64',
    [string]$OutDir,
    [switch]$Force,
    [switch]$Kill
)

$ErrorActionPreference = 'Stop'

# ---------- 路径 ----------
$ScriptDir   = Split-Path -Parent $MyInvocation.MyCommand.Path
$ProjectDir  = Split-Path -Parent $ScriptDir
$ProjectFile = Join-Path $ProjectDir 'LynkSign.WinForms.csproj'
$RootDir     = if ([string]::IsNullOrWhiteSpace($OutDir)) { Join-Path $ProjectDir 'dist' } else { $OutDir }
$PublishDir  = Join-Path $RootDir "LynkSignGUI-$RuntimeIdentifier"
$IssFile     = Join-Path $ScriptDir 'installer.iss'
$IconFile    = Join-Path $ProjectDir 'app.ico'
$ExeName     = 'LynkSignGUI.exe'

$t0 = Get-Date

function Write-Step([string]$text) { Write-Host ''; Write-Host "=== $text ===" -ForegroundColor Cyan }
function Write-Ok([string]$text)   { Write-Host "  [OK] $text" -ForegroundColor Green }
function Write-Note([string]$text) { Write-Host "  [!]  $text" -ForegroundColor Yellow }
function Write-Die([string]$text)  { Write-Host "  [X]  $text" -ForegroundColor Red; exit 1 }

# ---------- 版本号 ----------
if (-not $Version) {
    [xml]$proj = Get-Content -LiteralPath $ProjectFile -Raw -Encoding UTF8
    $Version = @($proj.Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { $_ }) | Select-Object -First 1
    if (-not $Version) { $Version = '1.0.0' }
}

Write-Host "项目:   $ProjectFile"
Write-Host "版本:   $Version"
Write-Host "架构:   $RuntimeIdentifier"
Write-Host "输出:   $RootDir"

# ============================================================
#  1/4 占用检查
#  publish 要写 <发布目录>\LynkSignGUI.exe。只要用户是从这个目录双击启动的程序，
#  文件就被锁着 —— 清目录会失败、覆盖写也会失败。所以先精确找出占用者。
# ============================================================
Write-Step '1/4 检查发布目录是否被占用'

$holders = @(Get-Process -Name 'LynkSignGUI' -ErrorAction SilentlyContinue | Where-Object {
    $path = $null
    try { $path = $_.Path } catch { }
    $path -and $path.StartsWith($PublishDir, [System.StringComparison]::OrdinalIgnoreCase)
})

if ($holders.Count -gt 0) {
    foreach ($holder in $holders) { Write-Note "正在占用: PID $($holder.Id)  $($holder.Path)" }
    if ($Kill) {
        $holders | Stop-Process -Force
        Start-Sleep -Milliseconds 800
        Write-Ok "已结束 $($holders.Count) 个进程"
    }
    else {
        Write-Note '这个目录被正在运行的程序占着，打包必然失败。'
        Write-Note '退出程序后重跑，或者加 -Kill 让脚本自动关掉它。'
        exit 1
    }
}
else {
    Write-Ok '没有被占用'
}

# ============================================================
#  2/4 发布（增量：源码与版本号都没变就跳过）
#  这里刻意用「覆盖式发布」而不是「先删目录」：省一次 237 文件的删除，
#  日常迭代够用。要绝对干净的产物请走 build-installer.ps1。
# ============================================================
Write-Step '2/4 发布 (自包含, 目标机无需预装 .NET)'

$exePath     = Join-Path $PublishDir $ExeName
$needPublish = $true
$reason      = '发布目录里还没有产物'

if ((-not $Force) -and (Test-Path -LiteralPath $exePath)) {
    $exeTime = (Get-Item -LiteralPath $exePath).LastWriteTime
    $exeVer  = (Get-Item -LiteralPath $exePath).VersionInfo.FileVersion

    # 只认真正会被编译进去的文件类型。
    # 注意别用 -Include: 它跟着 -LiteralPath 时不可靠 —— 实测会把 .bat 这类无关文件也算进来，
    # 结果改个文档也白发布一次 (11 秒)。
    $newest = Get-ChildItem -LiteralPath $ProjectDir -Recurse -File -ErrorAction SilentlyContinue |
        Where-Object {
            ($_.FullName -notmatch '\\(bin|obj|dist)\\') -and
            ($_.Extension -in @('.cs', '.csproj', '.resx', '.ico'))
        } |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1

    if ($newest -and $newest.LastWriteTime -gt $exeTime) {
        $reason = "源码有更新: $($newest.Name)  $($newest.LastWriteTime.ToString('MM-dd HH:mm'))"
    }
    elseif ($exeVer -and $exeVer -notlike "$Version*") {
        $reason = "版本号不符: 产物 $exeVer, 目标 $Version"
    }
    else {
        $needPublish = $false
    }
}

if ($needPublish) {
    Write-Note "重新发布 ($reason)"
    if (-not (Test-Path -LiteralPath $PublishDir)) {
        New-Item -ItemType Directory -Path $PublishDir -Force | Out-Null
    }

    & dotnet publish $ProjectFile `
        -c Release `
        -r $RuntimeIdentifier `
        --self-contained true `
        -o $PublishDir `
        -p:Version=$Version `
        -p:DebugType=none `
        --nologo

    if ($LASTEXITCODE -ne 0) { Write-Die 'dotnet publish 失败' }
    Write-Ok "已发布: $PublishDir"
}
else {
    Write-Ok "源码与版本号都没变，跳过发布 (强制重发布加 -Force)"
}

# ============================================================
#  3/4 凭据闸门（与 build-installer.ps1 第 2 步等价，两边改动要同步）
#  config.json 里是明文 refreshToken / app secret，绝不能混进安装包。
# ============================================================
Write-Step '3/4 凭据闸门'

$findings = New-Object System.Collections.Generic.List[string]

foreach ($name in @('config.json', 'config.json.tmp', '.lynk_access_token.json')) {
    Get-ChildItem -LiteralPath $PublishDir -Recurse -File -Filter $name -ErrorAction SilentlyContinue |
        ForEach-Object { $findings.Add("命中文件: $($_.FullName)") }
}

Get-ChildItem -LiteralPath $PublishDir -Recurse -Directory -Filter 'logs' -ErrorAction SilentlyContinue |
    ForEach-Object { $findings.Add("命中目录: $($_.FullName)") }

# 同上, 这里也避开 -Include, 免得去读那一堆 100MB 的运行时 dll 做正则匹配。
$gateFiles = @(Get-ChildItem -LiteralPath $PublishDir -Recurse -File -ErrorAction SilentlyContinue |
    Where-Object { $_.Extension -in @('.json', '.log', '.txt') })

foreach ($file in $gateFiles) {
    $text = Get-Content -LiteralPath $file.FullName -Raw -ErrorAction SilentlyContinue
    if ($text -and $text -match '"(refresh_token|ca_native_secret)"\s*:\s*"[^"]{8,}"') {
        $findings.Add("内容含明文凭据: $($file.FullName)")
    }
}

if ($findings.Count -gt 0) {
    foreach ($item in $findings) { Write-Note $item }
    Write-Die '发布产物疑似包含运行期凭据，已中止打包'
}

Write-Ok '干净: 未发现配置文件 / 日志 / token'

# ============================================================
#  4/4 Inno Setup
# ============================================================
Write-Step '4/4 用 Inno Setup 生成安装包'

$isccCandidates = @(
    (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
    (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe'),
    (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe')
) | Where-Object { $_ -and (Test-Path -LiteralPath $_) }

$iscc = $isccCandidates | Select-Object -First 1
if (-not $iscc) {
    Write-Die '找不到 ISCC.exe。请先安装 Inno Setup 6: https://jrsoftware.org/isdl.php'
}
Write-Ok "编译器: $iscc"

if (-not (Test-Path -LiteralPath $RootDir)) {
    New-Item -ItemType Directory -Path $RootDir -Force | Out-Null
}

& $iscc `
    "/DMyAppVersion=$Version" `
    "/DMySourceDir=$PublishDir" `
    "/DMyOutputDir=$RootDir" `
    "/DMyIconFile=$IconFile" `
    $IssFile

if ($LASTEXITCODE -ne 0) { Write-Die 'ISCC 编译失败' }

# ---------- 结果 ----------
$setup = Get-ChildItem -LiteralPath $RootDir -Filter 'LynkSignGUI-Setup-*.exe' -File |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1

$hash    = (Get-FileHash -LiteralPath $setup.FullName -Algorithm SHA256).Hash
$seconds = [int]((Get-Date) - $t0).TotalSeconds

Write-Host ''
Write-Host "打包完成 (用时 $seconds 秒)" -ForegroundColor Green
Write-Host "  版本:    $Version  /  $RuntimeIdentifier"
Write-Host "  安装包:  $($setup.FullName)"
Write-Host "  大小:    $([math]::Round($setup.Length / 1MB, 1)) MB"
Write-Host "  SHA256:  $hash"
Write-Host ''
Write-Host '分发: 把这个 setup.exe 发出去, 双击即装(免管理员), 装完从开始菜单或桌面运行。' -ForegroundColor Gray
Write-Host '数据目录: %APPDATA%\LynkSign  (配置与日志, 卸载不会删除)' -ForegroundColor Gray
