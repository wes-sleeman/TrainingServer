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
					_serverClients[server] = [.. otherClients, guid];
				else
					_serverClients.TryAdd(server, [guid]);

				monitor.OnTextMessageReceived += async msg =>
				{
					try
					{
						if (_monitors.TryGetValue(server, out var rSock))
							await rSock.SendAsync(msg);
					}
					catch (Exception) { }
				};

				await blocker;
			}
			catch (ArgumentException ex)
			{
				// Remote client is non-compliant. Purge it.
				if (_monitors.TryRemove(guid, out var sock))
					await sock.DisposeAsync(WebSocketCloseStatus.InvalidPayloadData, ex.Message);
			}
			catch (OperationCanceledException)
			{
				// Server was killed by the remote. Bye bye!
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

			if (await SetupAsync(guid) is string serverName)
			{

				_servers.TryAdd(guid, new(guid, serverName));
				_serverClients.TryAdd(guid, []);

				monitor.OnTextMessageReceived += async msg =>
				{
					try
					{
						if (_serverClients.TryGetValue(guid, out var clients))
							foreach (var client in clients)
								if (_monitors.TryGetValue(client, out var rSock))
									await rSock.SendAsync(msg);
					}
					catch (Exception) { }
				};

				await blocker;
			}
			else if (_monitors.TryRemove(guid, out var sock))
				// Remote client is non-compliant. Purge it
				await sock.DisposeAsync(WebSocketCloseStatus.ProtocolError, "Invalid handshake.");
		}
		catch (ArgumentException ex)
		{
			// Remote client is non-compliant. Purge it.
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

	/// <returns>The name of the endpoint.</returns>
	private async Task<string?> SetupAsync(Guid guid)
	{
		// Establish a tunnel.
		var socket = _monitors[guid];
		await Task.Delay(100);
		_ = socket.SendAsync(guid.ToString());

		string handshake = await socket.InterceptNextTextAsync();

		if (!handshake.StartsWith($"{guid}|"))
		{
			// Tunnel establishment handshake failed. Purge the client.
			if (_monitors.TryRemove(guid, out var sock))
				await sock.DisposeAsync(WebSocketCloseStatus.ProtocolError, "Handshake failed.");

			return null;
		}

		return handshake[(handshake.IndexOf('|') + 1)..];
	}
}
