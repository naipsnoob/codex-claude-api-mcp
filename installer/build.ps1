$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$mcpRoot = Join-Path $repoRoot 'mcp'
$project = Join-Path $PSScriptRoot 'CodexClaudeApiMcpSetup.csproj'
$bundle = Join-Path $PSScriptRoot 'runtime-bundle.zip'
$output = Join-Path $repoRoot 'dist'
$sourceFiles = @('server.mjs', 'executor.mjs', 'request.mjs', 'state-store.mjs', 'package.json', 'package-lock.json', 'selfcheck.mjs', 'launch.ps1')

Push-Location $mcpRoot
try {
  npm ci --ignore-scripts
  if ($LASTEXITCODE -ne 0) { throw "npm ci --ignore-scripts failed with exit code $LASTEXITCODE" }
  npm run selfcheck
  if ($LASTEXITCODE -ne 0) { throw "MCP selfcheck failed with exit code $LASTEXITCODE" }
}
finally { Pop-Location }

$bundleStage = Join-Path $env:TEMP ('codex-claude-runtime-' + [guid]::NewGuid().ToString('N'))
$expectedPrefix = [IO.Path]::GetFullPath((Join-Path $env:TEMP 'codex-claude-runtime-'))
$bundleStageFullPath = [IO.Path]::GetFullPath($bundleStage)
if (-not $bundleStageFullPath.StartsWith($expectedPrefix, [StringComparison]::OrdinalIgnoreCase)) {
  throw "Unsafe runtime staging path: $bundleStageFullPath"
}
New-Item -ItemType Directory -Path $bundleStage | Out-Null
try {
  foreach ($file in $sourceFiles) { Copy-Item -LiteralPath (Join-Path $mcpRoot $file) -Destination $bundleStage }
  Compress-Archive -Path (Join-Path $bundleStage '*') -DestinationPath $bundle -Force
}
finally {
  if ((Test-Path -LiteralPath $bundleStageFullPath) -and $bundleStageFullPath.StartsWith($expectedPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    Remove-Item -LiteralPath $bundleStageFullPath -Recurse -Force
  }
}

dotnet run --project $project -- --selfcheck
if ($LASTEXITCODE -ne 0) { throw "Installer selfcheck failed with exit code $LASTEXITCODE" }

New-Item -ItemType Directory -Path $output -Force | Out-Null
dotnet publish $project -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
  "-p:RuntimeBundle=$bundle" -o $output
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }

Write-Host "Built $output\CodexClaudeApiMcpSetup.exe"
