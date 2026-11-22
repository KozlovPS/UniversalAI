using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.Options;

namespace UniversalAI.Web;

public sealed class WorkspaceAccess(IConfiguration configuration)
{
    public string? Token => Environment.GetEnvironmentVariable("UNIVERSALAI_SERVER_TOKEN") ?? configuration["Server:AccessToken"];
    public bool Matches(string candidate) => !string.IsNullOrWhiteSpace(Token) &&
        CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(candidate)), SHA256.HashData(Encoding.UTF8.GetBytes(Token)));
    public static ClaimsPrincipal Principal(string scheme) => new(new ClaimsIdentity([new Claim(ClaimTypes.Name, "workspace-owner")], scheme));
}

public sealed class WorkspaceTokenHandler(IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger, UrlEncoder encoder, WorkspaceAccess access) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        bool local = Context.Connection.RemoteIpAddress is { } ip && IPAddress.IsLoopback(ip);
        string authorization = Request.Headers.Authorization.ToString();
        bool accepted = string.IsNullOrWhiteSpace(access.Token) ? local :
            authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) && access.Matches(authorization[7..]);
        return Task.FromResult(accepted
            ? AuthenticateResult.Success(new AuthenticationTicket(WorkspaceAccess.Principal(Scheme.Name), Scheme.Name))
            : AuthenticateResult.Fail("Требуется токен доступа к рабочему пространству."));
    }
}

public static class WorkspaceAuthentication
{
    public static void MapWorkspaceAuthentication(this WebApplication app)
    {
        app.MapPost("/auth/login", async (HttpContext context, WorkspaceAccess access, IAntiforgery antiforgery) =>
        {
            await antiforgery.ValidateRequestAsync(context);
            var form = await context.Request.ReadFormAsync();
            if (!access.Matches(form["token"].ToString())) return Results.LocalRedirect("/signin?error=1");
            await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,
                WorkspaceAccess.Principal(CookieAuthenticationDefaults.AuthenticationScheme));
            return Results.LocalRedirect("/");
        }).AllowAnonymous();
        app.MapPost("/auth/logout", async (HttpContext context, IAntiforgery antiforgery) =>
        {
            await antiforgery.ValidateRequestAsync(context);
            await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.LocalRedirect("/signin");
        });
    }
}
