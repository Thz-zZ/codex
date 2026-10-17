param([string]$Python=$env:CODEX_SESSION_PYTHON)
$ErrorActionPreference='Stop'
if(-not $Python){$Python=(Get-Command python.exe -ErrorAction Stop).Source}
& $Python (Join-Path $PSScriptRoot 'session-backend/build.py') --output (Join-Path $PSScriptRoot 'build') --work (Join-Path $PSScriptRoot 'build/session-build')
if($LASTEXITCODE -ne 0){throw '会话服务编译失败，请使用 Python 3.12 x64 并安装 session-backend/requirements-dev.txt。'}
