param(
    [Parameter(Mandatory)][string]$RunDirectory,
    [int]$Port = 5088,
    [string]$WebDirectory = 'src/UniversalAI.Web/bin/Debug/net10.0'
)
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$runSource = [IO.Path]::GetFullPath($RunDirectory)
$runId = Split-Path -Leaf $runSource
if ($runId -notmatch '^[a-f0-9]{32}$') { throw 'Expected a recorded RunId directory.' }
$checkDirectory = Join-Path $projectRoot ('.local/checks/day11-web-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path (Join-Path $checkDirectory 'runs') -Force | Out-Null
Copy-Item -LiteralPath $runSource -Destination (Join-Path $checkDirectory 'runs') -Recurse
$start = [Diagnostics.ProcessStartInfo]::new('dotnet')
$start.UseShellExecute = $false
$start.CreateNoWindow = $true
$start.RedirectStandardOutput = $true
$start.RedirectStandardError = $true
$start.WorkingDirectory = [IO.Path]::GetFullPath((Join-Path $projectRoot $WebDirectory))
$start.ArgumentList.Add((Join-Path $start.WorkingDirectory 'UniversalAI.Web.dll'))
$start.ArgumentList.Add('--urls')
$start.ArgumentList.Add("http://127.0.0.1:$Port")
$start.Environment['ASPNETCORE_ENVIRONMENT'] = 'Production'
$start.Environment['UNIVERSALAI_DATA_DIR'] = $checkDirectory
$start.Environment['UNIVERSALAI_SERVER_TOKEN'] = 'day11-test-only'
$start.Environment['UNIVERSALAI_Logging__EventLog__LogLevel__Default'] = 'None'
$process = [Diagnostics.Process]::Start($start)
$stdout = $process.StandardOutput.ReadToEndAsync()
$stderr = $process.StandardError.ReadToEndAsync()
$base = "http://127.0.0.1:$Port"
$headers = @{ Authorization = 'Bearer day11-test-only' }
function Check($condition, $message) { if (!$condition) { throw $message }; Write-Output "OK: $message" }
try {
    $ready = $false
    for ($attempt = 0; $attempt -lt 100; $attempt++) {
        if ($process.HasExited) { throw "Server exited: $($stderr.GetAwaiter().GetResult())" }
        try { $null = Invoke-RestMethod "$base/api/health" -Headers $headers -TimeoutSec 2; $ready = $true; break }
        catch { Start-Sleep -Milliseconds 100 }
    }
    Check $ready 'Day-11 test server ready with isolated data'
    $catalog = Invoke-RestMethod "$base/api/benchmarks" -Headers $headers
    Check (@($catalog | Where-Object id -eq 'day11').Count -eq 1) 'day11 present in web/API registry'
    $chat = Invoke-RestMethod "$base/api/conversations" -Headers $headers -Method Post -ContentType 'application/json' -Body '{"title":"Day11 web","providerId":"ollama","model":"fake-local"}'
    $settings = @{ memoryStrategy = 'Layered'; workingMemoryJson = '{"ticket":"LAB-118"}'; longTermMemoryJson = '{"owner":"Марина"}' } | ConvertTo-Json
    $chat = Invoke-RestMethod "$base/api/conversations/$($chat.id)/settings" -Headers $headers -Method Put -ContentType 'application/json; charset=utf-8' -Body ([Text.Encoding]::UTF8.GetBytes($settings))
    Check ($chat.workingMemoryJson.Contains('LAB-118') -and $chat.factsJson.Contains('Марина')) 'Explicit layer writes survive HTTP serialization'
    $bad = Invoke-WebRequest "$base/api/conversations/$($chat.id)/settings" -Headers $headers -Method Put -ContentType 'application/json' -Body '{"workingMemoryJson":"[]"}' -SkipHttpErrorCheck
    Check ($bad.StatusCode -eq 400) 'Invalid layer JSON rejected by API'
    $login = Invoke-WebRequest "$base/signin" -SessionVariable session
    $token = [regex]::Match($login.Content, 'name="__RequestVerificationToken"[^>]*value="([^"]+)"').Groups[1].Value
    $null = Invoke-WebRequest "$base/auth/login" -Method Post -WebSession $session -Body @{ token = 'day11-test-only'; __RequestVerificationToken = $token }
    $html = [Net.WebUtility]::HtmlDecode((Invoke-WebRequest "$base/chat" -WebSession $session).Content)
    Check ($html.Contains('Слои памяти') -and $html.Contains('Сохранить рабочую память') -and $html.Contains('Очистить долговременную память')) 'Chat renders separate layer editors and clear actions'
    $html = [Net.WebUtility]::HtmlDecode((Invoke-WebRequest "$base/runs/$runId" -WebSession $session).Content)
    Check ($html.Contains('Демонстрация эксперимента') -and $html.Contains('Память и этапы') -and $html.Contains('ПЛАВНЫЙ ПРОСМОТР') -and $html.Contains('Режим записи')) 'Recorded run renders staged playback, memory tab and recording mode'
    [IO.File]::WriteAllText((Join-Path $checkDirectory 'run-page.html'), $html)
    $cleared = Invoke-RestMethod "$base/api/conversations/$($chat.id)/settings" -Headers $headers -Method Put -ContentType 'application/json' -Body '{"clearShortTerm":true,"workingMemoryJson":"{}"}'
    Check ($cleared.workingMemoryJson -eq '{}' -and $cleared.factsJson.Contains('Марина')) 'Clearing short/working layers via HTTP preserves long-term facts'
    Check ($html.Contains('Итоговый отчёт') -and $html.Contains('Задание дня') -and $html.Contains('Сценарий эксперимента')) 'Report, assignment and scenario tabs are available'
    foreach ($asset in @('experiment-screen.css', 'experiment-screen.js', 'workbench.css', 'experiment-documents.css')) {
        $assetResponse = Invoke-WebRequest "$base/$asset"
        Check ($assetResponse.StatusCode -eq 200 -and $assetResponse.RawContentLength -gt 50) "Demonstration asset available: $asset"
    }
    $fixtureLog = Join-Path $checkDirectory "runs/$runId/output.log"
    [IO.File]::WriteAllText($fixtureLog, "BEGIN_FULL_LOG`n" + ('x' * 70000) + "`nВОПРОС: <script>alert('unsafe')</script>`nEND_FULL_LOG")
    $fullLog = (Invoke-WebRequest "$base/api/runs/$runId/log?full=true" -Headers $headers).Content
    Check ($fullLog.Contains('BEGIN_FULL_LOG') -and $fullLog.Contains('END_FULL_LOG')) 'Full log includes content older than 64 KB'
    $tailLog = (Invoke-WebRequest "$base/api/runs/$runId/log" -Headers $headers).Content
    Check (!$tailLog.Contains('BEGIN_FULL_LOG') -and $tailLog.Contains('END_FULL_LOG')) 'Default API log tail remains backward compatible'
    $html = [Net.WebUtility]::HtmlDecode((Invoke-WebRequest "$base/runs/$runId" -WebSession $session).Content)
    Check ($html.Contains('Исходный журнал') -and $html.Contains('Разбор')) 'Demonstration page provides structured and complete raw journal views'
    $rawHtml = (Invoke-WebRequest "$base/runs/$runId" -WebSession $session).Content
    Check (!$rawHtml.Contains("<script>alert('unsafe')</script>")) 'Log contents are HTML encoded'
    $download = Invoke-WebRequest "$base/logs/$runId" -WebSession $session
    Check ($download.Content.Contains('BEGIN_FULL_LOG')) 'Full raw log can be downloaded'
    $history = [Net.WebUtility]::HtmlDecode((Invoke-WebRequest "$base/experiments" -WebSession $session).Content)
    Check ($history.Contains('Удалить')) 'History renders delete action'
    $invalidDelete = Invoke-WebRequest "$base/api/runs/not-a-run" -Headers $headers -Method Delete -SkipHttpErrorCheck
    Check ($invalidDelete.StatusCode -eq 400) 'Delete API rejects malformed RunId'
    $activeId = [Guid]::NewGuid().ToString('N')
    $activeDirectory = Join-Path $checkDirectory "runs/$activeId"
    New-Item -ItemType Directory -Path $activeDirectory | Out-Null
    $activeFixture = Get-Content (Join-Path $checkDirectory "runs/$runId/run.json") -Raw | ConvertFrom-Json
    $activeFixture.id = $activeId; $activeFixture.status = 'Running'
    $activeFixture | ConvertTo-Json -Depth 30 | Set-Content (Join-Path $activeDirectory 'run.json') -Encoding utf8
    $activeDelete = Invoke-WebRequest "$base/api/runs/$activeId" -Headers $headers -Method Delete -SkipHttpErrorCheck
    Check ($activeDelete.StatusCode -eq 409 -and (Test-Path -LiteralPath $activeDirectory)) 'Delete API preserves an active run and returns conflict'
    $deleted = Invoke-WebRequest "$base/api/runs/$runId" -Headers $headers -Method Delete
    Check ($deleted.StatusCode -eq 204 -and !(Test-Path -LiteralPath (Join-Path $checkDirectory "runs/$runId"))) 'Delete API removes only the isolated fixture and its artifacts'
    $missing = Invoke-WebRequest "$base/api/runs/$runId" -Headers $headers -SkipHttpErrorCheck
    Check ($missing.StatusCode -eq 404) 'Deleted run is no longer accessible'
    Write-Output "Artifacts: $checkDirectory"
}
finally {
    if (!$process.HasExited) { $process.Kill($true); $process.WaitForExit() }
    [IO.File]::WriteAllText((Join-Path $checkDirectory 'stdout.log'), $stdout.GetAwaiter().GetResult())
    [IO.File]::WriteAllText((Join-Path $checkDirectory 'stderr.log'), $stderr.GetAwaiter().GetResult())
    $process.Dispose()
}
