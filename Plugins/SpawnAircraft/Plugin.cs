using System.Text.RegularExpressions;

using TrainingServer.Extensibility;

namespace SpawnAircraft;

public partial class Plugin : IPlugin
{
	public string Name => "Example Aircraft Spawner";
	public string Description => "A reference implementation of a basic aircraft spawner. For developer reference only.";
	public string Maintainer => "Wes (644899)";

	readonly IServer _server;

	public override string ToString() => $"{Name} ({Description})";

	public Plugin(IServer server)
	{
		_server = server;

		//_server.AddAircraft(new(
		//	metadata: new("BOT001", "ZZZZ", "ZZZZ", FlightData.FlightRules.IFR, "ZZZZ", "DCT", "RMK/Example"),
		//	position: new(360, 9000, new(33.9425, -118.408056), new(2000, Squawk.SquawkMode.Altitude)),
		//	movement: new(200, -10, 3)
		//));
	}

	public async Task ProcessTextMessageAsync(string sender, string recipient, string message)
	{
		message = message.Trim().ToUpperInvariant();

		if (!recipient.Equals("123.45") || !SpawnRegex().IsMatch(message))
			return;

		var matchGroups = SpawnRegex().Match(message).Groups;
		_server.AddAircraft(new(
			metadata: new(matchGroups["callsign"].Value, "ZZZZ", "ZZZZ", FlightData.FlightRules.IFR, "ZZZZ", "DCT", "RMK/Example"),
			position: new(360, 9000, new(float.Parse(matchGroups["lat"].Value), float.Parse(matchGroups["lon"].Value)), new(2000, Squawk.SquawkMode.Altitude)),
			movement: new(200, -10, 3)
		));

		await Task.CompletedTask;
	}

	public Task TickAsync(TimeSpan delta) => Task.CompletedTask;
	[GeneratedRegex(@"^SPAWN\s+(?<callsign>\S+)\s+AT\s+\((?<lat>-?\d+(\.\d*)?),\s*(?<lon>-?\d+(\.\d*)?)\)$", RegexOptions.ExplicitCapture | RegexOptions.Singleline | RegexOptions.Compiled)]
	private static partial Regex SpawnRegex();
}
