#!/usr/bin/env pwsh
#Requires -Version 5.1
<#
.SYNOPSIS
    Builds slaw, ensures a local Ollama instance is serving the requested
    model, and launches slaw with it. The Windows-native counterpart to run.sh.

.PARAMETER SlawArgs
    Arguments passed through to slaw. Defaults to 'studio' when omitted.

.EXAMPLE
    ./run.ps1

.EXAMPLE
    $env:MODEL = "mistral"; ./run.ps1 studio examples/synergy-lofi.slaw
#>
[CmdletBinding()]
param(
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$SlawArgs
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$IsWindowsHost = $IsWindows -or ($PSVersionTable.PSVersion.Major -le 5)
$SlawBinary = if ($IsWindowsHost) { './out/slaw.exe' } else { './out/slaw' }

$Model = if ($env:MODEL) { $env:MODEL } else { 'llama3' }
$OllamaRoot = 'http://localhost:11434'

function Test-OllamaReachable {
    try {
        Invoke-WebRequest -Uri $OllamaRoot -UseBasicParsing -TimeoutSec 2 | Out-Null
        return $true
    } catch {
        return $false
    }
}

function Assert-CommandAvailable([string]$Name, [string]$InstallHint) {
    if (-not (Get-Command $Name -ErrorAction SilentlyContinue)) {
        Write-Error "[error] '$Name' is not on PATH. $InstallHint"
    }
}

if (-not $SlawArgs -or $SlawArgs.Count -eq 0) {
    $SlawArgs = @('studio')
}

Assert-CommandAvailable 'dotnet' 'Install it with: winget install Microsoft.DotNet.SDK.8'

# Build slaw if needed
if (-not (Test-Path -LiteralPath $SlawBinary)) {
    Write-Host "[setup] building slaw..."
    & dotnet publish src/Slaw/Slaw.csproj -c Release -o out
    if ($LASTEXITCODE -ne 0) { Write-Error "[error] dotnet publish failed" }
}

Assert-CommandAvailable 'ollama' 'Install it from https://ollama.com/download, or via: winget install Ollama.Ollama'

# Start ollama in the background if it is not already serving
if (-not (Test-OllamaReachable)) {
    Write-Host "[setup] starting ollama..."
    Start-Process -FilePath 'ollama' -ArgumentList 'serve' -WindowStyle Hidden | Out-Null
    $ready = $false
    foreach ($attempt in 1..20) {
        Start-Sleep -Milliseconds 500
        if (Test-OllamaReachable) { $ready = $true; break }
    }
    if (-not $ready) { Write-Error "[error] ollama did not start in time" }
}

# Pull the model if it is not present locally
$installedModels = & ollama list 2>$null
if (-not ($installedModels | Select-String -SimpleMatch -Pattern $Model -Quiet)) {
    Write-Host "[setup] pulling model $Model..."
    & ollama pull $Model
    if ($LASTEXITCODE -ne 0) { Write-Error "[error] could not pull model $Model" }
}

Write-Host "[run] slaw $($SlawArgs -join ' ') with model $Model"
& $SlawBinary --model $Model @SlawArgs
exit $LASTEXITCODE
