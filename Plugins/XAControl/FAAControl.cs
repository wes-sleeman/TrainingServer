using AirPlanner;

using CIFPReader;

using System.Text.RegularExpressions;

using TrainingServer.Extensibility;

using AltitudeRestriction = AirPlanner.AltitudeRestriction;
using Coordinate = TrainingServer.Extensibility.Coordinate;
using SpeedRestriction = AirPlanner.SpeedRestriction;

namespace FAAControl;

public partial class FAAControl(IServer _server, AirPlanner.AirPlanner _planner) : IPlugin
{
	public string Name => "FAA Control";

	public string Description => "Controls aircraft using FAA data in FAA airspace.";

	public string Maintainer => "Wes (644899)";

	private readonly CIFP _cifp = CIFP.Load("cifp");

	public Task ProcessTextMessageAsync(string sender, string recipient, string message)
	{
		if (!message.Contains(','))
			return Task.CompletedTask;

		/// <summary>Checks if a regex matches the message. If so, return it and advance through the message.</summary>
		Match? checkMatch(Regex regex)
		{
			Match retval = regex.Match(message);

			if (!retval.Success)
				return null;

			message = message[retval.Length..].TrimStart();
			return retval;
		}

		string callsign = message[..message.IndexOf(',')];
		string originalMessage = message[(message.IndexOf(',') + 1)..].TrimStart();

		foreach (var (guid, ac) in _server.GetAircraftByCallsign(callsign))
		{
			message = originalMessage;
			LnavInstruction lnav = new PresentHeading();
			string directTo = "";

			// First check headings.
			if (checkMatch(_TurnMessage()) is Match turn)
				lnav = new Heading(float.Parse(turn.Groups["heading"].Value));

			// Directs override headings.
			if (checkMatch(_DirectMessage()) is Match direct && ResolveWaypoint((directTo = direct.Groups["waypoint"].Value.ToUpperInvariant()), ac.Position.Position) is Coordinate dCoord)
				lnav = new Direct(dCoord);

			AltitudeRestriction altRes = AltitudeRestriction.Empty;
			if (checkMatch(_AltitudeMessage()) is Match alt)
			{
				int altMsl = int.Parse(alt.Groups["alt"].Value) * 100;
				altRes = alt.Groups["type"].Value switch {
					"@" => new AltitudeRestriction(altMsl, altMsl),                 // At
					"c" when ac.Position.Altitude < altMsl => new(altMsl, altMsl),  // Climb
					"d" when ac.Position.Altitude > altMsl => new(altMsl, altMsl),  // Descend
					"a" => new(altMsl, null),                                       // At or above
					"b" => new(null, altMsl),                                       // At or below
					_ => AltitudeRestriction.Empty
				};
			}

			SpeedRestriction spdRes = SpeedRestriction.Empty;
			if (checkMatch(_SpeedMessage()) is Match spd)
			{
				uint spdKts = uint.Parse(spd.Groups["speed"].Value);
				spdRes = spd.Groups["type"].Value switch {
					"@" => new SpeedRestriction(spdKts, spdKts),    // At
					"a" => new(spdKts, null),                       // No slower than
					"b" => new(null, spdKts),                       // No faster than
					_ => SpeedRestriction.Empty
				};
			}

			// Terminate when crossing for directs, altitude for alt only instructions, and otherwise go forever.
			Termination termination = lnav switch {
				Direct => Termination.Crossing,
				_ => altRes == AltitudeRestriction.Empty ? Termination.Forever : Termination.Altitude
			};

			// Use the current instruction to fill in gaps where able.
			Instruction vector =
				_planner.TryGetValue(guid, out var instructions) && ((Queue<Instruction>)instructions).TryPeek(out var i)
				? new(
					lnav is PresentHeading ? i.Lnav : lnav,
					altRes == AltitudeRestriction.Empty ? i.Altitude : altRes,
					spdRes == SpeedRestriction.Empty ? i.Speed : spdRes,
					termination
				)
				: new(lnav, altRes, spdRes, termination);

			string resp = vector.ToString();

			if (lnav is Direct d && _planner.TryGetValue(guid, out var enumPrevRoute))
			{
				resp = $"Shortcut {d}";
				var prevRoute = (Queue<Instruction>)enumPrevRoute;
				Queue<Instruction> newRoute = new(prevRoute.SkipWhile(i => i.Lnav is not Direct od || od.Endpoint.DistanceTo(d.Endpoint) > 0.1f));

				if (newRoute.Count != 0)
					// This is a shortcut. Cut the route appropriately.
					_planner.SetRoute(guid, [vector, .. newRoute]);
				else
					_planner.Vector(guid, vector);
			}
			else
				_planner.Vector(guid, vector);

			if (checkMatch(_ProcedureMessage()) is Match procMatch && _cifp.Procedures.TryGetValue(procMatch.Groups[1].Value, out var procs))
			{
				Procedure proc = procs.MinBy(p =>
				{
					if (p.SelectAllRoutes(_cifp.Fixes).FirstOrDefault(i => i?.Endpoint is NamedCoordinate)?.Endpoint is not NamedCoordinate nc ||
						ResolveWaypoint(nc.Name, ac.Position.Position) is not Coordinate c)
						return float.MaxValue;

					return ac.Position.Position.DistanceTo(c);
				}) ?? procs.First();

				_planner.SetRoute(guid, proc.SelectRoute(null, null).Select(ConvertCifpInstruction));
				resp += $" Cleared {proc.Name}.";
			}

			if (checkMatch(_SquawkMessage()) is Match sq)
			{
				ushort squawkCode = ushort.Parse(sq.Groups["code"].Value);
				_server.UpdateAircraft(guid, ac with { Time = ac.Time, Position = ac.Position with { Squawk = new(squawkCode, Squawk.SquawkMode.Altitude) } });
				resp += $" Squawk {squawkCode:0000}.";
			}

			foreach (var controller in _server.Controllers.Where(c => c.Value.Metadata.Callsign == sender))
				if (_FrequencyChannel().IsMatch(recipient))
					_server.SendChannelMessage(decimal.Parse(recipient), resp);
				else
					_server.SendTextMessage(guid, controller.Key, resp);
		}

		return Task.CompletedTask;
	}

