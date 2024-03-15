using System.Net.WebSockets;
using System.Security.Cryptography;
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
	Transcoder _transcoder = new();

	public Task SendPositionAsync() => SelectedServer is null || socket is null || User is null ? Task.CompletedTask :
		socket.SendAsync(_transcoder.SecurePack(Me, SelectedServer.Value.Guid, new ControllerUpdate(Me, new(
			DateTimeOffset.Now,
			Metadata: User.Metadata,
			Position: User.Position
		))));

	public Task SendTextAsync(TextMessage message) => SelectedServer is null || socket is null || User is null ? Task.CompletedTask :
		socket.SendAsync(_transcoder.SecurePack(Me, SelectedServer.Value.Guid, message));

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
			_transcoder = new();
		}

		ClientWebSocket sockClient = new();

		try
		{
			await sockClient.ConnectAsync(new($"ws://127.0.0.1:5031/connect/{server.Guid}"), CancellationToken.None);
			socket = new(sockClient);
			_ = Task.Run(socket.MonitorAsync);

			string[] rsaParamElems = (await socket.InterceptNextTextAsync()).Split('|');
			if (rsaParamElems.Length != 3)
				return;

			Me = Guid.Parse(rsaParamElems[0]);
			_transcoder.LoadAsymmetricKey(new() { Modulus = Convert.FromBase64String(rsaParamElems[1]), Exponent = Convert.FromBase64String(rsaParamElems[2]) });

			byte[] symKey = Aes.Create().Key;
			_transcoder.RegisterKey(Me, symKey);
			_transcoder.RegisterSecondaryRecipient(server.Guid, Me);

			byte[] symKeyCrypt = _transcoder.AsymmetricEncrypt(symKey);
			await socket.SendAsync(symKeyCrypt);

			if (_transcoder.SecureUnpack(await socket.InterceptNextTextAsync()).Data is not JsonArray ja || ja.Count != 0)
			{
				// Tunnel establishment handshake failed. Purge the server.
				await socket.DisposeAsync(WebSocketCloseStatus.ProtocolError, "Handshake failed.");
				return;
			}

			void unpackMessage(string cryptedMessage)
			{
				DateTimeOffset sent; Guid recipient; JsonNode data;
				try
				{
					(sent, recipient, data) = _transcoder.SecureUnpack(cryptedMessage);
				}
				catch (ArgumentException) { return; }

				if (data.Deserialize<NetworkMessage>() is not NetworkMessage msg)
					return;

				PacketReceived?.Invoke(sent, msg);
			}

			socket.OnTextMessageReceived += unpackMessage;
			_token.Register(() => socket.OnTextMessageReceived -= unpackMessage);

			// Send the confirmation packet back to the hub.
			await Task.Delay(100);
			await socket.SendAsync(_transcoder.SecurePack(Me, Me, Array.Empty<object>()));

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
