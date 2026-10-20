$ErrorActionPreference = 'Stop'
$exitCode = 1
$secretBytes = $null

try {
  Add-Type -AssemblyName System.Security
  $secretPath = Join-Path $PSScriptRoot 'api-key.dpapi'
  $settingsPath = Join-Path $PSScriptRoot 'settings.json'
  $serverPath = Join-Path $PSScriptRoot 'server.mjs'
  $secretBytes = [Security.Cryptography.ProtectedData]::Unprotect(
    [IO.File]::ReadAllBytes($secretPath),
    $null,
    [Security.Cryptography.DataProtectionScope]::CurrentUser
  )
  $settings = Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json
  $node = Get-Command node.exe -ErrorAction Stop
  $env:CLAUDE_SS_API_KEY = [Text.Encoding]::UTF8.GetString($secretBytes)
  $env:CLAUDE_SS_BASE_URL = [string]$settings.base_url
  & $node.Source $serverPath
  $exitCode = $LASTEXITCODE
}
catch {
  [Console]::Error.WriteLine('Claude MCP launcher failed: ' + $_.Exception.Message)
}
finally {
  Remove-Item Env:CLAUDE_SS_API_KEY -ErrorAction SilentlyContinue
  Remove-Item Env:CLAUDE_SS_BASE_URL -ErrorAction SilentlyContinue
  if ($secretBytes) { [Array]::Clear($secretBytes, 0, $secretBytes.Length) }
}

exit $exitCode
