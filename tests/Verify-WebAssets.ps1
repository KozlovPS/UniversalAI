param(
    [string]$AssemblyPath = 'src/UniversalAI.Web/bin/Release/net10.0/UniversalAI.Web.dll',
    [int]$Port = 5087,
    [ValidateSet('Production', 'Development')]
    [string]$Environment = 'Production'
)

$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$assemblyFile = [IO.Path]::GetFullPath((Join-Path $projectRoot $AssemblyPath))
if (!(Test-Path -LiteralPath $assemblyFile)) { throw "Build the web project first: $assemblyFile" }
$checkDirectory = Join-Path $projectRoot ('.local/checks/web-assets-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $checkDirectory -Force | Out-Null
$start = [Diagnostics.ProcessStartInfo]::new('dotnet')
$start.UseShellExecute = $false
$start.CreateNoWindow = $true
$start.RedirectStandardOutput = $true
$start.RedirectStandardError = $true
$start.WorkingDirectory = Split-Path -Parent $assemblyFile
$start.ArgumentList.Add($assemblyFile)
$start.ArgumentList.Add('--urls')
$start.ArgumentList.Add("http://127.0.0.1:$Port")
$start.Environment['ASPNETCORE_ENVIRONMENT'] = $Environment
$start.Environment['DOTNET_ENVIRONMENT'] = $Environment
$start.Environment['UNIVERSALAI_DATA_DIR'] = $checkDirectory
$start.Environment['UNIVERSALAI_SERVER_TOKEN'] = 'asset-check-only'
$serverProcess = [Diagnostics.Process]::Start($start)
$stdout = $serverProcess.StandardOutput.ReadToEndAsync()
$stderr = $serverProcess.StandardError.ReadToEndAsync()
try {
    $ready = $false
    for ($attempt = 0; $attempt -lt 100; $attempt++) {
        if ($serverProcess.HasExited) { throw "Server exited: $($stderr.GetAwaiter().GetResult()) $($stdout.GetAwaiter().GetResult())" }
        try {
            $health = Invoke-WebRequest "http://127.0.0.1:$Port/api/health" -Headers @{ Authorization = 'Bearer asset-check-only' } -TimeoutSec 2
            if ($health.StatusCode -eq 200) { $ready = $true; break }
        }
        catch { Start-Sleep -Milliseconds 100 }
    }
    if (!$ready) { throw 'The test server did not become ready.' }
    foreach ($asset in @('/app.css', '/_framework/blazor.web.js')) {
        $response = Invoke-WebRequest "http://127.0.0.1:$Port$asset" -TimeoutSec 10
        if ($response.StatusCode -ne 200 -or $response.RawContentLength -lt 100) {
            throw "Missing or empty asset: $asset"
        }
        if ($asset.EndsWith('.js') -and $response.Headers['Content-Type'] -notmatch 'javascript') {
            throw "Incorrect JavaScript content type: $($response.Headers['Content-Type'])"
        }
        Write-Output "OK: $Environment $asset ($($response.RawContentLength) bytes)"
    }
    # Browser interactivity authenticates with its cookie, just like the real UI.
    $signIn = Invoke-WebRequest "http://127.0.0.1:$Port/signin" -SessionVariable browserSession
    $antiforgery = [regex]::Match($signIn.Content, 'name="__RequestVerificationToken"[^>]*value="([^"]+)"')
    if (!$antiforgery.Success) { throw 'Missing login antiforgery token.' }
    $null = Invoke-WebRequest "http://127.0.0.1:$Port/auth/login" -Method Post -WebSession $browserSession -Body @{
        token = 'asset-check-only'; __RequestVerificationToken = $antiforgery.Groups[1].Value
    }
    $negotiation = Invoke-RestMethod "http://127.0.0.1:$Port/_blazor/negotiate?negotiateVersion=1" -Method Post -Body '' -WebSession $browserSession
    if (!$negotiation.connectionToken -or !$negotiation.availableTransports) { throw 'Blazor negotiation failed.' }
    Write-Output "OK: $Environment Blazor connection negotiation"
}
finally {
    if (!$serverProcess.HasExited) { $serverProcess.Kill($true); $serverProcess.WaitForExit() }
    [IO.File]::WriteAllText((Join-Path $checkDirectory 'stdout.log'), $stdout.GetAwaiter().GetResult())
    [IO.File]::WriteAllText((Join-Path $checkDirectory 'stderr.log'), $stderr.GetAwaiter().GetResult())
    $serverProcess.Dispose()
}
