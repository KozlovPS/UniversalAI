param([int]$Port = 5102, [int]$FakePort = 5103, [switch]$HoldForBrowser)
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$checkRoot = Join-Path $projectRoot ('.local/checks/course-web-' + [Guid]::NewGuid().ToString('N'))
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
    if (!$web) { $start.Environment['UNIVERSALAI_CHECKS_FAKE_DELAY_MS'] = '50' }
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
try {
    $webAssembly = Join-Path $projectRoot 'src/UniversalAI.Web/bin/Debug/net10.0/UniversalAI.Web.dll'
    $children += Start-TestProcess (Join-Path $projectRoot 'tests/UniversalAI.Checks/bin/Debug/net10.0/UniversalAI.Checks.dll') $FakePort $false
    $children += Start-TestProcess $webAssembly $Port $true
    Wait-Ready
    $expected = @(1..15 | ForEach-Object { "day$_" })
    $definitions = Api 'benchmarks'
    if (($definitions.id -join ',') -ne ($expected -join ',')) { throw 'API benchmarks are not ordered numerically.' }
    $catalog = Invoke-WebRequest "$baseUrl/experiments"
    $options = [regex]::Matches($catalog.Content, '<option value="(day\d+)"').Groups | Where-Object Name -eq 1 | ForEach-Object Value
    if (($options -join ',') -ne ($expected -join ',')) { throw 'HTML selector order differs from API.' }
    $before = @(Api 'runs').Count
    foreach ($day in 1..15) {
        $page = Invoke-WebRequest "$baseUrl/experiments/day$day"
        $decoded = [Net.WebUtility]::HtmlDecode($page.Content)
        if (!$decoded.Contains("День $day.") -or !$decoded.Contains('Результат:') -or !$decoded.Contains('SHA256')) { throw "Missing full source preview day$day" }
        $page.Content | Set-Content (Join-Path $checkRoot "preview-day$day.html")
    }
    if (@(Api 'runs').Count -ne $before) { throw 'Preview created runs.' }
    function Send-Chat([int]$id, [string]$text) { Api "conversations/$id/messages" 'Post' @{ Content = $text } }
    $conversation = Api 'conversations' 'Post' @{ Title = 'Перезапуск процесса'; ProviderId = 'ollama'; Model = 'fake-local'; AgentId = 'tools' }
    $null = Api "conversations/$($conversation.id)/settings" 'Put' @{ CompressionEnabled = $false }
    $null = Send-Chat $conversation.id 'Запомни код проекта МАЯК-731. Подтверди коротко.'
    $toolAnswer = Send-Chat $conversation.id 'Вычисли 17 * 19 через calculate. Верни только число.'
    if ($toolAnswer.response.content -ne '323' -or $toolAnswer.toolRounds -ne 1) { throw 'Tool before restart failed.' }
    $messages = Api "conversations/$($conversation.id)/messages"
    $oldWeb = $children[1]
    $oldWeb.Process.Kill($true); $oldWeb.Process.WaitForExit()
    [IO.File]::WriteAllText((Join-Path $checkRoot 'server-before-restart.log'), $oldWeb.Out.GetAwaiter().GetResult() + $oldWeb.Err.GetAwaiter().GetResult())
    $oldWeb.Process.Dispose()
    $children[1] = Start-TestProcess $webAssembly $Port $true
    Wait-Ready
    $restored = Api "conversations/$($conversation.id)/messages"
    if (($messages | ConvertTo-Json -Depth 25 -Compress) -ne ($restored | ConvertTo-Json -Depth 25 -Compress)) { throw 'Process restart changed saved messages/tool results.' }
    $continued = Send-Chat $conversation.id 'Какой код проекта я называл и какой результат calculate был до перезапуска? Инструменты повторно не вызывай. Верни код и число.'
    if (!$continued.response.content.Contains('МАЯК-731') -or !$continued.response.content.Contains('323') -or $continued.toolRounds -ne 0) { throw 'Continuation after actual process restart failed.' }
    foreach ($day in @(1,2,6,7,8)) {
        $run = Api 'runs' 'Post' @{ BenchmarkId = "day$day"; Settings = @{ ProviderId = 'ollama'; Model = 'fake-local'; ContextWindowTokens = 1024 } }
        for ($attempt = 0; $attempt -lt 300; $attempt++) {
            $run = Api "runs/$($run.id)"
            if ($run.status -notin @('Queued','Running')) { break }
            Start-Sleep -Milliseconds 100
        }
        $report = Api "runs/$($run.id)/artifacts/report.json"
        if ($run.status -ne 'Completed' -or !$report.Completed) { throw "day$day did not finish: $($run.error)" }
        if ($day -ne 2 -and @($report.Observations | Where-Object { !$_.Passed }).Count -gt 0) { throw "day$day checks failed." }
        $material = Api "runs/$($run.id)/artifacts/experiment-materials.json"
        if (!$material.Assignment.Contains("День $day.") -or !$material.Assignment.Contains('Результат:')) { throw 'Saved assignment is incomplete.' }
        $page = Invoke-WebRequest "$baseUrl/runs/$($run.id)"
        if ($page.StatusCode -ne 200) { throw 'Run page failed.' }
        Write-Output "OK: day$day, RunId=$($run.id), calls=$($run.completedCalls), checks=$(@($report.Observations | Where-Object Passed).Count)/$($report.Observations.Count)"
    }
    Write-Output "OK: day1–day15 API/HTML numeric order; all full assignment previews; no preview calls; actual server process restart with tools; five new queue benchmarks."
    Write-Output "Artifacts: $checkRoot"
    if ($HoldForBrowser) {
        Write-Output "Browser: $baseUrl/experiments ; $baseUrl/runs/$($run.id)"
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
