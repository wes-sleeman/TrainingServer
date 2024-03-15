using System.Text.RegularExpressions;

using TrainingServer.Extensibility;

namespace StressTest;

public partial class Insanity(IServer server) : IPlugin
{
	public string Name => "Stress Tester";

	public string Description => "Development stress testing";

	public string Maintainer => "Wes (644899)";

	bool stressed = false;

	public async Task ProcessTextMessageAsync(string sender, string recipient, string message)
	{
		message = message.Trim().ToUpperInvariant();

		if (!message.StartsWith("STRESS"))
			return;

		if (stressed)
		{
			foreach (var ac in server.Aircraft)
				server.UpdateAircraft(ac.Key, ac.Value with { Movement = ac.Value.Movement with { TurnRate = Random.Shared.Next(-60, 60) / 10f, ClimbRate = Random.Shared.Next(-500, 500) } });
		}
		else
		{
			var bounds = CoordinateRegex().Matches(message).Where(m => m.Success).Select(m => new Coordinate(float.Parse(m.Groups["lat"].Value), float.Parse(m.Groups["lon"].Value))).ToArray();

			if (bounds.Length == 0)
				return;

			stressed = true;

			float left = MathF.Truncate(bounds.Min(c => c.Longitude)), right = MathF.Truncate(bounds.Max(c => c.Longitude));
			float bottom = MathF.Truncate(bounds.Min(c => c.Latitude)), top = MathF.Truncate(bounds.Max(c => c.Latitude));

			static Aircraft newFromCoord(Coordinate position) => new(
				metadata: new($"STRESS{(int)(position.Latitude * 100)}{(int)(position.Longitude * 100)}", "ZZZZ", "ZZZZ", FlightData.FlightRules.VFR, "?", "INSANITY", $"CS/STRESS BOT RMK/{position}"),
				position: new(Random.Shared.Next(360), Random.Shared.Next(030, 180) * 100, position, new(2000, Squawk.SquawkMode.Altitude)),
				movement: new((uint)Random.Shared.Next(100, 400), Random.Shared.Next(-500, 500), Random.Shared.Next(-60, 60) / 10f)
			);

			for (float lat = top; lat >= bottom; lat -= 0.5f)
				for (float lon = left; lon <= right; lon += 0.5f)
					server.AddAircraft(newFromCoord(new(lat, lon)));
		}
	}

	public Task TickAsync(TimeSpan delta) => Task.CompletedTask;


	[GeneratedRegex(@"\(\s*(?<lat>-?\d+(\.\d*)?)\s*,\s*(?<lon>-?\d+(\.\d*)?)\s*\)", RegexOptions.ExplicitCapture | RegexOptions.Compiled)]
	private static partial Regex CoordinateRegex();
}
