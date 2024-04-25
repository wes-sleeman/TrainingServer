using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

using TrainingServer.Extensibility;

namespace TrainingServer.Connector;

internal class FsdNegotiator
{
	public event Action<StreamWriter, string[]>? PacketReceived;

	public ConcurrentDictionary<Guid, Aircraft> Aircraft { get; } = new();
	public ConcurrentDictionary<Guid, Controller> Controllers { get; } = new();

	public Task Send(string packet) => Distribute?.Invoke(packet) ?? Task.CompletedTask;

	public string Callsign { get; set; }

	readonly CancellationToken _token;
	private event Func<string, Task>? Distribute;

	public FsdNegotiator(CancellationToken token = default)
	{
		_token = token;
		Callsign = "";

		if (token.IsCancellationRequested)
			return;

		_ = Task.Run(RunServer, default);
	}

	public string GetCallsign(Guid user)
	{
		if (Aircraft.TryGetValue(user, out Aircraft? ac))
			return ac.Metadata.Callsign;
		else if (Controllers.TryGetValue(user, out Controller? c))
			return c.Metadata.Callsign;
		else
			throw new ArgumentException("Could not find user with the provided GUID.", nameof(user));
	}

	public IEnumerable<Guid> GetGuids(string callsign)
	{
		foreach (var kvp in Aircraft)
			if (kvp.Value.Metadata.Callsign.Equals(callsign, StringComparison.InvariantCultureIgnoreCase))
				yield return kvp.Key;

		foreach (var kvp in Controllers)
			if (kvp.Value.Metadata.Callsign.Equals(callsign, StringComparison.InvariantCultureIgnoreCase))
				yield return kvp.Key;
	}

	public async Task ClearAsync()
	{
		await Task.WhenAll(
			Aircraft.Values.Select(ac => Distribute?.Invoke($"#DP{ac.Metadata.Callsign}")).Concat(
				Controllers.Values.Select(c => Distribute?.Invoke($"#DA{c.Metadata.Callsign}"))
			).Where(t => t is not null).Cast<Task>()
		);

		Aircraft.Clear();
		Controllers.Clear();
	}

	public void MessageReceived(string from, string to, string message)
	{
		Distribute?.Invoke($"#{from.Replace(":", "$C")}:{to.Replace(":", "$C")}:{message.Replace(":", "$C")}");
	}

	async Task ClientConnectedAsync(TcpClient client)
	{
		NetworkStream clientStream = client.GetStream();
		StreamReader reader = new(clientStream);
		StreamWriter writer = new(clientStream) {
			AutoFlush = true,
			NewLine = "\r\n"
		};

		SemaphoreSlim writeLock = new(1);

		Distribute += async (string message) =>
		{
			try
			{
				await writeLock.WaitAsync();
				await writer.WriteLineAsync(message);
			}
			catch (IOException)
			{
				// Client disconnected.
			}
			finally
			{
				writeLock.Release();
			}
		};

		_ = Task.Run(async () =>
		{
			while (!_token.IsCancellationRequested && client.Connected)
			{
				await PokeClientAsync();
				await Task.Delay(100);
			}
		});

		while (!_token.IsCancellationRequested && client.Connected)
		{
			await PokeClientAsync();

			if (reader.EndOfStream || reader.ReadLine() is not string data)
			{
				await Task.Delay(250);
				continue;
			}

			string header = new([.. data.TakeWhile(c => !char.IsLetterOrDigit(c))]);

			PacketReceived?.Invoke(writer, [header, .. data[header.Length..].Split(':').Select(l => l.Replace("$C", ":"))]);
		}

		Distribute -= writer.WriteLineAsync;
	}

	HashSet<Aircraft> prevAircraft = [];
	HashSet<Controller> prevControllers = [];
	DateTimeOffset lastPoke = DateTimeOffset.MinValue;
	async Task PokeClientAsync()
	{
		if (lastPoke > DateTimeOffset.Now - TimeSpan.FromSeconds(1.5))
			return;

		lastPoke = DateTimeOffset.Now;

		Aircraft[] newAc = [.. Aircraft.Values.Except(prevAircraft)];
		Controller[] newControllers = [.. Controllers.Values.Except(prevControllers)];

		foreach (Aircraft aircraft in newAc)
		{
			Distribute?.Invoke($"#AP{aircraft.Metadata.Callsign}:{Callsign}:111111::6:40:A Bot");
			Distribute?.Invoke($"=A{Callsign}:{aircraft.Metadata.Callsign}");
		}

			foreach (Controller c in newControllers)
			Distribute?.Invoke($"#AA{c.Metadata.Callsign}:{Callsign}:Another User:111111::6");

		prevAircraft = [.. Aircraft.Values];
		prevControllers = [.. Controllers.Values];

		var extrapolationOffset = DateTimeOffset.Now - TimeSpan.FromSeconds(1);

		string[] updates =
			Aircraft.Values
			.Select(ac => ac.Extrapolate(extrapolationOffset))
			.Select(ac => $"@{ac.Position.Squawk.Mode switch { Squawk.SquawkMode.Altitude => "N", _ => "S" }}:{ac.Metadata.Callsign}:{ac.Position.Squawk.Code}:6:{ac.Position.Position.Latitude}:{ac.Position.Position.Longitude}:{ac.Position.Altitude}:{ac.Movement.Speed}:{TupleToPBH((0, 0, ac.Position.Heading, false))}:0")
			.ToArray();

		if (Distribute is not null)
			foreach (string pos in updates)
				await Distribute.Invoke(pos);
	}

	public static uint TupleToPBH((float Pitch, float Bank, float Heading, bool OnGround) PBH)
	{
		uint TenBitMask = 0b00000011_11111111;

		const float pbhScale = 128f / 45;
		static ushort scale(float value) => (ushort)((value + 360) % 360 * pbhScale);

		uint retval = scale(PBH.Pitch) & TenBitMask;
		retval <<= 10;
		retval += scale(PBH.Bank) & TenBitMask;
		retval <<= 10;
		retval += scale(PBH.Heading) & TenBitMask;
		retval <<= 1;
		retval += (uint)(PBH.OnGround ? 1 : 0);
		retval <<= 1;

		return retval;
	}

	async Task RunServer()
	{
		TcpListener _listener = new(IPAddress.Any, 6809);
		_listener.Start();

		while (!_token.IsCancellationRequested)
		{
			if (!_listener.Pending())
			{
				await Task.Delay(1000);
				continue;
			}

			_ = Task.Run(async () => await ClientConnectedAsync(await _listener.AcceptTcpClientAsync()));
		}
	}
}
