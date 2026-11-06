param(
    [string]$OutputDirectory = (Join-Path $env:TEMP ('PrimaryOnlineEditor-' + [Guid]::NewGuid().ToString('N'))),
    [int]$TimeoutSeconds = 240
)
$ErrorActionPreference = 'Stop'
$game = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../../..'))
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $output) { throw 'Use a fresh output directory to preserve evidence and avoid stale completion flags.' }
New-Item -ItemType Directory -Path $output | Out-Null

function Link-Directory([string]$Destination, [string]$Source) {
    New-Item -ItemType Junction -Path $Destination -Value $Source | Out-Null
}
function Build-Driver([string]$Role, [string]$Server) {
    & dotnet build (Join-Path $PSScriptRoot 'OnlineEditor.csproj') -c Release --nologo -v:q "-p:Server=$Server" -o (Join-Path $output "driver-$Role") *> (Join-Path $output "build-$Role.log")
    if ($LASTEXITCODE -ne 0) { throw "Driver build failed: build-$Role.log" }
}
Build-Driver 'server' 'true'
Build-Driver 'client' 'false'

foreach ($role in @('server', 'a', 'b')) {
    $box = Join-Path $output "box-$role"
    New-Item -ItemType Directory -Path $box | Out-Null
    foreach ($folder in @('Content', 'Submarines')) { Link-Directory (Join-Path $box $folder) (Join-Path $game $folder) }
    Copy-Item -LiteralPath (Join-Path $game 'Data') -Destination $box -Recurse
    $mods = Join-Path $box 'LocalMods'
    New-Item -ItemType Directory -Path $mods | Out-Null
    foreach ($mod in Get-ChildItem -LiteralPath (Join-Path $game 'LocalMods') -Directory) {
        $destination = Join-Path $mods $mod.Name
        if ($mod.Name -ine 'Primary package') { Link-Directory $destination $mod.FullName; continue }
        New-Item -ItemType Directory -Path $destination | Out-Null
        foreach ($file in @('filelist.xml', 'ModConfig.xml')) { Copy-Item -LiteralPath (Join-Path $mod.FullName $file) -Destination $destination }
        foreach ($folder in @('CSharp', 'Content', 'Lua', 'Submarines')) { Link-Directory (Join-Path $destination $folder) (Join-Path $mod.FullName $folder) }
        foreach ($folder in @('Config', 'Data')) { Copy-Item -LiteralPath (Join-Path $mod.FullName $folder) -Destination $destination -Recurse }
    }
    [xml]$config = Get-Content -LiteralPath (Join-Path $game 'config_player.xml') -Raw
    foreach ($setting in @{vsync='false'; framelimit='60'; displaymode='Windowed'; width='960'; height='540'}.GetEnumerator()) {
        $config.config.graphicssettings.SetAttribute($setting.Key, $setting.Value)
    }
    $config.config.SetAttribute('savedebugconsolelogs', 'true')
    $config.Save((Join-Path $box 'config_player.xml'))
    if ($role -eq 'server') {
        $settings = [System.Xml.Linq.XElement]::new([System.Xml.Linq.XName]::Get('serversettings'))
        foreach ($setting in @{
            ServerName='Primary localhost acceptance'; IsPublic='False'; EnableUPnP='False';
            RequireAuthentication='False'; Port='27991'; QueryPort='27992'; MaxPlayers='4';
            TickRate='60'; BotCount='0'; GameModeIdentifier='sandbox'; RandomizeSeed='False'
        }.GetEnumerator()) { $settings.SetAttributeValue($setting.Key, $setting.Value) }
        $settings.Save((Join-Path $box 'serversettings.xml'))
    }
}

