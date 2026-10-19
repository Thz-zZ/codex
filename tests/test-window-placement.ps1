param([string]$ToolchainRoot=$env:CODEX_RELAY_TOOLCHAIN)
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot
if(-not $ToolchainRoot){$ToolchainRoot=Join-Path (Split-Path $root) 'native-toolchain'}
$output=Join-Path $root 'build/window-placement-tests'
New-Item -ItemType Directory -Force -Path $output | Out-Null
$hostPath=Join-Path $output 'WindowPlacementTestHost.exe'
$arguments=@('/nologo','/target:exe','/platform:x64','/optimize+',('/win32manifest:'+(Join-Path $root 'lite.manifest')),('/out:'+$hostPath))
foreach($reference in @('System.dll','System.Core.dll','System.Drawing.dll','System.Windows.Forms.dll','System.Web.Extensions.dll')){$arguments+=('/reference:'+$reference)}
foreach($source in @('Json','WindowPlacement','WindowPlacementChecks')){$arguments+=(Join-Path $root ('src/'+$source+'.cs'))}
$arguments+=(Join-Path $PSScriptRoot 'window-placement-host.cs')
& (Join-Path $ToolchainRoot 'roslyn/tasks/net472/csc.exe') @arguments
if($LASTEXITCODE -ne 0){throw '窗口状态测试编译失败。'}
$fixture=Join-Path $output ('fixture-'+[guid]::NewGuid().ToString('N'))
& $hostPath $fixture 'unit'
if($LASTEXITCODE -ne 0){throw '窗口状态读取及屏幕边界测试失败。'}
foreach($mode in @('remember-normal','restore-normal','immediate-exit','restore-normal','remember-maximized','restore-maximized','minimize-maximized','restore-maximized','silent-maximized','restore-maximized','remember-normal','minimize-normal','restore-normal')){
    $stdout=Join-Path $output ($mode+'.out.json')
    $stderr=Join-Path $output ($mode+'.error.txt')
    $process=Start-Process -FilePath $hostPath -ArgumentList @('"'+$fixture+'"',$mode) -WindowStyle Hidden -RedirectStandardOutput $stdout -RedirectStandardError $stderr -PassThru
    if(-not $process.WaitForExit(15000)){$process.Kill();throw ('窗口状态测试超时：'+$mode)}
    $process.Refresh();$code=$process.ExitCode;$process.Dispose()
    if($code -ne 0){Get-Content -LiteralPath $stderr;throw ('窗口状态跨进程测试失败：'+$mode)}
    Get-Content -LiteralPath $stdout
}
