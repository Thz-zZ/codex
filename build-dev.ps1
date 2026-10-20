param([string]$OutputDirectory)
$ErrorActionPreference='Stop'
$relayRoot=$PSScriptRoot
function Invoke-RelayCompiler([string]$Executable,[string]$Response,[string[]]$Arguments,[string[]]$Inputs) {
    $lines=$Arguments | ForEach-Object {'"'+($_ -replace '"','\"')+'"'}
    $content=($lines -join [Environment]::NewLine)+[Environment]::NewLine
    $changed=-not [IO.File]::Exists($Response) -or [IO.File]::ReadAllText($Response) -ne $content
    $required=$changed -or -not [IO.File]::Exists($Executable)
    if(-not $required){
        $built=(Get-Item -LiteralPath $Executable).LastWriteTimeUtc
        $required=@($Inputs | Where-Object {-not [IO.File]::Exists($_) -or (Get-Item -LiteralPath $_).LastWriteTimeUtc -gt $built}).Count -gt 0
    }
    if(-not $required){Write-Output ('Reusing unchanged build: '+$Executable);return}
    if($changed){[IO.File]::WriteAllText($Response,$content,[Text.UTF8Encoding]::new($true))}
    & (Join-Path $relayDependencies 'roslyn/tasks/net472/csc.exe') ('@'+$Response)
    if($LASTEXITCODE -ne 0){throw ('编译失败：'+$Executable)}
}
$relayVersionMatch=[regex]::Match([IO.File]::ReadAllText((Join-Path $relayRoot 'src/AppVersion.cs')),'const string Number="(\d+\.\d+\.\d+)"')
if(-not $relayVersionMatch.Success){throw '无法读取应用版本号。'}
$relayVersion=$relayVersionMatch.Groups[1].Value
$relayDependencies=if($env:CODEX_RELAY_TOOLCHAIN){(Resolve-Path $env:CODEX_RELAY_TOOLCHAIN).Path}else{Join-Path (Split-Path $relayRoot) 'native-toolchain'}
$relayBuild=if($OutputDirectory){[IO.Path]::GetFullPath($OutputDirectory)}else{Join-Path $relayRoot 'build/current'}
$relayUI=Join-Path $relayBuild 'ui'
$relayBackend=Join-Path $relayBuild 'SessionBackend.exe'
$relayBackendSources=Get-ChildItem -LiteralPath (Join-Path $relayRoot 'session-backend') -Filter '*.py' -Recurse | Where-Object {$_.FullName -notmatch '[\\/]tests[\\/]'}
if(-not (Test-Path -LiteralPath $relayBackend) -or ($relayBackendSources | Where-Object {$_.LastWriteTimeUtc -gt (Get-Item -LiteralPath $relayBackend).LastWriteTimeUtc})){& (Join-Path $relayRoot 'build-sessions.ps1') -OutputDirectory $relayBuild
  if($LASTEXITCODE -ne 0){throw '会话服务编译失败。'}
}
New-Item -ItemType Directory -Force -Path $relayUI | Out-Null
$relayWebView=Join-Path $relayDependencies 'webview2/lib/net462'
Copy-Item -LiteralPath (Join-Path $relayWebView 'Microsoft.Web.WebView2.Core.dll'),(Join-Path $relayWebView 'Microsoft.Web.WebView2.WinForms.dll'),(Join-Path $relayDependencies 'webview2/build/native/x64/WebView2Loader.dll'),(Join-Path $relayDependencies 'nett/lib/net40/Nett.dll') -Destination $relayBuild
Get-ChildItem -LiteralPath (Join-Path $relayRoot 'ui-src') -File | Where-Object {$_.Extension -in @('.html','.js','.css','.png','.ico')} | Copy-Item -Destination $relayUI
Copy-Item -LiteralPath (Join-Path $relayRoot 'bridge.js') -Destination $relayUI
Copy-Item -LiteralPath (Join-Path $relayRoot 'tests/draft-editor-smoke.js') -Destination $relayBuild
Copy-Item -LiteralPath (Join-Path $relayRoot 'tests/sessions-smoke.js') -Destination $relayBuild
Copy-Item -LiteralPath (Join-Path $relayRoot 'tests/updates-smoke.js') -Destination $relayBuild
$relayIndex=Join-Path $relayUI 'index.html'
$relayHtml=[IO.File]::ReadAllText($relayIndex).Replace("connect-src 'none'","connect-src 'self'").Replace('<script src="cache-adapters.js">','<script src="bridge.js"></script><script src="cache-adapters.js">')
$relayHtml=[regex]::Replace($relayHtml,'(<span data-app-version>)[^<]*(</span>)',('${1}v'+$relayVersion+'${2}'))
[IO.File]::WriteAllText($relayIndex,$relayHtml,[Text.UTF8Encoding]::new($false))
$relayReferences=@('System.dll','System.Core.dll','System.Drawing.dll','System.Windows.Forms.dll','System.Web.Extensions.dll','System.Net.Http.dll','System.Web.dll','System.Security.dll','System.IO.Compression.dll','System.IO.Compression.FileSystem.dll','Microsoft.CSharp.dll',(Join-Path $relayBuild 'Microsoft.Web.WebView2.Core.dll'),(Join-Path $relayBuild 'Microsoft.Web.WebView2.WinForms.dll'),(Join-Path $relayBuild 'Nett.dll'))
$relayArguments=@('/nologo','/target:winexe','/platform:x64','/optimize+',('/out:'+(Join-Path $relayBuild 'Codex Relay Lite.exe')),('/win32manifest:'+(Join-Path $relayRoot 'lite.manifest')),('/win32icon:'+(Join-Path $relayUI 'icon.ico')))
foreach($relayReference in $relayReferences){$relayArguments+=('/reference:'+$relayReference)}
$relaySources=(Get-ChildItem -LiteralPath (Join-Path $relayRoot 'src') -Filter '*.cs').FullName
$relayArguments+=$relaySources
$relayResponse=Join-Path $relayBuild 'dev.rsp'
$relayCompiler=Join-Path $relayDependencies 'roslyn/tasks/net472/csc.exe'
$relayCompileInputs=@($relaySources)+@($relayReferences | Where-Object {[IO.Path]::IsPathRooted($_)})+@((Join-Path $relayRoot 'lite.manifest'),(Join-Path $relayUI 'icon.ico'),$relayCompiler)
Invoke-RelayCompiler -Executable (Join-Path $relayBuild 'Codex Relay Lite.exe') -Response $relayResponse -Arguments $relayArguments -Inputs $relayCompileInputs
$relayUpdaterArguments=@('/nologo','/target:winexe','/platform:x64','/optimize+',('/out:'+(Join-Path $relayBuild 'Updater.exe')),('/win32manifest:'+(Join-Path $relayRoot 'lite.manifest')),('/win32icon:'+(Join-Path $relayUI 'icon.ico')))
foreach($relayReference in @('System.dll','System.Core.dll','System.Windows.Forms.dll','System.Web.Extensions.dll','System.IO.Compression.dll','System.IO.Compression.FileSystem.dll')){$relayUpdaterArguments+=('/reference:'+$relayReference)}
$relayUpdaterArguments+=(Join-Path $relayRoot 'updater/Program.cs'),(Join-Path $relayRoot 'src/Json.cs'),(Join-Path $relayRoot 'src/UpdatePackage.cs')
$relayUpdaterResponse=Join-Path $relayBuild 'updater.rsp'
$relayUpdaterInputs=@((Join-Path $relayRoot 'updater/Program.cs'),(Join-Path $relayRoot 'src/Json.cs'),(Join-Path $relayRoot 'src/UpdatePackage.cs'),(Join-Path $relayRoot 'lite.manifest'),(Join-Path $relayUI 'icon.ico'),$relayCompiler)
Invoke-RelayCompiler -Executable (Join-Path $relayBuild 'Updater.exe') -Response $relayUpdaterResponse -Arguments $relayUpdaterArguments -Inputs $relayUpdaterInputs
('Development build ready: v'+$relayVersion+'. No release package was changed.')
