<#
.SYNOPSIS
    Esegue i test e pubblica AIUsageMonitor come singolo eseguibile win-x64.
.DESCRIPTION
    Senza parametri produce una build framework-dependent (richiede il .NET 10 Desktop Runtime
    sulla macchina di destinazione). Con -SelfContained produce un eseguibile autonomo (~80 MB).
.EXAMPLE
    pwsh scripts/publish.ps1
    pwsh scripts/publish.ps1 -SelfContained -Output publish-sc
#>
param(
    [switch]$SelfContained,
    [string]$Output = "publish"
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Push-Location $root
try {
    dotnet test AIUsageMonitor.slnx
    if ($LASTEXITCODE -ne 0) { throw "tests failed" }
    $sc = if ($SelfContained) { "true" } else { "false" }
    dotnet publish src/AIUsageMonitor.App -c Release -r win-x64 --self-contained $sc `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none `
        -o $Output
    if ($LASTEXITCODE -ne 0) { throw "publish failed" }
    Get-ChildItem $Output | Format-Table Name, Length
}
finally { Pop-Location }
