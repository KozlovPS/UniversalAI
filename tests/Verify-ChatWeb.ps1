param(
    [string]$ArtifactsDirectory = '.local/chat-library-build',
    [int]$Port = 5098,
    [int]$FakePort = 5099,
    [switch]$HoldForBrowser
)
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$buildRoot = Join-Path $projectRoot $ArtifactsDirectory
$checkRoot = Join-Path $projectRoot ('.local/checks/chat-web-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $checkRoot -Force | Out-Null
$baseUrl = "http://127.0.0.1:$Port"
$headers = @{ Authorization = 'Bearer chat-check-only' }
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
    $start.Environment['UNIVERSALAI_SERVER_TOKEN'] = 'chat-check-only'
    $start.Environment['UNIVERSALAI_Providers__Default'] = 'ollama'
    $start.Environment['UNIVERSALAI_Providers__Profiles__2__BaseUrl'] = "http://127.0.0.1:$FakePort/v1"
    $start.Environment['UNIVERSALAI_Providers__Profiles__2__DefaultModel'] = 'fake-local'
    $start.Environment['UNIVERSALAI_Logging__EventLog__LogLevel__Default'] = 'None'
    $process = [Diagnostics.Process]::Start($start)
    return @{ Process = $process; Out = $process.StandardOutput.ReadToEndAsync(); Err = $process.StandardError.ReadToEndAsync() }
}
function Api([string]$path, [string]$method = 'Get', $body = $null) {
    $parameters = @{ Uri = "$baseUrl/api/$path"; Method = $method; Headers = $headers; TimeoutSec = 20 }
    if ($null -ne $body) { $parameters.Body = ConvertTo-Json $body -Depth 10; $parameters.ContentType = 'application/json; charset=utf-8' }
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
    $first = Api 'conversations' 'Post' @{ Title = 'Демо: План урока'; ProviderId = 'ollama'; Model = 'fake-local' }
    $null = Api "conversations/$($first.id)/messages" 'Post' @{ Content = 'Составим план урока о слоях памяти.' }
    $second = Api 'conversations' 'Post' @{ Title = 'Демо: Проверка памяти'; ProviderId = 'ollama'; Model = 'fake-local' }
    $null = Api "conversations/$($second.id)/messages" 'Post' @{ Content = 'В этом диалоге обсуждаем рабочую и долговременную память.' }
    $branch = Api "conversations/$($first.id)/branch" 'Post' @{ Title = 'Демо: Независимая ветка' }
    if ((Api 'conversations').Count -ne 3) { throw 'Conversation list is incomplete.' }
    if ((Api "conversations/$($first.id)/messages").items[0].content -notmatch 'план урока') { throw 'Incorrect history.' }
    Write-Output 'OK: List, messages and independent branch use isolated fake-model data'
    $disposable = Api 'conversations' 'Post' @{ Title = 'API deletion fixture'; ProviderId = 'ollama'; Model = 'fake-local' }
    $null = Api "conversations/$($disposable.id)/messages" 'Post' @{ Content = 'Disposable fixture' }
    $denied = Invoke-WebRequest "$baseUrl/api/conversations/$($disposable.id)" -Method Delete -SkipHttpErrorCheck
    if ($denied.StatusCode -ne 401) { throw 'Unauthenticated deletion was not rejected.' }
    $deleted = Invoke-WebRequest "$baseUrl/api/conversations/$($disposable.id)" -Method Delete -Headers $headers
    if ($deleted.StatusCode -ne 204) { throw 'Deletion did not return 204.' }
    foreach ($path in @("conversations/$($disposable.id)", "conversations/$($disposable.id)/messages")) {
        $missing = Invoke-WebRequest "$baseUrl/api/$path" -Headers $headers -SkipHttpErrorCheck
        if ($missing.StatusCode -ne 404) { throw "Deleted resource remains available: $path" }
    }
    $again = Invoke-WebRequest "$baseUrl/api/conversations/$($disposable.id)" -Method Delete -Headers $headers -SkipHttpErrorCheck
    if ($again.StatusCode -ne 404) { throw 'Repeated deletion did not return 404.' }
    if ((Api 'conversations').Count -ne 3 -or (Api "conversations/$($branch.id)/messages").total -ne 2) { throw 'Deletion affected another conversation.' }
    Write-Output 'OK: DELETE 401/204/404; unrelated history and branch preserved'
    $signin = Invoke-WebRequest "$baseUrl/signin" -SessionVariable browserSession
    $antiforgery = [regex]::Match($signin.Content, 'name="__RequestVerificationToken"[^>]*value="([^"]+)"')
    if (!$antiforgery.Success) { throw 'Missing login antiforgery token.' }
    $null = Invoke-WebRequest "$baseUrl/auth/login" -Method Post -WebSession $browserSession -Body @{ token = 'chat-check-only'; __RequestVerificationToken = $antiforgery.Groups[1].Value }
    $page = Invoke-WebRequest "$baseUrl/chat" -WebSession $browserSession
    $page.Content | Set-Content -LiteralPath (Join-Path $checkRoot 'chat-page.html')
    foreach ($text in @('Список диалогов', 'conversation-search', 'chat-library-row', 'chat-settings', 'chat.css')) {
        if ($page.Content -notmatch [regex]::Escape($text) -and [Net.WebUtility]::HtmlDecode($page.Content) -notmatch [regex]::Escape($text)) { throw "Missing chat UI: $text" }
    }
    $css = Invoke-WebRequest "$baseUrl/chat.css"
    if ($css.StatusCode -ne 200 -or $css.RawContentLength -lt 100) { throw 'Missing chat.css' }
    Write-Output 'OK: Authenticated chat SSR, complete library, history and CSS'
    Write-Output "Artifacts: $checkRoot"
    if ($HoldForBrowser) {
        Write-Output "Browser preview: $baseUrl/chat (test token: chat-check-only)"
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
