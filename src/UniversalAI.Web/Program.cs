using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using UniversalAI.Benchmarks.Runtime;
using UniversalAI.Configuration;
using UniversalAI.Web;
using UniversalAI.Web.Components;

var builder = WebApplication.CreateBuilder(args);
// Build output resolves framework assets from the SDK/NuGet manifest, including
// local Production runs. Published output has its own wwwroot and no runtime manifest.
string buildAssetsManifest = Path.Combine(AppContext.BaseDirectory,
    $"{builder.Environment.ApplicationName}.staticwebassets.runtime.json");
if (File.Exists(buildAssetsManifest)) builder.WebHost.UseStaticWebAssets();
builder.Configuration.AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.json"), optional: false)
    .AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.local.json"), optional: true)
    .AddEnvironmentVariables("UNIVERSALAI_")
    .AddCommandLine(args);
if (string.IsNullOrWhiteSpace(builder.Configuration["urls"])) builder.WebHost.UseUrls("http://localhost:5080");
builder.Services.AddUniversalAI(builder.Configuration).AddUniversalAIBenchmarks();
builder.Services.AddSingleton<StorageLease>();
builder.Services.AddSingleton<BenchmarkQueue>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<BenchmarkQueue>());
builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddSingleton<WorkspaceAccess>();
var protection = builder.Services.AddDataProtection().SetApplicationName("UniversalAI")
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(new AppPaths(builder.Configuration).Root, "keys")));
if (OperatingSystem.IsWindows()) protection.ProtectKeysWithDpapi();
builder.Services.AddAuthentication("Workspace")
    .AddPolicyScheme("Workspace", null, options => options.ForwardDefaultSelector = context =>
        context.Request.Path.StartsWithSegments("/api") || string.IsNullOrWhiteSpace(context.RequestServices.GetRequiredService<WorkspaceAccess>().Token)
            ? "ApiToken" : CookieAuthenticationDefaults.AuthenticationScheme)
    .AddScheme<AuthenticationSchemeOptions, WorkspaceTokenHandler>("ApiToken", _ => { })
    .AddCookie(options =>
    {
        options.LoginPath = "/signin";
        options.Cookie.Name = "UniversalAI.Workspace";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Strict;
        options.ExpireTimeSpan = TimeSpan.FromHours(12);
        options.Events.OnRedirectToLogin = context =>
        {
            if (context.Request.Path.StartsWithSegments("/api")) context.Response.StatusCode = 401;
            else context.Response.Redirect(context.RedirectUri);
            return Task.CompletedTask;
        };
    });
builder.Services.AddAuthorization(options => options.FallbackPolicy = new AuthorizationPolicyBuilder()
    .RequireAuthenticatedUser().Build());
builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));

var app = builder.Build();
_ = app.Services.GetRequiredService<StorageLease>();
await app.Services.InitializeUniversalAIAsync();
app.Use(async (context, next) =>
{
    try
    {
        if (context.Request.Path.StartsWithSegments("/api") && context.Request.Headers["Sec-Fetch-Site"] == "cross-site")
        {
            context.Response.StatusCode = 403; return;
        }
        await next(context);
    }
    catch (Exception e) when (!context.Response.HasStarted)
    {
        int status = e switch
        {
            KeyNotFoundException => 404,
            ArgumentException or NotSupportedException or System.Text.Json.JsonException => 400,
            HttpRequestException => 502,
            OperationCanceledException => 499,
            InvalidOperationException => 409,
            _ => 500
        };
        app.Logger.LogWarning(e, "Request {Path} failed", context.Request.Path);
        await Results.Problem(statusCode: status, detail: status == 500 ? "Ошибка сервера. Подробности в журнале." : e.Message).ExecuteAsync(context);
    }
});
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();
app.MapStaticAssets().AllowAnonymous();
app.MapWorkspaceApi();
app.MapWorkspaceAuthentication();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
app.Run();

public partial class Program;
