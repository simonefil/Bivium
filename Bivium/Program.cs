using Bivium.Components;
using Bivium.Models;
using Bivium.Services;
using Microsoft.AspNetCore.DataProtection;

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

// Persist DataProtection keys to survive container restarts
string homePath = Environment.GetEnvironmentVariable("BIVIUM_HOME") ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
string keysDir = Path.Combine(homePath, ".bivium", "keys");
Directory.CreateDirectory(keysDir);
builder.Services.AddDataProtection().SetApplicationName("Bivium").PersistKeysToFileSystem(new DirectoryInfo(keysDir));

// Register configuration
builder.Services.Configure<CommanderSettings>(builder.Configuration.GetSection("CommanderSettings"));

// Register services
builder.Services.AddSingleton<SecurityService>();
builder.Services.AddSingleton<IFileSystemService, FileSystemService>();
builder.Services.AddSingleton<IFileOperationService, FileOperationService>();
builder.Services.AddSingleton<IPermissionService, PermissionService>();
builder.Services.AddSingleton<IArchiveService, ArchiveService>();

builder.Services.AddControllers();
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

WebApplication app = builder.Build();

// Configure the HTTP request pipeline
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}

app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseAntiforgery();

app.MapControllers();
app.UseStaticFiles();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
