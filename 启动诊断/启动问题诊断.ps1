# ============================================================================
# ALH Pro 启动问题诊断(用户侧一键收集,只读,不改动系统)
# 用途:用户反馈「装完双击没反应 / 打不开 / 闪退」时,让用户运行本脚本,
#       把生成的 ALHPro启动诊断_*.zip 发回来,就能判断卡在哪一步。
# 用法:双击同目录的「启动问题诊断.bat」;或在本目录执行:
#       powershell -NoProfile -ExecutionPolicy Bypass -File .\启动问题诊断.ps1
# 参数:-OutDir <目录>  指定输出位置(默认桌面)
# 【只读】不删文件、不改设置、不联网、不需要管理员权限。
# ============================================================================
param(
    [string]$OutDir = [Environment]::GetFolderPath('Desktop'),
    [switch]$NoOpen          # 只收集、不自动打开所在文件夹(自测用)
)

$ErrorActionPreference = 'Continue'
$stamp  = Get-Date -Format 'yyyyMMdd_HHmmss'
$root   = Join-Path $OutDir "ALHPro启动诊断_$stamp"
New-Item -ItemType Directory -Force -Path $root | Out-Null

function Save-Report([string]$name, [scriptblock]$body) {
    $path = Join-Path $root $name
    try {
        $text = & $body 2>&1 | Out-String
        if ([string]::IsNullOrWhiteSpace($text)) { $text = "(无内容)" }
    } catch {
        $text = "收集失败: " + $_.Exception.Message
    }
    [System.IO.File]::WriteAllText($path, $text, (New-Object System.Text.UTF8Encoding($true)))
}

Write-Host "ALH Pro 启动问题诊断 —— 正在收集,请稍等(约 10~30 秒)…" -ForegroundColor Cyan

# ---- 1. 时间 / 系统 / 硬件 --------------------------------------------------
Save-Report '01_系统信息.txt' {
    "收集时间: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')"
    "--- 操作系统 ---"
    "版本: $([Environment]::OSVersion.VersionString)"
    try {
        $os = Get-CimInstance Win32_OperatingSystem
        "Caption: $($os.Caption)"
        "Version: $($os.Version) · BuildNumber: $($os.BuildNumber) · 架构: $($os.OSArchitecture)"
        "已安装时间: $($os.InstallDate) · 内存: $([math]::Round($os.TotalVisibleMemorySize/1MB,1)) GB"
    } catch { "Win32_OperatingSystem 读取失败: $($_.Exception.Message)" }
    "--- CPU ---"
    try {
        Get-CimInstance Win32_Processor | ForEach-Object { "  $($_.Name) · $($_.NumberOfCores) 核 / $($_.NumberOfLogicalProcessors) 线程" }
    } catch { "CPU 读取失败: $($_.Exception.Message)" }
    "--- 显示适配器(驱动)---"
    try {
        Get-CimInstance Win32_VideoController | ForEach-Object {
            "  $($_.Name) | 驱动版本 $($_.DriverVersion) | 驱动日期 $($_.DriverDate) | 状态 $($_.Status) | 可用 $($_.Availability) | 显存(适配器报告) $([math]::Round($_.AdapterRAM/1MB,0)) MB"
        }
    } catch { "显示适配器读取失败: $($_.Exception.Message)" }
    "--- 显卡类设备的问题代码(设备管理器隐藏问题)---"
    try {
        Get-PnpDevice -Class Display -ErrorAction Stop | ForEach-Object {
            "  $($_.Status) | ProblemCode=$($_.ProblemCode) | $($_.FriendlyName) | InstanceId=$($_.InstanceId)"
        }
    } catch { "Get-PnpDevice 失败: $($_.Exception.Message)" }
    "--- Windows App Runtime(应用商店包)---"
    try { Get-AppxPackage *WindowsAppRuntime* | ForEach-Object { "  $($_.Name) $($_.Version) [$($_.Architecture)]" } } catch { "读取失败: $($_.Exception.Message)" }
    "--- 是否是管理员运行本脚本 ---"
    try {
        $id = [System.Security.Principal.WindowsIdentity]::GetCurrent()
        "  是管理员: $((New-Object System.Security.Principal.WindowsPrincipal($id)).IsInRole([System.Security.Principal.WindowsBuiltInRole]::Administrator)) · 用户: $($id.Name)"
    } catch { "读取失败" }
}

