param(
    [string]$ArtifactsDirectory = '.local/day12-build',
    [int]$Port = 5094,
    [int]$FakePort = 5095,
    [switch]$HoldForBrowser
)
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$buildRoot = Join-Path $projectRoot $ArtifactsDirectory
$checkRoot = Join-Path $projectRoot ('.local/checks/day12-web-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $checkRoot -Force | Out-Null
$baseUrl = "http://127.0.0.1:$Port"
$children = @()
function Start-TestProcess([string]$assembly, [int]$listenPort, [bool]$web) {
    $start = [Diagnostics.ProcessStartInfo]::new('dotnet')
    $start.UseShellExecute = $false; $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true; $start.RedirectStandardError = $true
    $start.WorkingDirectory = Split-Path -Parent $assembly
    $start.ArgumentList.Add($assembly)
    if (!$web) { $start.ArgumentList.Add('--serve-fake') }
    $start.ArgumentList.Add('--urls'); $start.ArgumentList.Add("http://127.0.0.1:$listenPort")
    $start.Environment['ASPNETCORE_ENVIRONMENT'] = 'Production'
    $start.Environment['UNIVERSALAI_DATA_DIR'] = $checkRoot
    $start.Environment['UNIVERSALAI_SERVER_TOKEN'] = ''
    $start.Environment['UNIVERSALAI_Providers__Default'] = 'ollama'
    $start.Environment['UNIVERSALAI_Providers__Profiles__2__BaseUrl'] = "http://127.0.0.1:$FakePort/v1"
    $start.Environment['UNIVERSALAI_Providers__Profiles__2__DefaultModel'] = 'fake-local'
    $start.Environment['UNIVERSALAI_Logging__EventLog__LogLevel__Default'] = 'None'
    $process = [Diagnostics.Process]::Start($start)
    return @{ Process = $process; Out = $process.StandardOutput.ReadToEndAsync(); Err = $process.StandardError.ReadToEndAsync() }
}
function Api([string]$path, [string]$method = 'Get', $body = $null) {
    $parameters = @{ Uri = "$baseUrl/api/$path"; Method = $method; TimeoutSec = 30 }
    if ($null -ne $body) { $parameters.Body = ConvertTo-Json $body -Depth 20; $parameters.ContentType = 'application/json; charset=utf-8' }
    Invoke-RestMethod @parameters
}
try {
    $children += Start-TestProcess (Join-Path $buildRoot 'bin/UniversalAI.Checks/debug/UniversalAI.Checks.dll') $FakePort $false
    $children += Start-TestProcess (Join-Path $buildRoot 'bin/UniversalAI.Web/debug/UniversalAI.Web.dll') $Port $true
    $ready = $false
    for ($attempt = 0; $attempt -lt 100; $attempt++) {
        if ($children.Process.HasExited -contains $true) { throw 'Test server exited early.' }
        try { $null = Api 'health'; $null = Invoke-RestMethod "http://127.0.0.1:$FakePort/v1/models" -TimeoutSec 2; $ready = $true; break }
        catch { Start-Sleep -Milliseconds 100 }
    }
    if (!$ready) { throw 'Test server did not become ready.' }
    if (!((Api 'benchmarks').id -contains 'day12')) { throw 'day12 missing in registry.' }
    $chat = Api 'conversations' 'Post' @{ Title = 'День 12: профиль из диалога'; ProviderId = 'ollama'; Model = 'fake-local' }
    $profile = @{ Name = 'Анна'; LearnFromDialogue = $true } | ConvertTo-Json -Compress
    $null = Api "conversations/$($chat.id)/settings" 'Put' @{ MemoryStrategy = 'Layered'; UserProfileJson = $profile }
    $turn = Api "conversations/$($chat.id)/messages" 'Post' @{ Content = 'Я предпочитаю краткие ответы, без кода. Общайся со мной неформально, на ты.' }
    $info = Api "conversations/$($chat.id)"
    $learned = $info.userProfileJson | ConvertFrom-Json
    if ($learned.Length -ne 'brief' -or $learned.Code -ne 'forbidden' -or $learned.Tone -ne 'informal' -or $turn.context.profileTokens -le 0) { throw 'Profile learning failed over HTTP.' }
    if (!$turn.context.profileResponseNotice) { throw 'JSON normalization notice missing over HTTP.' }
    $invalid = Invoke-WebRequest "$baseUrl/api/conversations/$($chat.id)/settings" -Method Put -Body '{"UserProfileJson":"{\"Tone\":\"invalid\"}"}' -ContentType 'application/json' -SkipHttpErrorCheck
    if ($invalid.StatusCode -ne 400) { throw 'Invalid profile accepted.' }
    $null = Api "conversations/$($chat.id)/settings" 'Put' @{ ClearShortTerm = $true }
    if ((Api "conversations/$($chat.id)").userProfileJson -ne $info.userProfileJson) { throw 'History clearing removed profile.' }
    $null = Api "conversations/$($chat.id)/messages" 'Post' @{ Content = 'Объясни, как работает кэширование результатов вычислений и когда оно полезно.' }
    Write-Output 'OK: HTTP profile learning, validation, persistence and use after clearing history'
    $run = Api 'runs' 'Post' @{ BenchmarkId = 'day12'; Settings = @{ ProviderId = 'ollama'; Model = 'fake-local' } }
    for ($attempt = 0; $attempt -lt 200; $attempt++) {
        $run = Api "runs/$($run.id)"
        if ($run.status -notin @('Queued', 'Running')) { break }
        Start-Sleep -Milliseconds 100
    }
    if ($run.status -ne 'Completed' -or $run.completedCalls -ne 25) { throw "Benchmark incomplete: $($run | ConvertTo-Json -Compress)" }
    $report = Api "runs/$($run.id)/artifacts/report.json"
    if (!$report.Completed -or $report.Observations.Count -ne 13 -or @($report.Observations | Where-Object { !$_.Assessment.Passed }).Count -ne 0) { throw 'Unexpected Day12 report.' }
    if (@($report.Snapshots | Where-Object { $_.ResponseNotice }).Count -ne 12) { throw 'Missing profile normalization evidence.' }
    foreach ($entry in @(@{Path='chat';Text='Профиль пользователя'}, @{Path='experiments';Text='Персонализация'}, @{Path="runs/$($run.id)";Text='Пополнение профиля из диалога'})) {
        $page = Invoke-WebRequest "$baseUrl/$($entry.Path)"
        if (![Net.WebUtility]::HtmlDecode($page.Content).Contains($entry.Text)) { throw "Missing SSR: $($entry.Text)" }
        $page.Content | Set-Content -LiteralPath (Join-Path $checkRoot ($entry.Path.Replace('/', '-') + '.html'))
    }
    $log = Api "runs/$($run.id)/log?full=true"
    if (!$log.Contains('ОЖИДАНИЕ') -or !$log.Contains('ОЦЕНКА') -or !$log.Contains('ОТВЕТ')) { throw 'Demonstration log incomplete.' }
    Write-Output "OK: Web queue Day12 (25 calls), 13/13 fake responses, SSR catalog/chat/stages, complete log"
    Write-Output "Artifacts: $checkRoot; RunId: $($run.id)"
    if ($HoldForBrowser) {
        Write-Output "Browser: $baseUrl/chat ; $baseUrl/runs/$($run.id)"
        Write-Output "Stop file: $(Join-Path $checkRoot 'stop-server')"
        while (!(Test-Path -LiteralPath (Join-Path $checkRoot 'stop-server'))) {
            if ($children.Process.HasExited -contains $true) { throw 'Preview process stopped unexpectedly.' }
            Start-Sleep -Milliseconds 500
        }
    }
}
finally {
    for ($index = 0; $index -lt $children.Count; $index++) {
        $child = $children[$index]
        if (!$child.Process.HasExited) { $child.Process.Kill($true); $child.Process.WaitForExit() }
        [IO.File]::WriteAllText((Join-Path $checkRoot "server-$index.log"), $child.Out.GetAwaiter().GetResult() + $child.Err.GetAwaiter().GetResult())
        $child.Process.Dispose()
    }
}
