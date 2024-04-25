using TrainingServer.Extensibility;

namespace AirPlanner;

[Hidden]
public class AirPlanner(IServer server) : IPlugin
{
	public string Name => "Airborne Planner";

	public string Description => "Provides a convenient interface for queuing a sequence of instructions to an aircraft.";

	public string Maintainer => "Wes (644899)";

	public Task ProcessTextMessageAsync(string sender, string recipient, string message) => Task.CompletedTask;

	readonly Dictionary<Guid, Queue<Instruction>> _filedRoutes = [];
	readonly HashSet<Guid> _pendingSkips = [];
	readonly IServer _server = server;
	readonly Dictionary<Guid, Instruction> _ongoingInstructions = [];

	public Task TickAsync(TimeSpan delta)
	{
		foreach (var kvp in _server.Aircraft)
		{
			// Make sure airplane is tracked and has something to do.
			if (!_filedRoutes.TryGetValue(kvp.Key, out var instructions) || instructions.Count == 0)
				continue;

			Instruction executing = instructions.Peek();

			switch (executing.Termination)
			{
				case Termination.Crossing when executing.Lnav is Direct d:
					// Aircraft is proceeding direct to a fix.
					Aircraft current = _server.Aircraft[kvp.Key].Extrapolate(DateTimeOffset.Now);
					Aircraft prev = current.Extrapolate(DateTimeOffset.Now - delta);
					float currentEpBearing = (d.Endpoint.GetBearingDistance(current.Position.Position).bearing ?? 0) + 360 % 360;
					float prevEpBearing = (d.Endpoint.GetBearingDistance(prev.Position.Position).bearing ?? 0) + 360 % 360;
					float currentOffset = (currentEpBearing - current.Position.Heading) + 360 % 360;
					float prevOffset = (prevEpBearing - prev.Position.Heading) + 360 % 360;
					static bool isPast(float offset) => offset is > 90 and < 270;

					// If it's crossed the fix, it's done.
					if (isPast(currentOffset) && !isPast(prevOffset))
						_pendingSkips.Add(kvp.Key);
					continue;

				case Termination.Altitude:
					if (executing.Altitude.IsCompliant(kvp.Value.Extrapolate(DateTimeOffset.Now).Position.Altitude))
						_pendingSkips.Add(kvp.Key);
					continue;

				case Termination.Crossing:
				case Termination.Forever:
					continue;

				default:
					// Just in case I implement a new termination later and forget to update this.
					throw new NotImplementedException();
			}
		}

		foreach (Guid ac in _pendingSkips)
		{
			// Clear out any ongoing changes so it picks up the new one.
			_ongoingInstructions.Remove(ac);

			if (!_filedRoutes.TryGetValue(ac, out var instructions) || instructions.Count == 0)
				// All done!
				continue;

			instructions.Dequeue();

			if (!instructions.TryPeek(out Instruction? next))
				continue;

			_ongoingInstructions[ac] = next;
		}

		_pendingSkips.Clear();

		foreach (var (ac, i) in _ongoingInstructions)
		{
			Aircraft current = _server.Aircraft[ac].Extrapolate(DateTimeOffset.Now);
			bool modified = false;

			switch (i.Lnav)
			{
				case Heading h when current.Position.Heading != h.Degrees:
					// Turn at standard rate towards the planned heading.
					bool headingClockwise = ((h.Degrees - current.Position.Heading + 180) % 360 - 180) > 0;
					modified |= current.Movement.TurnRate != (headingClockwise ? 3 : -3);
					current = current with { Movement = current.Movement with { TurnRate = headingClockwise ? 3 : -3 } };
					break;

				case Direct d:
					// Check if we're already pointing the right way.
					float bearingToStation = current.Position.Position.GetBearingDistance(d.Endpoint).bearing ?? current.Position.Heading;
					if (Math.Abs(bearingToStation - current.Position.Heading) < 1f)
					{
						modified |= current.Movement.TurnRate != 0;
						current = current with { Movement = current.Movement with { TurnRate = 0 } };
						break;
					}

					// Otherwise, turn at standard rate towards the endpoint.
					bool directClockwise = ((bearingToStation - current.Position.Heading + 180) % 360 - 180) > 0;
					modified |= current.Movement.TurnRate != (directClockwise ? 3 : -3);
					current = current with { Movement = current.Movement with { TurnRate = directClockwise ? 3 : -3 } };
					break;

				default:
					// Make sure we're not over-turning.
					modified |= current.Movement.TurnRate != 0;
					current = current with { Movement = current.Movement with { TurnRate = 0 } };
					break;
			}

			if (i.Altitude.IsCompliant(current.Position.Altitude))
			{
				// Altitude is all good. Don't overshoot!
				modified |= current.Movement.ClimbRate != 0;
				current = current with { Movement = current.Movement with { ClimbRate = 0 } };
			}
			else
			{
				// 1000 fpm up, 500 fpm down.
				int expectedClimbRate = current.Position.Altitude < (i.Altitude.MinimumMSL ?? int.MinValue) ? 1000 : -500;

				if (expectedClimbRate == current.Movement.ClimbRate)
					break;

				modified = true;
				current = current with { Movement = current.Movement with { ClimbRate = expectedClimbRate } };
			}

			if (!i.Speed.IsCompliant(current.Movement.Speed))
			{
				// 10 kts per second acceleration, 5 kts per second deceleration.
				int accelDecelRate = (int)Math.Ceiling((current.Movement.Speed < (i.Speed.MinimumKnots ?? 0) ? 10 : -5) * delta.TotalSeconds);
				uint expectedSpeed = (uint)Math.Max(Math.Min(i.Speed.MaximumKnots ?? uint.MaxValue, current.Movement.Speed + accelDecelRate), i.Speed.MinimumKnots ?? uint.MinValue);

				if (expectedSpeed == current.Movement.Speed)
					break;

				modified = true;
				current = current with { Movement = current.Movement with { Speed = expectedSpeed } };
			}

			if (modified && !_server.UpdateAircraft(ac, current))
			{
				// Couldn't find the plane. Probably got destroyed. Clean it up.
				_ongoingInstructions.Remove(ac);
				_filedRoutes.Remove(ac);
			}
		}

		return Task.CompletedTask;
	}

	/// <summary>Sets the <paramref name="route"/> for a given <paramref name="aircraft"/>.</summary>
	/// <param name="aircraft">The <see cref="Guid"/> of the aircraft whose route is being planned.</param>
	/// <param name="route">A sequence of <see cref="Instruction">Instructions</see> to follow.</param>
	public void SetRoute(Guid aircraft, IEnumerable<Instruction> route)
	{
		_filedRoutes[aircraft] = new(route);

		if (_filedRoutes[aircraft].TryPeek(out Instruction? i))
			_ongoingInstructions[aircraft] = i;
	}

	/// <summary>Instructs the given <paramref name="aircraft"/> to move on to the next instruction.</summary>
	public void Skip(Guid aircraft)
	{
		if (!_filedRoutes.TryGetValue(aircraft, out var curRoute))
			return;

		if (!_pendingSkips.Add(aircraft))
			// Trying to skip multiple... Not sure if we want to batch these to prevent it, but for now just let them do it.
			curRoute.Dequeue();
	}
}