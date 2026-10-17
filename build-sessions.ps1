param([string]$Python=$env:CODEX_SESSION_PYTHON,[string]$OutputDirectory)
$ErrorActionPreference='Stop'
if(-not $Python){$Python=(Get-Command python.exe -ErrorAction Stop).Source}
$relayBackendOutput=if($OutputDirectory){[IO.Path]::GetFullPath($OutputDirectory)}else{Join-Path $PSScriptRoot 'build'}
& $Python (Join-Path $PSScriptRoot 'session-backend/build.py') --output $relayBackendOutput --work (Join-Path $relayBackendOutput 'session-build')
if($LASTEXITCODE -ne 0){throw '会话服务编译失败，请使用 Python 3.12 x64 并安装 session-backend/requirements-dev.txt。'}
