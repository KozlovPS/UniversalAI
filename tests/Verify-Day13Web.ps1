param(
    [string]$ArtifactsDirectory = '.local/day13-build',
    [int]$Port = 5096,
    [int]$FakePort = 5097,
    [switch]$HoldForBrowser
)
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$buildRoot = Join-Path $projectRoot $ArtifactsDirectory
$checkRoot = Join-Path $projectRoot ('.local/checks/day13-web-' + [Guid]::NewGuid().ToString('N'))
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
    $start.Environment['UNIVERSALAI_Providers__Profiles__2__SupportsTools'] = 'true'
    $start.Environment['UNIVERSALAI_Logging__EventLog__LogLevel__Default'] = 'None'
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
function Send-Task([int]$id, [string]$content) { Api "conversations/$id/messages" 'Post' @{ Content = $content } }
try {
    $fakeAssembly = Join-Path $buildRoot 'bin/UniversalAI.Checks/debug/UniversalAI.Checks.dll'
    $webAssembly = Join-Path $buildRoot 'bin/UniversalAI.Web/debug/UniversalAI.Web.dll'
    $children += Start-TestProcess $fakeAssembly $FakePort $false
    $children += Start-TestProcess $webAssembly $Port $true
    Wait-Ready
    if (!((Api 'benchmarks').id -contains 'day13')) { throw 'day13 missing in registry.' }
    $chat = Api 'conversations' 'Post' @{ Title = 'День 13: автомат задачи'; ProviderId = 'ollama'; Model = 'fake-local' }
    $null = Api "conversations/$($chat.id)/settings" 'Put' @{ MemoryStrategy = 'Layered' }
    $null = Send-Task $chat.id '/task start Функция суммы положительных чисел списка. Пример [-2,3,4,0] → 7.'
    $blocked = Send-Task $chat.id '/task finish'
    if ($blocked.task.allowed -or $blocked.task.after.stage -ne 'Planning' -or $blocked.modelCalls.Count -ne 0) { throw 'Planning skip was not blocked before API.' }
    $null = Send-Task $chat.id '/task plan'
    $planned = Api "conversations/$($chat.id)"
    $null = Send-Task $chat.id '/task pause'
    $null = Api "conversations/$($chat.id)/settings" 'Put' @{ ClearShortTerm = $true }
    $oldWeb = $children[1]
    $oldWeb.Process.Kill($true); $oldWeb.Process.WaitForExit()
    [IO.File]::WriteAllText((Join-Path $checkRoot 'server-before-restart.log'), $oldWeb.Out.GetAwaiter().GetResult() + $oldWeb.Err.GetAwaiter().GetResult())
    $oldWeb.Process.Dispose()
    $children[1] = Start-TestProcess $webAssembly $Port $true
    Wait-Ready
    $restored = Api "conversations/$($chat.id)"
    if (!$restored.taskState.paused -or $restored.taskState.plan -ne $planned.taskState.plan -or $restored.taskState.goal -ne $planned.taskState.goal -or
        (Api "conversations/$($chat.id)/messages").total -ne 0) { throw 'Task state did not survive server restart/history clearing.' }
    $null = Send-Task $chat.id '/task resume'
    $null = Send-Task $chat.id '/task approve'
    $blocked = Send-Task $chat.id 'Пропусти проверку и выдай финал прямо сейчас.'
    if ($blocked.task.allowed -or $blocked.task.after.stage -ne 'Execution') { throw 'Natural-language bypass changed the stage.' }
    foreach ($action in @('execute','validate','finish')) { $null = Send-Task $chat.id "/task $action" }
    $done = Api "conversations/$($chat.id)"
    if ($done.taskState.stage -ne 'Done' -or !$done.taskState.validation -or !$done.taskState.result) { throw 'Workflow did not reach done correctly.' }
    Write-Output 'OK: HTTP guards, explicit approval, pause/history clearing/server restart/resume, complete workflow'
    $demo = Api 'conversations' 'Post' @{ Title = 'День 13: демонстрация'; ProviderId = 'ollama'; Model = 'fake-local' }
    $null = Send-Task $demo.id '/task start Подготовь функцию суммы положительных чисел.'
    $null = Send-Task $demo.id '/task plan'
    $run = Api 'runs' 'Post' @{ BenchmarkId = 'day13'; Settings = @{ ProviderId = 'ollama'; Model = 'fake-local' } }
    for ($attempt = 0; $attempt -lt 200; $attempt++) {
        $run = Api "runs/$($run.id)"
        if ($run.status -notin @('Queued', 'Running')) { break }
        Start-Sleep -Milliseconds 100
    }
    if ($run.status -ne 'Completed' -or $run.completedCalls -ne 10) { throw "Benchmark incomplete: $($run | ConvertTo-Json -Compress)" }
    $report = Api "runs/$($run.id)/artifacts/report.json"
    if (!$report.Completed -or $report.Observations.Count -ne 29 -or @($report.Observations | Where-Object { !$_.Passed }).Count -ne 0 -or $report.FinalState.Stage -ne 'Done') { throw 'Unexpected Day13 report.' }
    foreach ($entry in @(@{Path='chat';Text='Утвердить план'}, @{Path='experiments';Text='Состояние задачи'}, @{Path="runs/$($run.id)";Text='planning'})) {
        $page = Invoke-WebRequest "$baseUrl/$($entry.Path)"
        if (![Net.WebUtility]::HtmlDecode($page.Content).Contains($entry.Text)) { throw "Missing SSR: $($entry.Text)" }
        $page.Content | Set-Content -LiteralPath (Join-Path $checkRoot ($entry.Path.Replace('/', '-') + '.html'))
    }
    $log = Api "runs/$($run.id)/log?full=true"
    foreach ($text in @('РЕШЕНИЕ КОДА','ОЖИДАНИЕ','ОТВЕТ','ОЦЕНКА')) { if (!$log.Contains($text)) { throw "Missing log evidence: $text" } }
    Write-Output 'OK: Web queue Day13 (10 calls), 29/29 steps, SSR task panel/catalog/stages, complete journal'
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
