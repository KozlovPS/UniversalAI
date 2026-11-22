param([int]$Port = 5100, [int]$FakePort = 5101, [switch]$HoldForBrowser)
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$checkRoot = Join-Path $projectRoot ('.local/checks/day15-web-' + [Guid]::NewGuid().ToString('N'))
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
    if (!$web) { $start.Environment['UNIVERSALAI_CHECKS_FAKE_DELAY_MS'] = '500' }
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
function Send-Memory([int]$id, [string]$content) { Api "conversations/$id/messages" 'Post' @{ Content = $content } }
try {
    $children += Start-TestProcess (Join-Path $projectRoot 'tests/UniversalAI.Checks/bin/Debug/net10.0/UniversalAI.Checks.dll') $FakePort $false
    $children += Start-TestProcess (Join-Path $projectRoot 'src/UniversalAI.Web/bin/Debug/net10.0/UniversalAI.Web.dll') $Port $true
    Wait-Ready
    if (!((Api 'benchmarks').id -contains 'day15')) { throw 'day15 missing.' }
    $beforeRuns = @(Api 'runs').Count
    $preview = Invoke-WebRequest "$baseUrl/experiments/day15"
    if (![Net.WebUtility]::HtmlDecode($preview.Content).Contains('Обновление памяти') -or @(Api 'runs').Count -ne $beforeRuns) { throw 'Preview failed or created a run.' }
    $chat = Api 'conversations' 'Post' @{ Title = 'Обновляемая память'; ProviderId = 'ollama'; Model = 'fake-local'; AgentId = 'evolving' }
    foreach ($value in @('OLD', 'NEW')) {
        $answer = Send-Memory $chat.id ('/remember ' + (ConvertTo-Json @{ Key = 'deadline'; Value = $value; Policy = 'replace'; WrittenAt = $(if ($value -eq 'OLD') { '2026-11-22T09:00:00Z' } else { '2026-11-22T10:00:00Z' }) } -Compress))
        if ($answer.modelCalls.Count -ne 0) { throw 'Write called model.' }
    }
    $null = Send-Memory $chat.id '/remember {"Key":"temporary","Value":"TASK_ONLY","Policy":"task"}'
    $null = Api "conversations/$($chat.id)/settings" 'Put' @{ WorkingMemoryJson = '{"scratch":"WORKING_ONLY"}' }
    $branch = Api "conversations/$($chat.id)/branch" 'Post' @{ Title = 'Ветка' }
    $null = Send-Memory $chat.id '/remember complete'
    $info = Api "conversations/$($chat.id)"
    $state = $info.evolvingMemoryJson | ConvertFrom-Json
    if ($info.workingMemoryJson -ne '{}' -or $state.Current.deadline.Value -ne 'NEW' -or @($state.Temporary.PSObject.Properties).Count -ne 0) { throw 'Cleanup lost long-term data or retained task data.' }
    $answer = Send-Memory $chat.id 'Каково актуальное значение ключа deadline? Верни только значение.'
    if ($answer.response.content -ne 'NEW') { throw 'Updated value not used.' }
    $oldWeb = $children[1]
    $oldWeb.Process.Kill($true); $oldWeb.Process.WaitForExit()
    [IO.File]::WriteAllText((Join-Path $checkRoot 'server-before-restart.log'), $oldWeb.Out.GetAwaiter().GetResult() + $oldWeb.Err.GetAwaiter().GetResult())
    $oldWeb.Process.Dispose()
    $children[1] = Start-TestProcess (Join-Path $projectRoot 'src/UniversalAI.Web/bin/Debug/net10.0/UniversalAI.Web.dll') $Port $true
    Wait-Ready
    $reloaded = (Api "conversations/$($chat.id)").evolvingMemoryJson | ConvertFrom-Json
    if ($reloaded.Current.deadline.Value -ne 'NEW' -or $reloaded.Audit[1].Before -ne 'OLD' -or $reloaded.Audit[1].After -ne 'NEW') { throw 'Restart lost memory/audit.' }
    $branched = (Api "conversations/$($branch.id)").evolvingMemoryJson | ConvertFrom-Json
    if ($branched.Temporary.temporary.Value -ne 'TASK_ONLY') { throw 'Branch was mutated.' }
    $run = Api 'runs' 'Post' @{ BenchmarkId = 'day15'; Settings = @{ ProviderId = 'ollama'; Model = 'fake-local' } }
    $reads = 0
    for ($attempt = 0; $attempt -lt 200; $attempt++) {
        $run = Api "runs/$($run.id)"
        if ($run.status -notin @('Queued', 'Running')) { break }
        $artifacts = Api "runs/$($run.id)/artifacts"
        foreach ($name in @('report.json', 'experiment-view.json', 'report.md')) {
            if ($artifacts -contains $name) { $null = Invoke-WebRequest "$baseUrl/api/runs/$($run.id)/artifacts/$name"; $reads++ }
        }
        $null = Invoke-WebRequest "$baseUrl/runs/$($run.id)"
        Start-Sleep -Milliseconds 100
    }
    $report = Api "runs/$($run.id)/artifacts/report.json"
    if ($reads -eq 0) { throw 'Concurrent artifact downloads were not exercised.' }
    if ($run.status -ne 'Completed' -or $run.completedCalls -ne 13 -or !$report.Completed -or !$report.CleanupPassed -or $report.StaleAnswers -ne 0 -or $report.PromptLeaks -ne 0 -or @($report.Observations | Where-Object { !$_.Passed }).Count -ne 0) { throw 'Unexpected benchmark result.' }
    $page = Invoke-WebRequest "$baseUrl/runs/$($run.id)"
    $decoded = [Net.WebUtility]::HtmlDecode($page.Content)
    $page.Content | Set-Content (Join-Path $checkRoot 'run.html')
    if ($page.StatusCode -ne 200 -or !$decoded.Contains('late-write')) { throw 'Missing recorded stages in run page.' }
    $chatPage = Invoke-WebRequest "$baseUrl/chat"
    if (![Net.WebUtility]::HtmlDecode($chatPage.Content).Contains('Обновляемая память')) { throw 'Agent is missing from chat selector.' }
    $log = Api "runs/$($run.id)/log?full=true"
    foreach ($word in @('СОСТАВ КОНТЕКСТА', 'ОЖИДАНИЕ', 'ОТВЕТ', 'ИТОГ')) { if (!$log.Contains($word)) { throw "Missing log: $word" } }
    Write-Output "OK: Day15 API memory/cleanup/branch/restart, preview, queue, 13 calls, 26/26 observations, summary and live downloads=$reads"
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