$processes = [System.Collections.Generic.List[System.Diagnostics.Process]]::new()
$streams = [System.Collections.Generic.List[object]]::new()
$watch = [System.Diagnostics.Stopwatch]::StartNew()
$success = $false
try {
    foreach ($role in @('server', 'a', 'b')) {
        $info = [System.Diagnostics.ProcessStartInfo]::new('dotnet')
        $info.WorkingDirectory = Join-Path $output "box-$role"
        $info.UseShellExecute = $false
        $info.CreateNoWindow = $true
        $info.WindowStyle = [System.Diagnostics.ProcessWindowStyle]::Hidden
        $info.RedirectStandardOutput = $true
        $info.RedirectStandardError = $true
        if ($role -eq 'server') {
            $info.ArgumentList.Add((Join-Path $game 'DedicatedServer.dll'))
            foreach ($arg in @('-ip', '127.0.0.1')) { $info.ArgumentList.Add($arg) }
            $hook = Join-Path $output 'driver-server/OnlineEditor.DedicatedServer.dll'
        } else {
            $info.ArgumentList.Add((Join-Path $game 'Barotrauma.dll'))
            foreach ($arg in @('-connect', 'Primary localhost acceptance', '127.0.0.1:27991', '-username', "Online-$role")) { $info.ArgumentList.Add($arg) }
            $hook = Join-Path $output 'driver-client/OnlineEditor.Barotrauma.dll'
        }
        $info.Environment['DOTNET_STARTUP_HOOKS'] = $hook
        $info.Environment['PP_ONLINE_ROOT'] = $output
        $info.Environment['PP_ONLINE_ROLE'] = $role
        $info.Environment.Remove('IE_SEQ') | Out-Null
        $info.Environment.Remove('IE_HOST') | Out-Null
        $process = [System.Diagnostics.Process]::Start($info)
        $processes.Add($process)
        $streams.Add(@{Role=$role; Out=$process.StandardOutput.ReadToEndAsync(); Err=$process.StandardError.ReadToEndAsync()})
        Write-Output "Started $role PID=$($process.Id)"
    }
    $captureAt = -1.0
    $circuitCompared = $false
    $pasteCompared = $false
    while ($watch.Elapsed.TotalSeconds -lt $TimeoutSeconds) {
        if ((Get-ChildItem -LiteralPath $output -Filter '*.failed' -File).Count -gt 0) { throw 'A native test process reported failure.' }
        if (($processes | Where-Object HasExited).Count -gt 0) { throw 'A test process exited before acceptance.' }
        if ((Test-Path -LiteralPath (Join-Path $output 'edit.flag')) -and $captureAt -lt 0) {
            $captureAt = $watch.Elapsed.TotalSeconds + 12
            Write-Output 'Two clients opened the editor; concurrent CircuitBridge requests started.'
        }
        if ($captureAt -ge 0 -and $watch.Elapsed.TotalSeconds -ge $captureAt) {
            Set-Content -LiteralPath (Join-Path $output 'capture.flag') -Value 'capture'
        }
        if (!$circuitCompared -and (Test-Path -LiteralPath (Join-Path $output 'server.done')) -and
            (Test-Path -LiteralPath (Join-Path $output 'a.done')) -and (Test-Path -LiteralPath (Join-Path $output 'b.done'))) {
            $serverState = Get-Content -LiteralPath (Join-Path $output 'server.circuit.xml') -Raw
            $clientA = Get-Content -LiteralPath (Join-Path $output 'a.circuit.xml') -Raw
            $clientB = Get-Content -LiteralPath (Join-Path $output 'b.circuit.xml') -Raw
            if ($serverState -cne $clientA -or $serverState -cne $clientB) { throw 'Authoritative CircuitBox differs from a native client.' }
            if ($serverState -notmatch 'online-[ab]') { throw 'No requested edit reached the authoritative circuit.' }
            if ($serverState -notmatch '<Wire\b') { throw 'No requested wire reached the authoritative circuit.' }
            $circuitCompared = $true
            Write-Output 'CircuitBox graph and wires match both clients; starting native Copy/Paste.'
            Set-Content -LiteralPath (Join-Path $output 'paste.flag') -Value 'paste'
        }
        if ($circuitCompared -and !$pasteCompared -and (Test-Path -LiteralPath (Join-Path $output 'server.paste.done')) -and
            (Test-Path -LiteralPath (Join-Path $output 'a.paste.done')) -and (Test-Path -LiteralPath (Join-Path $output 'b.paste.done'))) {
            $serverClone = Get-Content -LiteralPath (Join-Path $output 'server.paste.xml') -Raw
            if ($serverClone -cne (Get-Content -LiteralPath (Join-Path $output 'a.paste.xml') -Raw) -or
                $serverClone -cne (Get-Content -LiteralPath (Join-Path $output 'b.paste.xml') -Raw)) { throw 'Pasted circuit differs between peers.' }
            $pasteCompared = $true
            Write-Output 'Native clipboard retained complete circuit on both clients; starting batch Delete.'
            Set-Content -LiteralPath (Join-Path $output 'delete.flag') -Value 'delete'
        }
        if ($pasteCompared -and (Test-Path -LiteralPath (Join-Path $output 'server.deleted')) -and
            (Test-Path -LiteralPath (Join-Path $output 'a.deleted')) -and (Test-Path -LiteralPath (Join-Path $output 'b.deleted')) -and
            (Test-Path -LiteralPath (Join-Path $output 'b.rejoin.done'))) {
            $success = $true
            Write-Output 'ONLINE PASS: two clients, concurrent wiring, native Copy/Paste, batch Delete, stale-containment snapshot recovery.'
            break
        }
        Start-Sleep -Seconds 2
    }
    if (!$success) { throw 'Online acceptance timed out.' }
} catch {
    $_.Exception.ToString() | Set-Content -LiteralPath (Join-Path $output 'failure.txt')
    Write-Output $_.Exception.Message
} finally {
    Set-Content -LiteralPath (Join-Path $output 'stop.flag') -Value 'stop'
    # These process handles belong exclusively to this run. Never stop by name.
    foreach ($process in $processes) {
        if (!$process.HasExited -and !$process.WaitForExit(5000)) { $process.Kill($true); $process.WaitForExit() }
    }
    foreach ($stream in $streams) {
        [IO.File]::WriteAllText((Join-Path $output ($stream.Role + '.stdout.log')), $stream.Out.GetAwaiter().GetResult())
        [IO.File]::WriteAllText((Join-Path $output ($stream.Role + '.stderr.log')), $stream.Err.GetAwaiter().GetResult())
    }
    Write-Output "Evidence: $output"
}
if (!$success) { exit 1 }
