﻿using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Nodes;

using TrainingServer.Extensibility;
using TrainingServer.Networking;

namespace TrainingServer.Connector;

internal class ServerNegotiator(CancellationToken token)
{
	public event Action<DateTimeOffset, NetworkMessage>? PacketReceived;

	public ServerInfo? SelectedServer { get; private set; }
	public Guid Me { get; private set; } = new();
	public Controller? User { get; set; }

	readonly CancellationToken _token = token;

	WebsocketMonitor? socket;

	public Task SendPositionAsync() => SelectedServer is null || socket is null || User is null ? Task.CompletedTask :
		socket.SendAsync(new ControllerUpdate(Me, new(
			DateTimeOffset.Now,
			Metadata: User.Metadata,
			Position: User.Position
		)));

	public Task SendTextAsync(TextMessage message) => SelectedServer is null || socket is null || User is null ? Task.CompletedTask :
		socket.SendAsync(message);

	public async Task ConnectAsync(ServerInfo server)
	{
		if (SelectedServer is not null)
			Disconnect();

		SelectedServer = server;
		_token.Register(Disconnect);

		if (socket is not null)
		{
			await socket!.DisposeAsync(WebSocketCloseStatus.NormalClosure, "Good day!");
			socket = null;
		}

		ClientWebSocket sockClient = new();

		try
		{
			await sockClient.ConnectAsync(new($"ws://{HubNegotiator.HUB_ADDRESS}/connect/{server.Guid}"), CancellationToken.None);
			socket = new(sockClient);
			_ = Task.Run(socket.MonitorAsync);

			Me = Guid.Parse(await socket.InterceptNextTextAsync());
			await socket.SendAsync($"{Me}|{Environment.UserName}");

			void unpackMessage(string rawJson)
			{
				try
				{
					if (JsonNode.Parse(rawJson).Deserialize<NetworkMessage>() is not NetworkMessage msg)
						return;

					PacketReceived?.Invoke(DateTimeOffset.Now, msg);
				}
				catch (JsonException) when (rawJson.Length <= 0) { return; }
				catch (JsonException)
				{
					rawJson = rawJson.TrimEnd()[..^1];

					int quotes, braceCount;

					do
					{
						int lastBrace = rawJson[..^1].LastIndexOf('}');

						if (lastBrace == -1)
							return;

						rawJson = rawJson[..(lastBrace + 1)];

						quotes = 0; braceCount = 0;

						foreach (char c in rawJson)
							if (c == '"')
								++quotes;
							else if (c == '{')
								++braceCount;
							else if (c == '}')
								--braceCount;
					} while (quotes % 2 != 0 || braceCount != 0);

					unpackMessage(rawJson);
				}
			}

			socket.OnTextMessageReceived += unpackMessage;
			_token.Register(() => socket.OnTextMessageReceived -= unpackMessage);

			// Present yourself to the server.
			await SendPositionAsync();
		}
		catch (WebSocketException) { }
	}

	public void Disconnect()
	{
		SelectedServer = null;
	}
}
