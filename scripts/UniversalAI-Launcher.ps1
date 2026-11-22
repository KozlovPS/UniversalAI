param(
    [ValidateRange(1024, 65535)][int]$Port = 5080,
    [switch]$Stop,
    [switch]$NoBrowser,
    [switch]$NoDialogs
)

$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$stateDirectory = Join-Path $projectRoot ".local/launcher/$Port"
$buildDirectory = Join-Path $projectRoot '.local/launcher-build'
$assembly = Join-Path $buildDirectory 'bin/UniversalAI.Web/debug/UniversalAI.Web.dll'
$stateFile = Join-Path $stateDirectory 'server.json'
$baseUrl = "http://localhost:$Port"
$mutex = $null
$locked = $false
$client = $null

function Show-Notice([string]$message, [bool]$isError = $false) {
    if ($NoDialogs) { Write-Output $message; return }
    Add-Type -AssemblyName System.Windows.Forms
    $icon = if ($isError) { [Windows.Forms.MessageBoxIcon]::Error } else { [Windows.Forms.MessageBoxIcon]::Information }
    [void][Windows.Forms.MessageBox]::Show($message, 'UniversalAI', [Windows.Forms.MessageBoxButtons]::OK, $icon)
}

function Test-ServerReady {
    $response = $null
    try {
        $response = $client.GetAsync("$baseUrl/api/health").GetAwaiter().GetResult()
        if ([int]$response.StatusCode -eq 200) {
            $health = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult() | ConvertFrom-Json
            return $health.status -eq 'ready' -and $health.version -eq '1'
        }
        if ([int]$response.StatusCode -eq 401) {
            # A protected workspace is ready too; leave sign-in to the browser.
            $login = $client.GetStringAsync("$baseUrl/signin").GetAwaiter().GetResult()
            return $login.Contains('UniversalAI') -and $login.Contains('/auth/login')
        }
    }
    catch { }
    finally { if ($null -ne $response) { $response.Dispose() } }
    return $false
}

function Test-PortInUse {
    $tcp = [Net.Sockets.TcpClient]::new()
    try { return $tcp.ConnectAsync('localhost', $Port).Wait(500) -and $tcp.Connected }
    catch { return $false }
    finally { $tcp.Dispose() }
}

function Get-OwnedServer {
    if (!(Test-Path -LiteralPath $stateFile)) { return $null }
    $state = Get-Content -LiteralPath $stateFile -Raw | ConvertFrom-Json
    $serverProcess = Get-Process -Id $state.ProcessId -ErrorAction SilentlyContinue
    if ($null -eq $serverProcess) { return $null }
    $command = Get-CimInstance Win32_Process -Filter "ProcessId = $($serverProcess.Id)"
    if ($serverProcess.ProcessName -ne 'dotnet' -or
        $serverProcess.StartTime.ToUniversalTime().ToString('o') -ne $state.StartedAt -or
        $state.Assembly -ne $assembly -or
        $null -eq $command.CommandLine -or !$command.CommandLine.Contains('"' + $assembly + '"')) {
        throw 'Записанный процесс не принадлежит этому запуску UniversalAI. Остановка отменена.'
    }
    return $serverProcess
}

try {
    New-Item -ItemType Directory -Path $stateDirectory -Force | Out-Null
    Add-Type -AssemblyName System.Net.Http
    $handler = [Net.Http.HttpClientHandler]::new()
    $handler.UseProxy = $false
    $client = [Net.Http.HttpClient]::new($handler)
    $client.Timeout = [TimeSpan]::FromSeconds(2)
    $hash = [Security.Cryptography.SHA256]::Create()
    try { $key = [BitConverter]::ToString($hash.ComputeHash([Text.Encoding]::UTF8.GetBytes($projectRoot.ToLowerInvariant()))).Replace('-', '') }
    finally { $hash.Dispose() }
    $mutex = [Threading.Mutex]::new($false, "Local\UniversalAI.Launcher.$key.$Port")
    try { $locked = $mutex.WaitOne([TimeSpan]::FromMinutes(5)) }
    catch [Threading.AbandonedMutexException] { $locked = $true }
    if (!$locked) { throw 'Предыдущий запуск ещё выполняется. Повторите попытку немного позже.' }

    if ($Stop) {
        $owned = Get-OwnedServer
        if ($null -eq $owned) {
            Show-Notice 'Сервер, запущенный этим файлом, не работает. Если он запущен в Rider или другом окне, остановите его там.'
        }
        else {
            Stop-Process -Id $owned.Id -ErrorAction Stop
            [void]$owned.WaitForExit(10000)
            if (!$owned.HasExited) { throw 'Процесс сервера не завершился.' }
            Remove-Item -LiteralPath $stateFile -ErrorAction SilentlyContinue
            Show-Notice 'Сервер UniversalAI остановлен.'
        }
    }
    else {
        if (!(Test-ServerReady)) {
            if (Test-PortInUse) { throw "Порт $Port занят, но готовый UniversalAI не обнаружен. Проверьте приложение, использующее этот порт." }
            $dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
            if ($null -eq $dotnet) { throw 'Не найден .NET SDK. Установите SDK, указанный в global.json, и повторите запуск.' }
            $buildLog = Join-Path $stateDirectory 'build.log'
            Push-Location $projectRoot
            try {
                & $dotnet.Source build (Join-Path $projectRoot 'src/UniversalAI.Web/UniversalAI.Web.csproj') --artifacts-path $buildDirectory -p:UseAppHost=false *> $buildLog
                if ($LASTEXITCODE -ne 0) { throw "Не удалось собрать UniversalAI. Журнал: $buildLog" }
            }
            finally { Pop-Location }
            $server = Start-Process -FilePath $dotnet.Source -ArgumentList @('"' + $assembly + '"', '--urls', $baseUrl) `
                -WorkingDirectory (Split-Path -Parent $assembly) -WindowStyle Hidden -PassThru `
                -RedirectStandardOutput (Join-Path $stateDirectory 'server.log') `
                -RedirectStandardError (Join-Path $stateDirectory 'server-errors.log')
            @{ ProcessId = $server.Id; StartedAt = $server.StartTime.ToUniversalTime().ToString('o'); Assembly = $assembly } |
                ConvertTo-Json | Set-Content -LiteralPath $stateFile -Encoding UTF8
            $deadline = [DateTime]::UtcNow.AddMinutes(3)
            $ready = $false
            while ([DateTime]::UtcNow -lt $deadline) {
                if ($server.HasExited) { throw "Сервер завершился при запуске. Журналы: $stateDirectory" }
                if (Test-ServerReady) { $ready = $true; break }
                Start-Sleep -Milliseconds 300
            }
            if (!$ready) { throw "Сервер не ответил за три минуты. Журналы: $stateDirectory" }
        }
        if (!$NoBrowser) { Start-Process "$baseUrl/experiments" }
        Write-Output "UniversalAI ready: $baseUrl/experiments"
    }
}
catch {
    $message = $_.Exception.Message
    if (Test-Path -LiteralPath $stateDirectory) { $message | Set-Content -LiteralPath (Join-Path $stateDirectory 'launcher-error.log') -Encoding UTF8 }
    Show-Notice $message $true
    exit 1
}
finally {
    if ($locked) { $mutex.ReleaseMutex() }
    if ($null -ne $mutex) { $mutex.Dispose() }
    if ($null -ne $client) { $client.Dispose() }
}
