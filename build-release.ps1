param([string]$OutputDirectory,[string]$BuildDirectory)
$ErrorActionPreference='Stop'
$relayRoot=$PSScriptRoot
function Assert-RelayReleaseDirectory([string]$Path,[string]$Marker) {
    $full=[IO.Path]::GetFullPath($Path).TrimEnd('\','/')
    if($full -eq [IO.Path]::GetPathRoot($full).TrimEnd('\','/') -or $full -eq $relayRoot -or $relayRoot.StartsWith($full+'\',[StringComparison]::OrdinalIgnoreCase)){throw '发布目标不能是磁盘根目录、项目目录或其上级目录。'}
    foreach($protected in @($relayBuild,(Join-Path $relayRoot 'src'),(Join-Path $relayRoot 'ui-src'),(Join-Path $relayRoot 'session-backend'),(Join-Path $relayRoot 'tests'),(Join-Path $relayRoot 'updater'),(Join-Path $relayRoot '.git'),(Join-Path $relayRoot 'build'))){
        if($full -eq $protected -or $full.StartsWith($protected.TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase) -or $protected.StartsWith($full+'\',[StringComparison]::OrdinalIgnoreCase)){throw '发布目标不能覆盖源码或构建目录。'}
    }
    for($ancestor=$full;$ancestor;$ancestor=[IO.Path]::GetDirectoryName($ancestor)){
        if((Test-Path -LiteralPath $ancestor) -and ((Get-Item -LiteralPath $ancestor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)){throw '发布目标不能包含目录链接。'}
    }
    if(Test-Path -LiteralPath $full){
        if(-not (Test-Path -LiteralPath $full -PathType Container)){throw '发布目标必须是独立目录。'}
        if(@(Get-ChildItem -LiteralPath $full -Force).Count -gt 0 -and -not [IO.File]::Exists((Join-Path $full $Marker))){throw ('目标目录不是已生成的发布包，未覆盖：'+$full)}
        $running=Get-Process -Name 'Codex Relay Lite','SessionBackend','Updater' -ErrorAction SilentlyContinue | Where-Object {$_.Path -and $_.Path.StartsWith($full+'\',[StringComparison]::OrdinalIgnoreCase)}
        if($running){throw ('请先退出该目录中正在运行的程序，再复用发布目录：'+$full)}
    }
}
function Reset-RelayReleaseDirectory([string]$Path,[string]$Marker) {
    Assert-RelayReleaseDirectory -Path $Path -Marker $Marker
    if(Test-Path -LiteralPath $Path){
        Add-Type -AssemblyName Microsoft.VisualBasic
        [Microsoft.VisualBasic.FileIO.FileSystem]::DeleteDirectory($Path,[Microsoft.VisualBasic.FileIO.UIOption]::OnlyErrorDialogs,[Microsoft.VisualBasic.FileIO.RecycleOption]::SendToRecycleBin,[Microsoft.VisualBasic.FileIO.UICancelOption]::ThrowException)
    }
    New-Item -ItemType Directory -Path $Path -Force | Out-Null
}
$relayVersionMatch=[regex]::Match([IO.File]::ReadAllText((Join-Path $relayRoot 'src/AppVersion.cs')),'const string Number="(\d+\.\d+\.\d+)"')
if(-not $relayVersionMatch.Success){throw '无法读取应用版本号。'}
$relayVersion=$relayVersionMatch.Groups[1].Value
$relayDefaultOutput=-not $OutputDirectory
if($relayDefaultOutput){$OutputDirectory=Join-Path $relayRoot 'release/current'}
$relayRelease=[IO.Path]::GetFullPath($OutputDirectory)
$relayAssets=if($relayDefaultOutput){Join-Path $relayRoot 'release/assets'}else{$relayRelease+'-assets'}
$relayBuild=if($BuildDirectory){[IO.Path]::GetFullPath($BuildDirectory)}else{Join-Path $relayRoot 'build/current'}
Assert-RelayReleaseDirectory -Path $relayRelease -Marker 'package-files.json'
Assert-RelayReleaseDirectory -Path $relayAssets -Marker 'update.json'
& (Join-Path $relayRoot 'build-dev.ps1') -OutputDirectory $relayBuild
$relayFiles=@('SessionBackend.exe','Codex Relay Lite.exe','Updater.exe','Microsoft.Web.WebView2.Core.dll','Microsoft.Web.WebView2.WinForms.dll','WebView2Loader.dll','Nett.dll')
foreach($relayFile in $relayFiles){if(-not (Test-Path -LiteralPath (Join-Path $relayBuild $relayFile))){throw ('缺少运行文件：'+$relayFile)}}
Reset-RelayReleaseDirectory -Path $relayRelease -Marker 'package-files.json'
Reset-RelayReleaseDirectory -Path $relayAssets -Marker 'update.json'
foreach($relayFile in $relayFiles){Copy-Item -LiteralPath (Join-Path $relayBuild $relayFile) -Destination $relayRelease}
Copy-Item -LiteralPath (Join-Path $relayBuild 'ui') -Destination $relayRelease -Recurse
Copy-Item -LiteralPath (Join-Path $relayRoot 'THIRD-PARTY-NOTICES.txt') -Destination $relayRelease
Copy-Item -LiteralPath (Join-Path $relayRoot 'session-backend/licenses') -Destination (Join-Path $relayRelease 'session-licenses') -Recurse
Copy-Item -LiteralPath (Join-Path $relayRoot 'SESSION-GUIDE.md') -Destination (Join-Path $relayRelease '会话管理说明.md')
Copy-Item -LiteralPath (Join-Path $relayRoot 'CHANGELOG.md') -Destination (Join-Path $relayRelease '版本记录.md')
$relayEncoding=[Text.UTF8Encoding]::new($false)
[IO.File]::WriteAllText((Join-Path $relayRelease 'VERSION.txt'),($relayVersion+"`r`n"),$relayEncoding)
$relayGuide=@"
Codex Relay Lite v$relayVersion

双击“Codex Relay Lite.exe”启动，也可使用桌面快捷方式。
请保留同目录的 SessionBackend.exe、ui 文件夹及 DLL 文件，移动应用时请移动整个文件夹。

支持 Windows x64，使用 .NET Framework 4.8 和 Microsoft Edge WebView2 Runtime。
中转与加密密钥沿用本机 Codex Relay Lite 数据目录。
本应用仅支持 Codex Responses 直连，无需本地模型桥接，退出本应用不影响 Codex 调用。
添加中转请点击左上角品牌栏右侧的“＋”图标。
默认模型须明确选择；默认推理强度可选，不指定时跟随 Codex，指定后仍可在 Codex 内切换支持的档位。
6.1 Sol 可在 Relay 的模型下拉框选择“6.1 Sol · 500k”预设；普通项保留原预算，仅选用预设时使用 500k，请确认中转支持后选用。保存并应用后需重启 Codex。
启动时安全升级本应用的旧模型目录并保存加密备份；上下文预算不代表上游容量，用量以真实 Token 通知为准。
关闭窗口会收起到托盘，后台继续刷新当前中转的用量统计；重开保留原页面、筛选和未保存草稿，不重新加载。
自动记住窗口大小、位置和最大化状态，完全退出后再次启动也会恢复；最小化或静默启动不会覆盖正常窗口尺寸。
完全退出请右键托盘图标选择“退出”。
“设置 → 软件更新”可检查、下载并重启安装新版，默认在启动时及每 6 小时检查。

“会话管理”位于左侧“设置”下方，详细用法见“会话管理说明.md”。
导入或永久删除前需要关闭其他 Codex 客户端。

版本号见 VERSION.txt，更新内容见“版本记录.md”。
"@
[IO.File]::WriteAllText((Join-Path $relayRelease '使用说明.txt'),$relayGuide,$relayEncoding)
$relayManifest=[ordered]@{name='Codex Relay Lite';version=$relayVersion;fileVersion=($relayVersion+'.0');platform='windows-x64';builtAtUtc=[DateTime]::UtcNow.ToString('o')}
[IO.File]::WriteAllText((Join-Path $relayRelease 'build-info.json'),($relayManifest | ConvertTo-Json),$relayEncoding)
$relayPackageFiles=@(Get-ChildItem -LiteralPath $relayRelease -Recurse -File | ForEach-Object {$_.FullName.Substring($relayRelease.TrimEnd('\').Length+1).Replace('\','/')} | Sort-Object)+@('package-files.json')
[IO.File]::WriteAllText((Join-Path $relayRelease 'package-files.json'),(@{files=$relayPackageFiles} | ConvertTo-Json -Depth 5),$relayEncoding)
$relayArchiveName='CodexRelayLite-'+$relayVersion+'-windows-x64.zip'
$relayArchive=Join-Path $relayAssets $relayArchiveName
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::CreateFromDirectory($relayRelease,$relayArchive)
$relayNotesMatch=[regex]::Match([IO.File]::ReadAllText((Join-Path $relayRoot 'CHANGELOG.md')),'(?ms)^## v'+[regex]::Escape($relayVersion)+'[^\r\n]*\r?\n(.*?)(?=^## |\z)')
$relayUpdate=@{version=$relayVersion;platform='windows-x64';url=('https://github.com/Thz-zZ/codex/releases/download/v'+$relayVersion+'/'+$relayArchiveName);size=(Get-Item -LiteralPath $relayArchive).Length;sha256=(Get-FileHash -LiteralPath $relayArchive -Algorithm SHA256).Hash.ToLowerInvariant();notes=$relayNotesMatch.Groups[1].Value.Trim()}
[IO.File]::WriteAllText((Join-Path $relayAssets 'update.json'),($relayUpdate | ConvertTo-Json),$relayEncoding)
Write-Output ('Release ready: '+$relayRelease)
Write-Output ('Upload both files to GitHub Release v'+$relayVersion+': '+$relayAssets)
