using RustProxy.Server.Components;
using RustProxy.Server.Services;
using RustProxy.Core.Storage;
using SqlSugar;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Components.Authorization;
using RustProxy.Server.Security;

var builder = WebApplication.CreateBuilder(args);
var packageLimits = builder.Configuration.GetSection("PackageLimits").Get<RustProxy.Core.Package.PackageLimits>() ?? new();
packageLimits.Validate();
builder.Services.AddSingleton(packageLimits);
builder.Services.AddControllers();
builder.Services.AddMemoryCache();
builder.Services.AddAuthentication(AdministratorAuthenticationHandler.SchemeName)
    .AddScheme<AuthenticationSchemeOptions, AdministratorAuthenticationHandler>(AdministratorAuthenticationHandler.SchemeName, _ => { });
builder.Services.AddAuthorization(options => options.AddPolicy("Administrator", policy => policy.RequireAuthenticatedUser().RequireRole("Administrator")));
builder.Services.AddCascadingAuthenticationState();
var databasePath = DatabaseStorage.Prepare(builder.Environment.ContentRootPath);
builder.Services.AddSingleton<ISqlSugarClient>(_ => new SqlSugarScope(new ConnectionConfig
{
    ConnectionString = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = databasePath }.ToString(),
    DbType = DbType.Sqlite,
    IsAutoCloseConnection = true
}));
builder.Services.AddHostedService<PackageMetadataInitializer>();
builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddSingleton<PackageCatalog>();

var app = builder.Build();
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();
app.MapControllers();
app.MapStaticAssets();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
app.Run();

/// <summary>Application entry point exposed for integration testing.</summary>
public partial class Program { }
