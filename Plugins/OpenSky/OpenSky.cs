using System.Net.Http.Json;
using System.Text.Json;

using TrainingServer.Extensibility;

namespace OpenSky;

public class OpenSky(IServer _server) : IPlugin
{
	private readonly TimeSpan UPDATE_FREQ = TimeSpan.FromSeconds(15);
	public string Name => "OpenSky";
	public string Description => "Replicates a small bubble of OpenSky traffic around each connected user.";
	public string Maintainer => "Wes (644899)";
	public override string ToString() => "Real-world traffic from OpenSky";
	public Task ProcessTextMessageAsync(string sender, string recipient, string message) => Task.CompletedTask;

	readonly HttpClient _http = new();

	TimeSpan _timeSinceLastUpdate = TimeSpan.FromDays(1);

	public async Task TickAsync(TimeSpan delta)
	{
		_timeSinceLastUpdate += delta;

		if (_timeSinceLastUpdate < UPDATE_FREQ)
			return;

		_timeSinceLastUpdate = TimeSpan.Zero;

		static (float minLat, float maxLat, float minLon, float maxLon) getBox(Coordinate c) =>
			(c.Latitude - 1.5f, c.Latitude + 1.5f, c.Longitude - 1.5f, c.Longitude + 1.5f);

		Coordinate[] cps = [.. _server.Controllers.Values.SelectMany(c => c.Position.RadarAntennae is null ? [] : c.Position.RadarAntennae)];

		(float minLat, float maxLat, float minLon, float maxLon)[] boundingBoxes = [..
			cps
				.Where(cp => !cps.Any(cp2 => cp.DistanceTo(cp2) < 30 && cp.Latitude < cp2.Latitude))
				.Select(getBox)
		];

		string[] urls = [.. boundingBoxes.Select(bb => $"https://opensky-network.org/api/states/all?lamin={bb.minLat}&lamax={bb.maxLat}&lomin={bb.minLon}&lomax={bb.maxLon}")];

		List<Aircraft> aircraft = [];

		foreach (string url in urls)
		aircraft.AddRange((await _http.GetFromJsonAsync<OpenSkyAircraft>(url))?.Aircraft ?? throw new JsonException());

		aircraft = [..aircraft.DistinctBy(ac => ac.Metadata.Callsign).Select(ac => ac with { Movement = ac.Movement with { Speed = (uint)(ac.Movement.Speed * 0.7f) } })];
		HashSet<Aircraft> newAcs = [.. aircraft.Where(ac => !_server.Aircraft.Values.Any(ac2 => ac2.Metadata.Callsign == ac.Metadata.Callsign))];
		HashSet<Aircraft> updateAcs = [.. aircraft.Except(newAcs)];

		foreach (Aircraft ac in newAcs)
			_server.AddAircraft(ac);

		foreach (Aircraft ac in updateAcs)
			_server.UpdateAircraft(_server.Aircraft.First(kvp => kvp.Value.Metadata.Callsign == ac.Metadata.Callsign).Key, ac);
	}
}