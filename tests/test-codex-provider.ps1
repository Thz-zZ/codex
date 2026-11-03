param([string]$ToolchainRoot=$env:CODEX_RELAY_TOOLCHAIN,[string]$CodexPath='codex',[switch]$SkipCli)
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot
if(-not $ToolchainRoot){$ToolchainRoot=Join-Path (Split-Path $root) 'native-toolchain'}
$output=Join-Path $root 'build/current'
New-Item -ItemType Directory -Force -Path $output | Out-Null
$references=@('System.dll','System.Core.dll','System.Web.Extensions.dll','System.Net.Http.dll','System.Security.dll',(Join-Path $ToolchainRoot 'nett/lib/net40/Nett.dll'))
Copy-Item -LiteralPath (Join-Path $ToolchainRoot 'nett/lib/net40/Nett.dll') -Destination $output
$arguments=@('/nologo','/target:exe','/platform:x64','/optimize+',('/out:'+(Join-Path $output 'CodexProviderTestHost.exe')))
foreach($reference in $references){$arguments+=('/reference:'+$reference)}
foreach($source in @('Json','Secure','Store','Usage','UsageCache','ModelProtocol','ModelCatalog','NativeModelCatalog','TomlConfig','TomlWhitespace','Models','ConnectionTest','ModelCatalogChecks','ModelsChecks','DraftChecks','CodexOnlyChecks')){$arguments+=(Join-Path $root ('src/'+$source+'.cs'))}
$arguments+=(Join-Path $PSScriptRoot 'codex-provider-host.cs')
 $testHost=Join-Path $output 'CodexProviderTestHost.exe'
 $compile=(-not (Test-Path -LiteralPath $testHost)) -or @($arguments | Where-Object {$_ -like '*.cs' -and (Get-Item -LiteralPath $_).LastWriteTimeUtc -gt (Get-Item -LiteralPath $testHost).LastWriteTimeUtc}).Count -gt 0
 if($compile){& (Join-Path $ToolchainRoot 'roslyn/tasks/net472/csc.exe') @arguments;if($LASTEXITCODE -ne 0){throw 'Codex 中转测试编译失败。'}}
$work=Join-Path $output 'provider-test-work'
if(Test-Path -LiteralPath $work){throw 'provider-test-work 已存在，请检查上次测试产物。'}
New-Item -ItemType Directory -Path $work | Out-Null
try{
& $testHost (Join-Path $work 'native')
if($LASTEXITCODE -ne 0){throw 'Codex 配置与协议限制测试失败。'}
$linkFixture=Join-Path $work 'links'
$linkTarget=Join-Path $linkFixture 'real'
New-Item -ItemType Directory -Path $linkTarget -Force | Out-Null
New-Item -ItemType Junction -Path (Join-Path $linkFixture 'linked') -Target $linkTarget | Out-Null
& (Join-Path $output 'CodexProviderTestHost.exe') $linkFixture '--check-links'
if($LASTEXITCODE -ne 0){throw '模型目录及配置链接隔离测试失败。'}
if(-not $SkipCli){
    Push-Location $root
    try{
        & node (Join-Path $PSScriptRoot 'model-catalog-cli.cjs') (Join-Path $output 'CodexProviderTestHost.exe') $CodexPath
        if($LASTEXITCODE -ne 0){throw 'Codex 原生模型列表与当前中转显示名同步测试失败。'}
        & node (Join-Path $PSScriptRoot 'model-context-cli.cjs') (Join-Path $output 'CodexProviderTestHost.exe') $CodexPath
        if($LASTEXITCODE -ne 0){throw 'Responses 直连上下文窗口与实际 Token 通知测试失败。'}
    }finally{Pop-Location}
}
}finally{
    $verifiedWork=[IO.Path]::GetFullPath($work)
    $verifiedOutput=[IO.Path]::GetFullPath($output)
    if([IO.Path]::GetDirectoryName($verifiedWork) -ne $verifiedOutput -or [IO.Path]::GetFileName($verifiedWork) -ne 'provider-test-work'){throw 'Unexpected test cleanup target.'}
    $junction=Join-Path $work 'links/linked'
    if(Test-Path -LiteralPath $junction){Remove-Item -LiteralPath $junction -Force}
    Remove-Item -LiteralPath $verifiedWork -Recurse -Force
}
