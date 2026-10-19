param([string]$ToolchainRoot=$env:CODEX_RELAY_TOOLCHAIN,[string]$CodexPath='codex',[switch]$SkipCli)
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot
if(-not $ToolchainRoot){$ToolchainRoot=Join-Path (Split-Path $root) 'native-toolchain'}
$output=Join-Path $root 'build/codex-provider-tests'
New-Item -ItemType Directory -Force -Path $output | Out-Null
$references=@('System.dll','System.Core.dll','System.Web.Extensions.dll','System.Net.Http.dll','System.Security.dll',(Join-Path $ToolchainRoot 'nett/lib/net40/Nett.dll'))
Copy-Item -LiteralPath (Join-Path $ToolchainRoot 'nett/lib/net40/Nett.dll') -Destination $output
$arguments=@('/nologo','/target:exe','/platform:x64','/optimize+',('/out:'+(Join-Path $output 'CodexProviderTestHost.exe')))
foreach($reference in $references){$arguments+=('/reference:'+$reference)}
foreach($source in @('Json','Secure','Store','Usage','UsageCache','ModelProtocol','ModelCatalog','Models','ConnectionTest','ModelCatalogChecks','ModelsChecks','DraftChecks','CodexOnlyChecks')){$arguments+=(Join-Path $root ('src/'+$source+'.cs'))}
$arguments+=(Join-Path $PSScriptRoot 'codex-provider-host.cs')
& (Join-Path $ToolchainRoot 'roslyn/tasks/net472/csc.exe') @arguments
if($LASTEXITCODE -ne 0){throw 'Codex 中转测试编译失败。'}
& (Join-Path $output 'CodexProviderTestHost.exe') (Join-Path $output ('fixture-'+[guid]::NewGuid().ToString('N')))
if($LASTEXITCODE -ne 0){throw 'Codex 配置与协议限制测试失败。'}
if(-not $SkipCli){
    Push-Location $root
    try{
        & node (Join-Path $PSScriptRoot 'model-catalog-cli.cjs') (Join-Path $output 'CodexProviderTestHost.exe') $CodexPath
        if($LASTEXITCODE -ne 0){throw 'Codex 原生模型列表与当前中转显示名同步测试失败。'}
    }finally{Pop-Location}
}
