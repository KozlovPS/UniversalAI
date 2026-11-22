param([int]$Port = 5100, [int]$FakePort = 5101, [switch]$HoldForBrowser)
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$checkRoot = Join-Path $projectRoot ('.local/checks/modules-web-' + [Guid]::NewGuid().ToString('N'))
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
    $chat = Api 'conversations' 'Post' @{ Title = 'Модули диалога'; ProviderId = 'ollama'; Model = 'fake-local' }
    if ($chat.modules.tasks -or $chat.modules.personalization -or $chat.modules.invariants) { throw 'New chat attaches modules.' }
    $plain = Send-Invariant $chat.id 'Привет'
    if ($plain.task -or $plain.modelCalls.Count -ne 1) { throw 'Ordinary chat entered a module.' }
    $page = Invoke-WebRequest "$baseUrl/chat"
    $text = [Net.WebUtility]::HtmlDecode($page.Content)
    if (!$text.Contains('Возможности диалога') -or !$text.Contains('Обычный диалог') -or
        $text.Contains('Цель новой задачи') -or $text.Contains('<h2>Профиль пользователя</h2>')) { throw 'Default chat contains optional editors.' }
    $page.Content | Set-Content -LiteralPath (Join-Path $checkRoot 'plain-chat.html')
    $off = @{ Tasks = $false; Personalization = $false; Invariants = $false }
    $tasks = @{ Tasks = $true; Personalization = $false; Invariants = $false }
    $null = Api "conversations/$($chat.id)/settings" 'Put' @{ Modules = $tasks }
    $null = Send-Invariant $chat.id '/task start Сохрани план после отключения'
    $null = Send-Invariant $chat.id '/task plan'
    $planned = Api "conversations/$($chat.id)"
    $null = Api "conversations/$($chat.id)/settings" 'Put' @{ Modules = $off }
    $plain = Send-Invariant $chat.id 'Просто поговорим'
    if ($plain.task -or (Api "conversations/$($chat.id)").taskState.plan -ne $planned.taskState.plan) { throw 'Detach lost state or still routes task.' }
    $profile = @{ Name = 'Анна'; Tone = 'formal'; LearnFromDialogue = $false } | ConvertTo-Json -Compress
    $null = Api "conversations/$($chat.id)/settings" 'Put' @{ UserProfileJson = $profile; Modules = @{ Personalization = $true } }
    $personal = Send-Invariant $chat.id 'Расскажи про контекст'
    if ($personal.context.profileTokens -le 0) { throw 'Profile was not attached.' }
    $savedProfile = (Api "conversations/$($chat.id)").userProfileJson
    $null = Api "conversations/$($chat.id)/settings" 'Put' @{ Modules = $off }
    $plain = Send-Invariant $chat.id 'Ответ без профиля'
    if ($plain.context.profileTokens -ne 0 -or (Api "conversations/$($chat.id)").userProfileJson -ne $savedProfile) { throw 'Profile detach lost data or still applies.' }
    $null = Send-Invariant $chat.id '/invariant set stack = C#'
    $strict = Send-Invariant $chat.id 'Разрешённое решение'
    if (!$strict.response.content.Contains('stack = C#')) { throw 'Invariant not attached by explicit command.' }
    $null = Api "conversations/$($chat.id)/settings" 'Put' @{ Modules = $off }
    $detached = Api "conversations/$($chat.id)"
    $rules = $detached.invariantStateJson | ConvertFrom-Json
    if ($rules.Rules.stack -ne 'C#' -or $rules.Audit[-1].Key -ne '$module.enabled' -or $rules.Audit[-1].After -ne 'False') { throw 'Invariant detach did not preserve/audit.' }
    $plain = Send-Invariant $chat.id 'Обычный вопрос после инвариантов'
    if ($plain.response.content.Contains('Решение проверено кодом')) { throw 'Strict routing still active.' }
    $oldWeb = $children[1]
    $oldWeb.Process.Kill($true); $oldWeb.Process.WaitForExit()
    [IO.File]::WriteAllText((Join-Path $checkRoot 'server-before-restart.log'), $oldWeb.Out.GetAwaiter().GetResult() + $oldWeb.Err.GetAwaiter().GetResult())
    $oldWeb.Process.Dispose()
    $children[1] = Start-TestProcess $webAssembly $Port $true
    Wait-Ready
    $restored = Api "conversations/$($chat.id)"
    if ($restored.modules.tasks -or $restored.modules.personalization -or $restored.modules.invariants -or $restored.taskState.plan -ne $planned.taskState.plan) { throw 'Restart lost module selection/state.' }
    $null = Api "conversations/$($chat.id)/settings" 'Put' @{ Modules = $tasks }
    $status = Send-Invariant $chat.id '/task status'
    if ($status.task.after.plan -ne $planned.taskState.plan) { throw 'Reattach did not restore task.' }
    $page = Invoke-WebRequest "$baseUrl/chat"
    if (![Net.WebUtility]::HtmlDecode($page.Content).Contains('Утвердить план')) { throw 'Connected task editor missing.' }
    $null = Api "conversations/$($chat.id)/settings" 'Put' @{ Modules = $off }
    $plainChat = Api 'conversations' 'Post' @{ Title = 'Простой разговор'; ProviderId = 'ollama'; Model = 'fake-local' }
    Write-Output 'OK: default simple chat, tasks detach/reattach with state, profile context on/off, invariant audit, server restart and SSR'
    Write-Output "Artifacts: $checkRoot; chat id: $($chat.id)"
    if ($HoldForBrowser) {
        Write-Output "Browser: $baseUrl/chat"
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
