param([string]$ToolchainRoot=$env:CODEX_RELAY_TOOLCHAIN,[string]$CodexPath='codex')
$ErrorActionPreference='Stop'
$bridgeRoot=Split-Path $PSScriptRoot
if(-not $ToolchainRoot){$ToolchainRoot=Join-Path (Split-Path $bridgeRoot) 'native-toolchain'}
$bridgeOutput=Join-Path $bridgeRoot 'build/model-bridge-tests'
New-Item -ItemType Directory -Force -Path $bridgeOutput | Out-Null
$bridgeReferences=@('System.dll','System.Core.dll','System.Web.Extensions.dll','System.Net.Http.dll','System.Security.dll',(Join-Path $ToolchainRoot 'nett/lib/net40/Nett.dll'))
Copy-Item -LiteralPath (Join-Path $ToolchainRoot 'nett/lib/net40/Nett.dll') -Destination $bridgeOutput
$bridgeArguments=@('/nologo','/target:exe','/platform:x64','/optimize+',('/out:'+(Join-Path $bridgeOutput 'ModelBridgeTestHost.exe')))
foreach($bridgeReference in $bridgeReferences){$bridgeArguments+=('/reference:'+$bridgeReference)}
foreach($bridgeSource in @('Json','Secure','Store','Usage','UsageCache','ModelProtocol','ClaudeProtocol','ModelBridge','ModelCatalog','Models','ConnectionTest','ModelBridgeChecks','ClaudeProtocolChecks','ModelCatalogChecks')){$bridgeArguments+=(Join-Path $bridgeRoot ('src/'+$bridgeSource+'.cs'))}
$bridgeArguments+=(Join-Path $PSScriptRoot 'model-bridge-host.cs')
& (Join-Path $ToolchainRoot 'roslyn/tasks/net472/csc.exe') @bridgeArguments
if($LASTEXITCODE -ne 0){throw '模型桥接测试编译失败。'}
& (Join-Path $bridgeOutput 'ModelBridgeTestHost.exe') (Join-Path $bridgeOutput ('fixture-'+[guid]::NewGuid().ToString('N')))
if($LASTEXITCODE -ne 0){throw '模型桥接协议与 HTTP 测试失败。'}
Push-Location $bridgeRoot
try{
    & node (Join-Path $PSScriptRoot 'model-catalog-cli.cjs') (Join-Path $bridgeOutput 'ModelBridgeTestHost.exe') $CodexPath
    if($LASTEXITCODE -ne 0){throw 'Codex 原生模型列表与当前中转显示名同步测试失败。'}
    & node (Join-Path $PSScriptRoot 'model-context-cli.cjs') (Join-Path $bridgeOutput 'ModelBridgeTestHost.exe') $CodexPath
    if($LASTEXITCODE -ne 0){throw 'Codex 原生上下文窗口与实际 Token 用量通知测试失败。'}
    foreach($bridgeProtocol in @('chat-completions','anthropic')){
        foreach($effortDefault in @('default','high')){
            & node (Join-Path $PSScriptRoot 'model-bridge-cli.cjs') (Join-Path $bridgeOutput 'ModelBridgeTestHost.exe') $CodexPath 'relay-effort-fixture' $bridgeProtocol 'relay-effort-fixture' 'catalog' $effortDefault 'low'
            if($LASTEXITCODE -ne 0){throw 'Codex 会话强度覆盖可选中转默认值测试失败。'}
        }
    }
    foreach($bridgeModel in @('deepseek-fixture','gpt-6.1-sol','grok-fixture','qwen-fixture','glm-fixture','gemini-fixture')){
        & node (Join-Path $PSScriptRoot 'model-bridge-cli.cjs') (Join-Path $bridgeOutput 'ModelBridgeTestHost.exe') $CodexPath $bridgeModel
        if($LASTEXITCODE -ne 0){throw 'Codex CLI 端到端桥接测试失败。'}
    }
    & node (Join-Path $PSScriptRoot 'model-bridge-cli.cjs') (Join-Path $bridgeOutput 'ModelBridgeTestHost.exe') $CodexPath 'gpt-6.1-sol' 'anthropic'
    if($LASTEXITCODE -ne 0){throw 'Codex CLI Claude Messages 端到端桥接测试失败。'}
    foreach($bridgeProtocol in @('chat-completions','anthropic')){
        & node (Join-Path $PSScriptRoot 'model-bridge-cli.cjs') (Join-Path $bridgeOutput 'ModelBridgeTestHost.exe') $CodexPath 'configured-relay-fixture' $bridgeProtocol 'gpt-6.1-sol'
        if($LASTEXITCODE -ne 0){throw 'Codex CLI 旧会话模型覆盖已选中转模型回归测试失败。'}
    }
    & node (Join-Path $PSScriptRoot 'model-bridge-cli.cjs') (Join-Path $bridgeOutput 'ModelBridgeTestHost.exe') $CodexPath 'claude-opus-5-5' 'anthropic' 'gemini-4-argon'
    if($LASTEXITCODE -ne 0){throw 'Codex CLI Claude 模型、原生强度、上游缓存策略和旧 Gemini 会话回归测试失败。'}
    foreach($bridgeProtocol in @('chat-completions','anthropic')){
        & node (Join-Path $PSScriptRoot 'model-bridge-cli.cjs') (Join-Path $bridgeOutput 'ModelBridgeTestHost.exe') $CodexPath 'image-relay-fixture' $bridgeProtocol 'image-relay-fixture' 'image'
        if($LASTEXITCODE -ne 0){throw 'Codex CLI 查看图片工具结果桥接回归测试失败。'}
        & node (Join-Path $PSScriptRoot 'model-bridge-cli.cjs') (Join-Path $bridgeOutput 'ModelBridgeTestHost.exe') $CodexPath 'relay-default-fixture' $bridgeProtocol 'relay-selected-fixture' 'catalog'
        if($LASTEXITCODE -ne 0){throw 'Codex CLI 切换非默认中转模型回归测试失败。'}
    }
}finally{Pop-Location}
