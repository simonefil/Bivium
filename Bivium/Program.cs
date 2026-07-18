using Bivium.Components;
using Bivium.Controllers;
using Bivium.Models;
using Bivium.Services;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;

// Parse port: environment variable > --port argument > default 5000
int port = 5000;
string envPort = Environment.GetEnvironmentVariable("BIVIUM_PORT");
if (envPort != null)
{
    int.TryParse(envPort, out port);
}
else
{
    for (int i = 0; i < args.Length; i++)
    {
        if (args[i] == "--port" && i + 1 < args.Length)
        {
            int.TryParse(args[i + 1], out port);
        }
    }
}

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

// Set listening URL
builder.WebHost.UseUrls("http://0.0.0.0:" + port);

// Persistent DataProtection keys keep authentication cookies valid across restarts
string dataPath = Environment.GetEnvironmentVariable("BIVIUM_DATA_DIR");
if (string.IsNullOrWhiteSpace(dataPath))
{
    dataPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".bivium");
}

string dataProtectionPath = Path.Combine(dataPath, "DataProtectionKeys");
Directory.CreateDirectory(dataProtectionPath);
builder.Services.AddDataProtection().SetApplicationName("Bivium").PersistKeysToFileSystem(new DirectoryInfo(dataProtectionPath));

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(ConfigureAuthenticationCookie);

builder.Services.AddAuthorization();
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddHttpContextAccessor();

// Register configuration
builder.Services.Configure<CommanderSettings>(builder.Configuration.GetSection("CommanderSettings"));

// Register services
builder.Services.AddSingleton<AuthenticationService>();
builder.Services.AddSingleton<BiviumWorkspaceService>();
builder.Services.AddSingleton<TerminalRuntimeService>();
builder.Services.AddSingleton<SecurityService>();
builder.Services.AddSingleton<IFileSystemService, FileSystemService>();
builder.Services.AddSingleton<IFileOperationService, FileOperationService>();
builder.Services.AddSingleton<IPermissionService, PermissionService>();
builder.Services.AddSingleton<IArchiveService, ArchiveService>();

builder.Services.AddControllers(ConfigureControllers);
builder.Services.AddRazorComponents().AddInteractiveServerComponents();

WebApplication app = builder.Build();
BiviumWorkspaceService workspaceService = app.Services.GetRequiredService<BiviumWorkspaceService>();
TerminalRuntimeService terminalRuntimeService = app.Services.GetRequiredService<TerminalRuntimeService>();
app.Lifetime.ApplicationStopping.Register(workspaceService.Stop);
app.Lifetime.ApplicationStopping.Register(terminalRuntimeService.Stop);

// Configure the HTTP request pipeline
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}

app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);

app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.MapControllers();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

app.Run();

void ConfigureAuthenticationCookie(CookieAuthenticationOptions options)
{
    options.Cookie.Name = "BiviumAuth";
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Lax;
    options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
    options.ExpireTimeSpan = TimeSpan.FromHours(8);
    options.SlidingExpiration = true;
    options.LoginPath = "/";
    options.AccessDeniedPath = "/";
}

void ConfigureControllers(MvcOptions options)
{
    options.Filters.Add<ConditionalAuthorizeFilter>();
}