	private Coordinate? ResolveWaypoint(string waypoint, Coordinate referencePosition)
	{
		if (!_cifp.Fixes.TryGetValue(waypoint, out var possibleCoords) || possibleCoords.Count == 0)
			return null;

		return possibleCoords
			.Select(ic => { var oldCoord = ic.GetCoordinate(); return new Coordinate((float)oldCoord.Latitude, (float)oldCoord.Longitude); })
			.MinBy(c => c.DistanceTo(referencePosition));
	}

	private Instruction ConvertCifpInstruction(Procedure.Instruction instr)
	{
		LnavInstruction? lnavInstr = null;
		Termination termination = Termination.Forever;

		if (instr.Termination.HasFlag(ProcedureLine.PathTermination.UntilCrossing) && instr.Endpoint is ICoordinate epCoord)
		{
			var oldCoord = epCoord.GetCoordinate();
			lnavInstr = new Direct(new Coordinate((double)oldCoord.Latitude, (double)oldCoord.Longitude));
			termination = Termination.Crossing;
		}
		else if (
			(instr.Termination.HasFlag(ProcedureLine.PathTermination.Course) ||
			 instr.Termination.HasFlag(ProcedureLine.PathTermination.Track) ||
			 instr.Termination.HasFlag(ProcedureLine.PathTermination.Heading)) &&
			 instr.Via is Course hdgCrs)
			lnavInstr = new Heading((float)hdgCrs.ToTrue().Degrees);
		else
			System.Diagnostics.Debugger.Break();

		return new(
			lnavInstr ?? new PresentHeading(),
			new AltitudeRestriction(instr.Altitude.Minimum?.Feet, instr.Altitude.Maximum?.Feet),
			new SpeedRestriction(instr.Speed.Minimum, instr.Speed.Maximum),
			termination
		);
	}

	public Task TickAsync(TimeSpan delta) => Task.CompletedTask;

	[GeneratedRegex(@"^t(?<direction>[lrh]?)\s+(?<heading>([0-3]?\d)?\d)", RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.ExplicitCapture)]
	private partial Regex _TurnMessage();

	[GeneratedRegex(@"^(pd|x)\s+(?<waypoint>\w+)", RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.ExplicitCapture)]
	private partial Regex _DirectMessage();

	[GeneratedRegex(@"^(?<type>(@|c|d|a|b))\s+(?<alt>-?\d\d\d)(?!k)", RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.ExplicitCapture)]
	private partial Regex _AltitudeMessage();

	[GeneratedRegex(@"^(?<type>(@|a|b))\s+(?<speed>\d+)k(ts?)?", RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.ExplicitCapture)]
	private partial Regex _SpeedMessage();

	[GeneratedRegex(@"^s(qk?)?\s+(?<code>[0-7]{4})", RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.ExplicitCapture)]
	private partial Regex _SquawkMessage();

	[GeneratedRegex(@"^id(ent)?", RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.ExplicitCapture)]
	private partial Regex _IdentMessage();

	[GeneratedRegex(@"^p(roc)? (?<id>[\w\.]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.ExplicitCapture)]
	private partial Regex _ProcedureMessage();

	[GeneratedRegex(@"^\d+\.\d+$", RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.ExplicitCapture)]
	private partial Regex _FrequencyChannel();
}