# ---- 2. ALH Pro 进程与安装目录 ---------------------------------------------
Save-Report '02_ALHPro进程与文件.txt' {
    "--- 当前 ALHPro 进程(有残留进程会让双击「没反应」:单实例锁)---"
    $procs = @(Get-Process ALHPro -ErrorAction SilentlyContinue)
    if ($procs.Count -eq 0) { "  没有 ALHPro 进程在运行" }
    foreach ($p in $procs) {
        "  PID=$($p.Id) 启动时间=$($p.StartTime) 窗口标题='$($p.MainWindowTitle)' 内存=$([math]::Round($p.WorkingSet64/1MB,0))MB 路径=$($p.Path)"
    }
    "--- 安装记录(卸载注册表项)---"
    $regDirs = @()
    foreach ($hive in @(
            'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*',
            'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*',
            'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*')) {
        try {
            Get-ItemProperty $hive -ErrorAction SilentlyContinue |
                Where-Object { $_.DisplayName -match 'ALH\s?Pro' } | ForEach-Object {
                    "  $($_.DisplayName) · 版本 $($_.DisplayVersion) · 位置 '$($_.InstallLocation)' · 卸载命令 $($_.UninstallString)"
                    if ($_.InstallLocation) { $regDirs += $_.InstallLocation.TrimEnd('\') }
                }
        } catch { }
    }
    if ($regDirs.Count -eq 0) { "  注册表里没有 ALH Pro 的安装记录(可能没装成功,或装的是绿色版)" }
    "--- 安装目录 ---"
    $cands = @(
        (Join-Path $env:LOCALAPPDATA 'Programs\ALH Pro'),
        (Join-Path $env:ProgramFiles 'ALH Pro'),
        (Join-Path ${env:ProgramFiles(x86)} 'ALH Pro')
    ) + $regDirs
    $found = $null
    foreach ($c in $cands) {
        if ([string]::IsNullOrWhiteSpace($c)) { continue }
        if (Test-Path (Join-Path $c 'ALHPro.exe')) { $found = $c; break }
        if (Test-Path $c) { "  目录存在但没有 ALHPro.exe: $c" }
    }
    if (-not $found) { "  ✗ 常见位置与注册表记录里都没找到 ALHPro.exe" } else {
        "  安装目录: $found"
        $exe = Join-Path $found 'ALHPro.exe'
        $fi = Get-Item $exe
        "  ALHPro.exe: $($fi.Length) 字节 · 文件版本 $($fi.VersionInfo.FileVersion) · 修改时间 $($fi.LastWriteTime)"
        "--- 关键运行库文件是否随包存在(缺一个都可能导致双击没反应)---"
        foreach ($f in @('coreclr.dll','hostfxr.dll','hostpolicy.dll','Microsoft.WindowsAppRuntime.dll','Microsoft.UI.Xaml.dll','Microsoft.WindowsAppRuntime.Bootstrap.dll','Microsoft.Graphics.Display.dll','ALHPro.runtimeconfig.json','onnxruntime.dll','DirectML.dll')) {
            $p = Join-Path $found $f
            "  $(if (Test-Path $p) {'✓'} else {'✗'}) $f"
        }
        "--- 目录里的 d3dcompiler_47.dll(若有旧版会冲突,建议删除)---"
        $d3d = Join-Path $found 'd3dcompiler_47.dll'
        if (Test-Path $d3d) { "  ⚠ 存在: $((Get-Item $d3d).VersionInfo.FileVersion) $($d3d)" } else { "  不存在(正常)" }
        "--- 目录顶层文件数与体积 ---"
        try {
            $all = Get-ChildItem $found -Recurse -File -ErrorAction SilentlyContinue
            "  文件 $($all.Count) 个,共 $([math]::Round(($all | Measure-Object Length -Sum).Sum/1MB,1)) MB"
        } catch { }
    }
    "--- 快捷方式指向 ---"
    foreach ($lnk in @(
        (Join-Path ([Environment]::GetFolderPath('Desktop')) 'ALH Pro.lnk'),
        (Join-Path ([Environment]::GetFolderPath('Programs')) 'ALH Pro.lnk')
    )) {
        if (Test-Path $lnk) {
            try {
                $sh = New-Object -ComObject WScript.Shell
                $t = $sh.CreateShortcut($lnk)
                "  $lnk → $($t.TargetPath) 参数='$($t.Arguments)'"
            } catch { "  读取快捷方式失败: $lnk" }
        }
    }
}

# ---- 3. 软件自己的日志与崩溃诊断 -------------------------------------------
Save-Report '03_软件日志(尾部).txt' {
    $dir = Join-Path $env:LOCALAPPDATA 'ALHPro'
    "日志目录: $dir"
    if (-not (Test-Path $dir)) { "✗ 目录不存在 —— 说明程序可能【从未成功启动过】(连日志都没来得及写)" } else {
        "--- 目录清单 ---"
        Get-ChildItem $dir -File | Sort-Object LastWriteTime -Descending |
            Select-Object -First 30 | ForEach-Object { "  $($_.LastWriteTime) · $($_.Length) 字节 · $($_.Name)" }
        $log = Join-Path $dir 'diagnostic.log'
        if (Test-Path $log) {
            $fi = Get-Item $log
            "--- diagnostic.log 最后修改: $($fi.LastWriteTime),大小 $($fi.Length) 字节 ---"
            "--- 最近一次「应用启动」之后的全部内容(若连「应用启动」都没有 = 停在窗口创建之前)---"
            $lines = Get-Content $log -Tail 400 -Encoding UTF8
            $idx = -1
            for ($i = $lines.Count - 1; $i -ge 0; $i--) { if ($lines[$i] -match '应用启动') { $idx = $i; break } }
            if ($idx -ge 0) { $lines[$idx..($lines.Count-1)] } else { "⚠ 整个日志里没有「应用启动」记录" ; $lines }
        } else { "✗ 没有 diagnostic.log" }
        $crash = Get-ChildItem $dir -Filter '崩溃诊断_*.txt' -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending
        if ($crash) {
            $latest = $crash | Select-Object -First 1
            "--- 崩溃诊断文件 $($crash.Count) 个,最新: $($latest.Name) ---"
            Get-Content $latest.FullName -Encoding UTF8
        } else { "没有 崩溃诊断_*.txt(说明没走到「已捕获的异常」那一步)" }
    }
    "--- 单实例锁:是否有别的用户会话在跑(多用户同时登录也会互相挡住)---"
    try { Get-Process ALHPro -ErrorAction SilentlyContinue | ForEach-Object { "  PID=$($_.Id) 会话=$($_.SessionId) 用户=$($_.StartInfo.EnvironmentVariables["USERNAME"])" } } catch { }
}

# ---- 4. 系统事件日志(崩溃/卡死/被拦截)-------------------------------------
Save-Report '04_事件日志.txt' {
    $since = (Get-Date).AddDays(-7)
    "--- 应用程序日志:近 7 天 错误/警告 中与 ALHPro 或崩溃相关的(最多 60 条)---"
    try {
        Get-WinEvent -FilterHashtable @{ LogName='Application'; Level=1,2,3; StartTime=$since } -MaxEvents 400 -ErrorAction Stop |
            Where-Object { $_.Message -match 'ALHPro|\.NET Runtime|Application Error|Application Hang|Windows Error Reporting|KERNELBASE|coreclr' } |
            Select-Object -First 60 | ForEach-Object {
                "===== $($_.TimeCreated) [$($_.LevelDisplayName)] $($_.ProviderName) (EventID $($_.Id))"
                ($_.Message -split "`r?`n" | Select-Object -First 12) -join "`n"
            }
    } catch { "读取事件日志失败: $($_.Exception.Message)" }
    "--- 系统日志:近 7 天 显示/驱动相关错误(最多 30 条)---"
    try {
        Get-WinEvent -FilterHashtable @{ LogName='System'; Level=1,2; StartTime=$since } -MaxEvents 400 -ErrorAction Stop |
            Where-Object { $_.ProviderName -match 'Display|nvlddmkm|igfx|amdkmdag|dxgkrnl|Kernel-PnP|Service Control Manager' } |
            Select-Object -First 30 | ForEach-Object {
                "===== $($_.TimeCreated) [$($_.LevelDisplayName)] $($_.ProviderName) (EventID $($_.Id))"
                ($_.Message -split "`r?`n" | Select-Object -First 6) -join "`n"
            }
    } catch { "读取系统日志失败: $($_.Exception.Message)" }
    "--- 最近的程序兼容性助手/被阻止记录 ---"
    try {
        Get-WinEvent -FilterHashtable @{ LogName='Application'; ProviderName='Application Compatibility Infrastructure' } -MaxEvents 20 -ErrorAction Stop |
            ForEach-Object { "===== $($_.TimeCreated) $($_.Message)" }
    } catch { "无(或读取失败)" }
}

# ---- 5. 杀软 / SmartScreen 拦截 --------------------------------------------
Save-Report '05_安全软件与拦截.txt' {
    "--- Defender 实时保护状态 ---"
    try { Get-MpComputerStatus | Select-Object AMServiceEnabled,RealTimeProtectionEnabled,AntivirusEnabled,IsTamperProtected | Format-List | Out-String } catch { "读取失败(可能被第三方杀软接管): $($_.Exception.Message)" }
    "--- 第三方杀毒软件(WMI SecurityCenter2)---"
    try { Get-CimInstance -Namespace root\SecurityCenter2 -ClassName AntiVirusProduct | ForEach-Object { "  $($_.displayName) · 状态码 $($_.productState)" } } catch { "读取失败: $($_.Exception.Message)" }
    "--- Defender 最近的检测记录 ---"
    try { Get-MpThreatDetection -ErrorAction Stop | Sort-Object InitialDetectionTime -Descending | Select-Object -First 15 | ForEach-Object { "  $($_.InitialDetectionTime) · $($_.ThreatID) · 资源: $($_.Resources)" } } catch { "无记录(或读取失败)" }
    "--- 文件被「来自 Internet 的文件」阻止(Mark-of-the-Web)---"
    $exe = Join-Path $env:LOCALAPPDATA 'Programs\ALH Pro\ALHPro.exe'
    if (Test-Path $exe) {
        try { Get-Item $exe -Stream Zone.Identifier -ErrorAction Stop | Out-String } catch { "  无 Zone.Identifier(正常:本地安装的不会带这个标记)" }
    } else { "  未找到 $exe" }
}

# ---- 6. 打包 ----------------------------------------------------------------
$zip = Join-Path $OutDir "ALHPro启动诊断_$stamp.zip"
try {
    Compress-Archive -Path (Join-Path $root '*') -DestinationPath $zip -Force
    Remove-Item $root -Recurse -Force -ErrorAction SilentlyContinue
} catch {
    $zip = $root
}

Write-Host ""
Write-Host "收集完成 ✓" -ForegroundColor Green
Write-Host "请把这个文件发给我:" -ForegroundColor Yellow
Write-Host "  $zip" -ForegroundColor White
Write-Host ""
Write-Host "(打开所在文件夹…)"
if (-not $NoOpen) { try { Start-Process explorer.exe "/select,`"$zip`"" } catch { } }
