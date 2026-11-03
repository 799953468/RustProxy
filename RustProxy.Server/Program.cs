using RustProxy.Server.Components;
using RustProxy.Server.Services;
using RustProxy.Core.Storage;
using SqlSugar;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddControllers();
builder.Services.AddMemoryCache();
Directory.CreateDirectory(Path.Combine(builder.Environment.ContentRootPath, "data"));
builder.Services.AddSingleton<ISqlSugarClient>(_ => new SqlSugarClient(new ConnectionConfig
{
    ConnectionString = $"Data Source={Path.Combine(builder.Environment.ContentRootPath, "data", "rustproxy.db")}",
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
app.UseAntiforgery();
app.MapControllers();
app.MapStaticAssets();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
app.Run();
