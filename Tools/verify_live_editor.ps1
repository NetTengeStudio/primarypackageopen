param(
    [string]$GameRoot = 'C:/Steam/steamapps/common/Barotrauma',
    [string]$LuaCsRoot = 'C:/Steam/steamapps/workshop/content/602960/2559634234'
)
$ErrorActionPreference = 'Stop'
$primaryRoot = Split-Path -Parent $PSScriptRoot
$evidenceRoot = Join-Path $GameRoot 'Docs/PrimaryPackage/LiveEditor'
New-Item -ItemType Directory -Force -Path $evidenceRoot | Out-Null
$simulatorProject = Join-Path $GameRoot 'LocalMods/LuaCsBuildSimulator'
$simulator = Join-Path $simulatorProject 'bin/Release/net10.0/LuaCsBuildSimulator.dll'
$testProject = Join-Path $primaryRoot 'Tests/GMPlatform'
$test = Join-Path $testProject 'bin/Release/net10.0/GMPlatform.dll'
function Run-Check([string]$Name, [string]$Executable, [string[]]$Arguments) {
    & $Executable @Arguments *> (Join-Path $evidenceRoot ($Name + '.log'))
    if ($LASTEXITCODE -ne 0) { throw "${Name}: failed; see $evidenceRoot/$Name.log" }
    Write-Output "PASS $Name"
}
# No engine entry point, client or dedicated server is launched by this script.
Run-Check 'simulator-build' 'dotnet' @('build', $simulatorProject, '-c', 'Release', '--no-restore', '-v', 'minimal')
Run-Check 'primary-build' 'dotnet' @($simulator, '--mod', $primaryRoot, '--luacs', $LuaCsRoot, '--out', (Join-Path $evidenceRoot 'build'))
Run-Check 'tests-build' 'dotnet' @('build', $testProject, '-c', 'Release', '--no-restore', '-v', 'minimal')
Run-Check 'tests' 'dotnet' @($test, $primaryRoot)
Run-Check 'contracts-client' 'dotnet' @($test, '--contracts', $GameRoot, (Join-Path $evidenceRoot 'build/client/InternalsAwareAssembly.dll'))
Run-Check 'contracts-server' 'dotnet' @($test, '--contracts', (Join-Path $LuaCsRoot 'Binary'), (Join-Path $evidenceRoot 'build/server/InternalsAwareAssembly.dll'))
Run-Check 'contentclient-build' 'dotnet' @($simulator, '--mod', (Join-Path $GameRoot 'LocalMods/contentclient'), '--luacs', $LuaCsRoot, '--out', (Join-Path $evidenceRoot 'contentclient-build'), '--compile-only')
Run-Check 'contentserver-build' 'dotnet' @($simulator, '--mod', (Join-Path $GameRoot 'LocalMods/contentserver'), '--luacs', $LuaCsRoot, '--out', (Join-Path $evidenceRoot 'contentserver-build'), '--compile-only')
$activeClient = 'C:/Users/Admin/AppData/Local/Daedalic Entertainment GmbH/Barotrauma/WorkshopMods/Installed/3831040655'
Run-Check 'active-contentclient-build' 'dotnet' @($simulator, '--mod', $activeClient, '--luacs', $LuaCsRoot, '--out', (Join-Path $evidenceRoot 'active-contentclient-build'), '--compile-only')
$contentTests = Join-Path $primaryRoot 'Tests/ContentContracts'
Run-Check 'content-tests-build' 'dotnet' @('build', $contentTests, '-c', 'Release', '--no-restore', '-v', 'minimal')
Run-Check 'content-tests' 'dotnet' @((Join-Path $contentTests 'bin/Release/net10.0/ContentContracts.dll'))
$physicsTests = Join-Path $primaryRoot 'Tests/PhysicsContracts'
Run-Check 'physics-tests-build' 'dotnet' @('build', $physicsTests, '-c', 'Release', '--no-restore', '-v', 'minimal')
Run-Check 'physics-tests' 'dotnet' @((Join-Path $physicsTests 'bin/Release/net10.0/PhysicsContracts.dll'))
Run-Check 'sync-log-tests' 'python' @('-m', 'unittest', 'discover', '-s', (Join-Path $primaryRoot 'Tests'), '-p', 'test_world_sync_diagnostics.py')
Run-Check 'sync-log-analysis' 'python' @((Join-Path $PSScriptRoot 'diagnose_world_sync.py'))
Run-Check 'audit' 'python' @((Join-Path $PSScriptRoot 'audit_live_editor.py'))
Run-Check 'gm-audit' 'python' @((Join-Path $PSScriptRoot 'audit_gm_platform.py'))
Run-Check 'diff-check' 'git' @('-C', $primaryRoot, 'diff', '--check')
Run-Check 'verification' 'python' @((Join-Path $PSScriptRoot 'record_live_editor_verification.py'))
