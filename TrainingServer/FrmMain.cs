using System.Net.WebSockets;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

using TrainingServer.Extensibility;
using TrainingServer.Networking;

namespace TrainingServer;

public partial class FrmMain : Form
{
	public const string HUB_ADDRESS = @"hub.wsleeman.com";

	public FrmMain()
	{
		InitializeComponent();

		Text = "Training Server (Disconnected)";
		BtnStart.Enabled = true;
		server = new();
		manager = new(server);
		manager.OnPluginsListUpdated += Manager_OnPluginsListUpdated;
	}

	WebsocketMonitor? socket;
	readonly PluginManager manager;
	readonly Server server;
	Guid guid;

	private void BtnStart_Click(object sender, EventArgs e)
	{
		switch (BtnStart.Text)
		{
			case "Connect":
				BtnStart.Enabled = false;
				Task.Run(ServerConnectedAsync);
				break;

			case "Disconnect":
				Task.Run(async () => await socket!.DisposeAsync(WebSocketCloseStatus.NormalClosure, "Good day!"));
				BtnStart.Text = "Connect";
				Text = "Training Server (Disconnected)";
				break;
		}
	}

	private async Task ServerConnectedAsync()
	{
		ClientWebSocket sockClient = new();

		try
		{
			// Connect to the server.
			await sockClient.ConnectAsync(new($"ws://{HUB_ADDRESS}/connect"), CancellationToken.None);
			socket = new(sockClient);
			_ = socket.MonitorAsync();

			// Get your GUID.
			string guidStr = await socket.InterceptNextTextAsync();
			guid = Guid.Parse(guidStr);
			Invoke(() => Text = $"Training Server (Connected: {guid})");

			// Send your server name.
			await socket.SendAsync($"{guid}|{guid}");

			// Update the UI.
			BtnStart.Invoke(() =>
			{
				BtnStart.Text = "Disconnect";
				BtnStart.Enabled = true;
			});

			socket.OnTextMessageReceived += async (msg) =>
			{
				try
				{
					if (JsonNode.Parse(msg).Deserialize<NetworkMessage>(JsonSerializerOptions.Default) is NetworkMessage netmsg)
						await server.MessageReceivedAsync(netmsg);
				}
				catch (Exception ex)
				{
					System.Diagnostics.Debug.WriteLine($"Error handling message!{Environment.NewLine}Message: {msg}{Environment.NewLine}Error: {ex}.");
				}
			};

			server.OnTextMessageReceived += async txt =>
			{
				string sender, recipient;

				switch (server.ResolveGuid(txt.From))
				{
					case Controller c:
						sender = c.Metadata.Callsign;
						break;

					case Aircraft ac:
						sender = ac.Metadata.Callsign;
						break;

					case null when txt.From == Guid.Empty:
						sender = "SERVER";
						break;

					default:
						return;
				}

				if (FrequencyGuidRegex().Match(txt.To.ToString()) is Match m && m.Success)
					recipient =
						(decimal.Parse(m.Groups[1].Value) / 1000m)
							   .ToString("000.00#");
				else if (server.ResolveGuid(txt.To) is Controller c)
					recipient = c.Metadata.Callsign;
				else if (server.ResolveGuid(txt.To) is Aircraft ac)
					recipient = ac.Metadata.Callsign;
				else
					// Couldn't find the Guid.
					return;

				await manager.ProcessTextMessageAsync(sender, recipient, txt.Message);
			};

			server.OnTextMessageSent += async txt => await socket.SendAsync(txt);

			server.OnAircraftAdded += async g =>
			{
				await socket.SendAsync(new AuthoritativeUpdate(
					g,
					server.Controllers.Select(kvp => new ControllerUpdate(kvp.Key, kvp.Value)).ToArray(),
					server.Aircraft.Select(kvp => new AircraftUpdate(kvp.Key, kvp.Value)).ToArray()
				));
			};

			server.OnAircraftUpdated += Server_OnAircraftUpdated;

			// Register new controllers and send authoritative updates.
			server.OnControllerAdded += async g =>
			{
				await socket.SendAsync(new AuthoritativeUpdate(
					g,
					server.Controllers.Select(kvp => new ControllerUpdate(kvp.Key, kvp.Value)).ToArray(),
					server.Aircraft.Select(kvp => new AircraftUpdate(kvp.Key, kvp.Value)).ToArray()
				));
			};

			server.OnControllerUpdated += Server_OnControllerUpdated;

			await Task.WhenAll(
				Task.Run(async () =>
				{
					DateTimeOffset lastAuthoritativeUpdate = DateTimeOffset.Now;
					while (socket is not null && socket.IsConnected)
					{
						if (DateTimeOffset.Now - lastAuthoritativeUpdate < TimeSpan.FromMinutes(0.5))
						{
							await Task.Delay(1000);
							continue;
						}

						await Task.WhenAll(
							server.Controllers.Keys.Select(cGuid => socket.SendAsync(new AuthoritativeUpdate(
								cGuid,
								[.. server.Controllers.Select(c => new ControllerUpdate(c.Key, c.Value))],
								[.. server.Aircraft.Select(ac => new AircraftUpdate(ac.Key, ac.Value))]
							)))
						);
						lastAuthoritativeUpdate = DateTimeOffset.Now;
					}
				}),
				PollPluginsAsync()
			);
		}
		catch (WebSocketException) { }

		await socket!.DisposeAsync(WebSocketCloseStatus.NormalClosure, "Good day!");
	}

	private async void Server_OnControllerUpdated(ControllerUpdate delta)
	{
		if (socket is null)
			return;

		await socket.SendAsync(delta);
	}

	private async void Server_OnAircraftUpdated(AircraftUpdate[] delta)
	{
		if (socket is null)
			return;

		foreach (var update in delta)
			await socket.SendAsync(update);
	}

	private async Task PollPluginsAsync()
	{
		DateTimeOffset lastRun = DateTimeOffset.Now;

		while (socket is not null)
		{
			TimeSpan delta = DateTimeOffset.Now - lastRun;
			lastRun = DateTimeOffset.Now;

			await manager.TickAsync(delta);
			server.CommitBatch();
		}
	}

	private void FrmMain_FormClosing(object sender, FormClosingEventArgs e)
	{
		if (BtnStart.Text == "Disconnect" && socket is not null)
			socket.DisposeAsync(WebSocketCloseStatus.NormalClosure, "Good day!").AsTask().Wait();
	}

	private void Manager_OnPluginsListUpdated()
	{
		if (InvokeRequired)
		{
			Invoke(Manager_OnPluginsListUpdated);
			return;
		}

		ClbPlugins.BeginUpdate();
		ClbPlugins.Items.Clear();

		foreach (var kvp in manager._loadedPlugins.Where(kvp => kvp.Key.GetType().GetTypeInfo().GetCustomAttributes().All(a => a is not HiddenAttribute)))
			ClbPlugins.Items.Add(kvp.Key, kvp.Value);

		ClbPlugins.EndUpdate();
	}

	[GeneratedRegex(@"^(\d{8})-0000-0000-0000-000000000000$", RegexOptions.Compiled | RegexOptions.Singleline)]
	private static partial Regex FrequencyGuidRegex();

	private void ClbPlugins_ItemCheck(object sender, ItemCheckEventArgs e)
	{
		IPlugin modifiedPlugin = (IPlugin)((CheckedListBox)sender).Items[e.Index];
		manager._loadedPlugins[modifiedPlugin] = e.NewValue == CheckState.Checked;
	}
}
