using TrainingServer.Hub.Data;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorPages();
builder.Services.AddServerSideBlazor();
builder.Services.AddSingleton<ConnectionManager>();
builder.Services.AddSingleton<OsmDataProvider>();

var app = builder.Build();
app.Services.GetRequiredService<OsmDataProvider>();

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
	else
		await next(context);
});

app.MapGet("/servers", (ConnectionManager manager) => manager.ListServers());

app.MapGet("/geos/nodes", (OsmDataProvider osm) => osm.Nodes.Values);
app.MapGet("/geos/ways", (OsmDataProvider osm) => osm.Ways.Values);
app.MapGet("/geos/relations", (OsmDataProvider osm) => osm.Relations.Values);

app.Run();
