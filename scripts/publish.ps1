<#
.SYNOPSIS
    Esegue i test e pubblica AIUsageMonitor come singolo eseguibile win-x64.
.DESCRIPTION
    Senza parametri produce una build framework-dependent (richiede il .NET 10 Desktop Runtime
    sulla macchina di destinazione). Con -SelfContained produce un eseguibile autonomo (~180 MB).
    Con -Version la build riporta quella versione (SemVer stabile, come fa la CI dei rilasci con
    -p:Version); senza, resta la <Version> del csproj.
.EXAMPLE
    pwsh scripts/publish.ps1
    pwsh scripts/publish.ps1 -SelfContained -Output publish-sc
    pwsh scripts/publish.ps1 -Version 0.2.0
#>
param(
    [switch]$SelfContained,
    [string]$Output = "publish",
    [ValidatePattern('^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$')]
    [string]$Version
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Push-Location $root
try {
    dotnet test AIUsageMonitor.slnx
    if ($LASTEXITCODE -ne 0) { throw "tests failed" }
    # --no-self-contained e non "--self-contained false": con l'SDK .NET 10 e PublishSingleFile il valore false
    # non arriva a MSBuild e l'eseguibile esce comunque self-contained.
    $mode = @(if ($SelfContained) { "--self-contained"; "true" } else { "--no-self-contained" })
    $versionArgs = @(if ($Version) { "-p:Version=$Version" })
    dotnet publish src/AIUsageMonitor.App -c Release -r win-x64 @mode @versionArgs `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none `
        -o $Output
    if ($LASTEXITCODE -ne 0) { throw "publish failed" }
    Get-ChildItem $Output | Format-Table Name, Length
}
finally { Pop-Location }
