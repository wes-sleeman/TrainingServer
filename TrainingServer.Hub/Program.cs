using TrainingServer.Hub.Data;

System.Globalization.CultureInfo.DefaultThreadCurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
Thread.CurrentThread.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;

var builder = WebApplication.CreateBuilder(args);

builder.Configuration.AddJsonFile("wwwroot/appsettings.json");

// Add services to the container.
builder.Services.AddRazorPages();
builder.Services.AddServerSideBlazor();
builder.Services.AddSingleton<ConnectionManager>();
builder.Services.AddSingleton<ManualDataProvider>();

var app = builder.Build();
// Trigger any required loading.
app.Services.GetRequiredService<ManualDataProvider>();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
	app.UseExceptionHandler("/Error");
}


app.UseStaticFiles();

app.UseRouting();
app.UseWebSockets();

app.MapBlazorHub();
app.MapFallbackToPage("/_Host");

ManualDataProvider mdp = app.Services.GetRequiredService<ManualDataProvider>();

app.Use(async (context, next) =>
{
	if (context.Request.Path.StartsWithSegments("/connect") && context.WebSockets.IsWebSocketRequest)
	{
		var manager = app.Services.GetRequiredService<ConnectionManager>();

		if (context.Request.Path.ToString().TrimEnd('/') == "/connect")
			await manager.AcceptServerAsync(await context.WebSockets.AcceptWebSocketAsync(new WebSocketAcceptContext() { DangerousEnableCompression = true }));
		else
		{
			string server = context.Request.Path.Value!["/connect/".Length..].TrimEnd('/');
			await manager.AcceptClientAsync(server, await context.WebSockets.AcceptWebSocketAsync(new WebSocketAcceptContext() { DangerousEnableCompression = true }));
		}
	}
	else if (context.Request.Path.ToString().Equals("/geos", StringComparison.InvariantCultureIgnoreCase))
		await context.Response.SendFileAsync(mdp.OsmGeoPath);
	else if (context.Request.Path.ToString().Equals("/boundaries", StringComparison.InvariantCultureIgnoreCase))
		await context.Response.SendFileAsync(mdp.BoundariesPath);
	else
		await next(context);
});

static DateTime GetConfigFileWrittenDate(IConfiguration config, string configPath) =>
	config[configPath] is string path
	? (File.Exists(path)
		? File.GetLastWriteTimeUtc(path)
		: Directory.Exists(path)
			? Directory.GetLastWriteTimeUtc(path)
			: DateTime.MaxValue)
	: DateTime.MaxValue;

app.MapGet("/cache/servers", () => DateTime.UtcNow);
app.MapGet("/servers", (ConnectionManager manager) => manager.ListServers());
app.MapGet("/cache/boundaries", (IConfiguration config) => GetConfigFileWrittenDate(config, "BoundariesFile"));
app.MapGet("/topologies", (ManualDataProvider mdp) => mdp.Topologies);
app.MapGet("/cache/topologies", (IConfiguration config) => GetConfigFileWrittenDate(config, "Topologies"));
app.MapGet("/cache/geos", (IConfiguration config) => GetConfigFileWrittenDate(config, "OsmPbf"));

app.Run();
