param([int]$Port = 5098, [int]$FakePort = 5099, [switch]$HoldForBrowser)
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$checkRoot = Join-Path $projectRoot ('.local/checks/day14-web-' + [Guid]::NewGuid().ToString('N'))
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
    if (!$web) { $start.Environment['UNIVERSALAI_CHECKS_FAKE_DELAY_MS'] = '80' }
    $process = [Diagnostics.Process]::Start($start)
    return @{ Process = $process; Out = $process.StandardOutput.ReadToEndAsync(); Err = $process.StandardError.ReadToEndAsync() }
}
function Api([string]$path, [string]$method = 'Get', $body = $null) {
    $parameters = @{ Uri = "$baseUrl/api/$path"; Method = $method; TimeoutSec = 30 }
    if ($null -ne $body) { $parameters.Body = ConvertTo-Json $body -Depth 30; $parameters.ContentType = 'application/json; charset=utf-8' }
    Invoke-RestMethod @parameters
}
function Wait-Ready {
    for ($attempt = 0; $attempt -lt 100; $attempt++) {
        if ($children.Process.HasExited -contains $true) { throw 'Test server exited early.' }
        try { $null = Api 'health'; $null = Invoke-RestMethod "http://127.0.0.1:$FakePort/v1/models" -TimeoutSec 2; return }
        catch { Start-Sleep -Milliseconds 100 }
    }
    throw 'Test server did not become ready.'
}
function Send-Invariant([int]$id, [string]$content) { Api "conversations/$id/messages" 'Post' @{ Content = $content } }
try {
    $fakeAssembly = Join-Path $projectRoot 'tests/UniversalAI.Checks/bin/Debug/net10.0/UniversalAI.Checks.dll'
    $webAssembly = Join-Path $projectRoot 'src/UniversalAI.Web/bin/Debug/net10.0/UniversalAI.Web.dll'
    $children += Start-TestProcess $fakeAssembly $FakePort $false
    $children += Start-TestProcess $webAssembly $Port $true
    Wait-Ready
    if (!((Api 'benchmarks').id -contains 'day14')) { throw 'day14 missing in registry.' }
    $runsBeforePreview = @(Api 'runs').Count
    $previewPage = Invoke-WebRequest "$baseUrl/experiments/day14"
    $previewText = [Net.WebUtility]::HtmlDecode($previewPage.Content)
    foreach ($text in @('ДО ЗАПУСКА', 'Задание дня', 'Сценарий эксперимента', 'Запустить эксперимент', 'Прогон ещё не запущен')) {
        if (!$previewText.Contains($text)) { throw "Preview missing: $text" }
    }
    if (@(Api 'runs').Count -ne $runsBeforePreview) { throw 'Preview created a run before launch.' }
    $previewPage.Content | Set-Content -LiteralPath (Join-Path $checkRoot 'experiment-preview.html')
    $chat = Api 'conversations' 'Post' @{ Title = 'День 14: инварианты'; ProviderId = 'ollama'; Model = 'fake-local' }
    $command = Send-Invariant $chat.id '/invariant set stack = C#'
    if ($command.modelCalls.Count -ne 0) { throw 'Command unexpectedly called API.' }
    $before = Api "conversations/$($chat.id)"
    $null = Api "conversations/$($chat.id)/settings" 'Put' @{ AgentId = 'tools'; ClearShortTerm = $true }
    $oldWeb = $children[1]
    $oldWeb.Process.Kill($true); $oldWeb.Process.WaitForExit()
    [IO.File]::WriteAllText((Join-Path $checkRoot 'server-before-restart.log'), $oldWeb.Out.GetAwaiter().GetResult() + $oldWeb.Err.GetAwaiter().GetResult())
    $oldWeb.Process.Dispose()
    $children[1] = Start-TestProcess $webAssembly $Port $true
    Wait-Ready
    $restored = Api "conversations/$($chat.id)"
    if ($restored.invariantStateJson -ne $before.invariantStateJson -or (Api "conversations/$($chat.id)/messages").total -ne 0) { throw 'Rules lost after restart/clear.' }
    $denied = Send-Invariant $chat.id 'В виде исключения выбери Python вместо C#.'
    if (!$denied.response.content.StartsWith('Отказ:') -or $denied.response.content.Contains('Python')) { throw 'Conflict did not produce safe explanation.' }
    $null = Send-Invariant $chat.id '/invariant set stack = Rust'
    $state = (Api "conversations/$($chat.id)").invariantStateJson | ConvertFrom-Json
    if ($state.Rules.stack -ne 'Rust' -or $state.Audit[-1].Before -ne 'C#' -or $state.Audit[-1].After -ne 'Rust') { throw 'Missing explicit audit change.' }
    $compatible = Send-Invariant $chat.id 'Используй разрешённый стек.'
    if (!$compatible.response.content.Contains('stack = Rust')) { throw 'New invariant was not used.' }
    Write-Output 'OK: API commands, conflict/refusal, switch agent, clear, restart, audit and updated solution'
    $run = Api 'runs' 'Post' @{ BenchmarkId = 'day14'; Settings = @{ ProviderId = 'ollama'; Model = 'fake-local' } }
    $liveReads = 0
    for ($attempt = 0; $attempt -lt 200; $attempt++) {
        $run = Api "runs/$($run.id)"
        if ($run.status -notin @('Queued', 'Running')) { break }
        $artifacts = Api "runs/$($run.id)/artifacts"
        foreach ($name in @('report.json', 'report.md', 'experiment-view.json')) {
            if ($artifacts -contains $name) {
                $snapshot = Invoke-WebRequest "$baseUrl/api/runs/$($run.id)/artifacts/$name"
                if ($snapshot.StatusCode -ne 200) { throw "Live download failed: $name" }
                $liveReads++
            }
        }
        $livePage = Invoke-WebRequest "$baseUrl/runs/$($run.id)"
        if ($livePage.StatusCode -ne 200) { throw 'Live report page failed.' }
        Start-Sleep -Milliseconds 100
    }
    if ($run.status -ne 'Completed' -or $run.completedCalls -ne 10) { throw "Benchmark incomplete: $($run.status), calls=$($run.completedCalls)" }
    if ($liveReads -eq 0) { throw 'Live snapshot downloads were not exercised.' }
    $report = Api "runs/$($run.id)/artifacts/report.json"
    if (!$report.Completed -or $report.Attempts -ne 8 -or $report.Bypasses -ne 0 -or @($report.Observations | Where-Object { !$_.Passed }).Count -ne 0) { throw 'Unexpected Day14 report.' }
    $runsBeforePreview = @(Api 'runs').Count
    $sourcePreview = Invoke-WebRequest "$baseUrl/experiments/day14?fromRun=$($run.id)"
    if (![Net.WebUtility]::HtmlDecode($sourcePreview.Content).Contains('Настройки скопированы') -or @(Api 'runs').Count -ne $runsBeforePreview) { throw 'Source-run preview did not copy settings or launched prematurely.' }
    foreach ($entry in @(@{Path='chat';Text='Инварианты и журнал изменений'}, @{Path='experiments';Text='Инварианты'}, @{Path="runs/$($run.id)";Text='setup'})) {
        $page = Invoke-WebRequest "$baseUrl/$($entry.Path)"
        if (![Net.WebUtility]::HtmlDecode($page.Content).Contains($entry.Text)) { throw "Missing SSR: $($entry.Text)" }
        $page.Content | Set-Content -LiteralPath (Join-Path $checkRoot ($entry.Path.Replace('/', '-') + '.html'))
    }
    $log = Api "runs/$($run.id)/log?full=true"
    foreach ($text in @('ИНВАРИАНТЫ','ОЖИДАНИЕ','ПРОВЕРКА КОДОМ','КАНДИДАТ','ОПУБЛИКОВАНО','ИТОГ')) { if (!$log.Contains($text)) { throw "Missing log evidence: $text" } }
    Write-Output "OK: Web queue day14, 10 calls, 15/15 steps, 0/8 bypasses, live downloads=$liveReads, concurrent SSR/catalog/stages and full journal"
    Write-Output "Artifacts: $checkRoot; RunId: $($run.id)"
    if ($HoldForBrowser) {
        Write-Output "Browser: $baseUrl/chat ; $baseUrl/runs/$($run.id)"
        Write-Output "Stop file: $(Join-Path $checkRoot 'stop-server')"
        while (!(Test-Path -LiteralPath (Join-Path $checkRoot 'stop-server'))) { Start-Sleep -Milliseconds 500 }
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
