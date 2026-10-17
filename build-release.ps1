param([string]$OutputDirectory)
$ErrorActionPreference='Stop'
$relayRoot=$PSScriptRoot
& (Join-Path $relayRoot 'build-dev.ps1')
if($LASTEXITCODE -ne 0){throw '应用编译失败。'}
$relayVersionMatch=[regex]::Match([IO.File]::ReadAllText((Join-Path $relayRoot 'src/AppVersion.cs')),'const string Number="(\d+\.\d+\.\d+)"')
if(-not $relayVersionMatch.Success){throw '无法读取应用版本号。'}
$relayVersion=$relayVersionMatch.Groups[1].Value
if(-not $OutputDirectory){$OutputDirectory=Join-Path $relayRoot ('release/Codex Relay Lite v'+$relayVersion)}
$relayRelease=[IO.Path]::GetFullPath($OutputDirectory)
if(Test-Path -LiteralPath $relayRelease){throw ('输出目录已存在，请指定新目录：'+$relayRelease)}
$relayBuild=Join-Path $relayRoot 'build'
$relayFiles=@('SessionBackend.exe','Codex Relay Lite.exe','Microsoft.Web.WebView2.Core.dll','Microsoft.Web.WebView2.WinForms.dll','WebView2Loader.dll','Nett.dll')
foreach($relayFile in $relayFiles){if(-not (Test-Path -LiteralPath (Join-Path $relayBuild $relayFile))){throw ('缺少运行文件：'+$relayFile)}}
New-Item -ItemType Directory -Path $relayRelease -Force | Out-Null
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
关闭窗口会收起到托盘；完全退出请右键托盘图标选择“退出”。

“会话管理”位于左侧“设置”下方，详细用法见“会话管理说明.md”。
导入或永久删除前需要关闭其他 Codex 客户端。

版本号见 VERSION.txt，更新内容见“版本记录.md”。
"@
[IO.File]::WriteAllText((Join-Path $relayRelease '使用说明.txt'),$relayGuide,$relayEncoding)
$relayManifest=[ordered]@{name='Codex Relay Lite';version=$relayVersion;fileVersion=($relayVersion+'.0');platform='windows-x64';builtAtUtc=[DateTime]::UtcNow.ToString('o')}
[IO.File]::WriteAllText((Join-Path $relayRelease 'build-info.json'),($relayManifest | ConvertTo-Json),$relayEncoding)
Write-Output ('Release ready: '+$relayRelease)
