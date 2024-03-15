namespace TrainingServer.Hub.Data;

using System;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Linq;
using System.Net.WebSockets;
using System.Text.Json.Nodes;

using TrainingServer.Networking;

/// <summary>
/// Manages websocket connections received by the server.
/// </summary>
public class ConnectionManager
{
	private readonly ConcurrentDictionary<Guid, WebsocketMonitor> _monitors = [];
	private readonly ConcurrentDictionary<Guid, ServerInfo> _servers = [];
	private readonly ConcurrentDictionary<Guid, ImmutableHashSet<Guid>> _serverClients = [];
	private readonly Transcoder _transcoder = new();

	/// <summary>Accepts an incoming client websocket connection and establishes a secure tunnel.</summary>
	/// <param name="serverId">The server that the websocket was established against.</param>
	/// <param name="socket">The accepted <see cref="WebSocket"/>.</param>
	public async Task AcceptClientAsync(string serverId, WebSocket socket)
	{
		Guid guid = Guid.NewGuid();
		WebsocketMonitor monitor = new(socket);

		_monitors.TryAdd(guid, monitor);

		if (Guid.Parse(serverId) is Guid server && _monitors.ContainsKey(server))
		{
			try
			{
				// Perform the handshake to setup encryption.
				Task blocker = monitor.MonitorAsync();
				await SetupAsync(guid);

				if (_serverClients.TryGetValue(server, out var otherClients))
					_serverClients[server] = [..otherClients, guid];
				else
					_serverClients.TryAdd(server, [guid]);

				monitor.OnTextMessageReceived += async msg =>
				{
					try
					{
						var (sent, recipient, data) = _transcoder.SecureUnpack(msg);

						if (_monitors.TryGetValue(recipient, out var rSock))
							await rSock.SendAsync(_transcoder.SecurePack(server, recipient, data));
					}
					catch (Exception) { }
				};

				await blocker;
			}
			catch (ArgumentException ex)
			{
				// Remote client is non-compliant. Purge it.
				_transcoder.TryUnregister(guid);
				if (_monitors.TryRemove(guid, out var sock))
					await sock.DisposeAsync(WebSocketCloseStatus.InvalidPayloadData, ex.Message);
			}

			if (_serverClients.TryGetValue(server, out var allClients))
				_serverClients[server] = allClients.Remove(guid);
		}

		if (_monitors.TryRemove(guid, out var m))
		{
			if (_monitors.ContainsKey(server))
				await m.DisposeAsync();
			else
				await m.DisposeAsync(WebSocketCloseStatus.EndpointUnavailable, "The requested server is not available.");
		}
	}

	/// <summary>Accepts an incoming server websocket connection and establishes a secure tunnel.</summary>
	/// <param name="socket">The accepted <see cref="WebSocket"/>.</param>
	public async Task AcceptServerAsync(WebSocket socket)
	{
		Guid guid = Guid.NewGuid();
		WebsocketMonitor monitor = new(socket);
		_monitors.TryAdd(guid, monitor);

		try
		{
			// Perform the handshake to setup encryption.
			Task blocker = monitor.MonitorAsync();
			await SetupAsync(guid);
			_servers.TryAdd(guid, new(guid, _transcoder.SecureUnpack(await monitor.InterceptNextTextAsync()).Data.ToString()));
			_serverClients.TryAdd(guid, []);

			monitor.OnTextMessageReceived += async msg =>
			{
				try
				{
					var (sent, recipient, data) = _transcoder.SecureUnpack(msg);

					if (recipient == guid)
						await Task.WhenAll(_monitors.Where(kvp => kvp.Key != guid).AsParallel().Select(kvp => kvp.Value.SendAsync(_transcoder.SecurePack(guid, kvp.Key, data))));
					else if (_monitors.TryGetValue(recipient, out var rSock))
						await rSock.SendAsync(_transcoder.SecurePack(guid, recipient, data));
				}
				catch (Exception) { }
			};

			await blocker;
		}
		catch (ArgumentException ex)
		{
			// Remote client is non-compliant. Purge it.
			_transcoder.TryUnregister(guid);
			if (_monitors.TryRemove(guid, out var sock))
				await sock.DisposeAsync(WebSocketCloseStatus.ProtocolError, ex.Message);
		}
		catch (WebSocketException) { }

		_servers.Remove(guid, out _);

		if (_monitors.TryRemove(guid, out var m))
			await m.DisposeAsync();

		// Remove all connected clients.
		if (_serverClients.TryGetValue(guid, out var allClients))
			await Task.WhenAll(allClients.Where(_monitors.ContainsKey).Select(mg => _monitors[mg].DisposeAsync().AsTask()));
	}

	public IEnumerable<ServerInfo> ListServers() => _servers.Values;

	private async Task SetupAsync(Guid guid)
	{
		// Establish a tunnel.
		var socket = _monitors[guid];
		var key = _transcoder.GetAsymmetricKey();
		await Task.Delay(100);
		_ = socket.SendAsync(guid.ToString() + '|' + Convert.ToBase64String(key.Modulus!) + '|' + Convert.ToBase64String(key.Exponent!));

		byte[] symKeyCrypt = (await socket.InterceptNextBinaryAsync())[..256];
		_transcoder.RegisterKey(guid, _transcoder.AsymmetricDecrypt(symKeyCrypt));

		await Task.Delay(100);
		// Send an encrypted handshake.
		_ = _monitors[guid].SendAsync(_transcoder.SecurePack(guid, guid, Array.Empty<object>()));

		if (_transcoder.SecureUnpack(await socket.InterceptNextTextAsync()).Data is not JsonArray ja || ja.Count != 0)
		{
			// Tunnel establishment handshake failed. Purge the client.
			_transcoder.TryUnregister(guid);
			if (_monitors.TryRemove(guid, out var sock))
				await sock.DisposeAsync(WebSocketCloseStatus.ProtocolError, "Handshake failed.");
		}
	}
}
