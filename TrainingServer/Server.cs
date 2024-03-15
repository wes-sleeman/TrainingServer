using System.Collections.Concurrent;
using System.Collections.Immutable;

using TrainingServer.Extensibility;
using TrainingServer.Networking;

namespace TrainingServer;

internal class Server : IServer
{
	public event Action<AircraftUpdate[]>? OnAircraftUpdated;
	public event Action<Guid>? OnAircraftAdded;
	public event Action<ControllerUpdate>? OnControllerUpdated;
	public event Action<Guid>? OnControllerAdded;
	public event Action<TextMessage>? OnTextMessageReceived;

	public IDictionary<Guid, Aircraft> Aircraft => _aircraft;
	public IDictionary<Guid, Controller> Controllers => _controllers;

	private ImmutableDictionary<Guid, Aircraft> _aircraft = ImmutableDictionary<Guid, Aircraft>.Empty;
	private readonly ConcurrentDictionary<Guid, Controller> _controllers = new();

	private readonly ConcurrentDictionary<Guid, AircraftUpdate> _updateBatch = new();

	public object? ResolveGuid(Guid guid)
	{
		if (_aircraft.TryGetValue(guid, out var ac))
			return ac;
		else if (_controllers.TryGetValue(guid, out var c))
			return c;
		else
			return null;
	}

	public Guid AddAircraft(Aircraft newAircraft)
	{
		_batchingEvent.Wait();
		Guid newGuid = Guid.NewGuid();

		_updateBatch.TryAdd(newGuid, new(newGuid, newAircraft));

		return newGuid;
	}

	public IDictionary<Guid, Aircraft> GetAircraftByCallsign(string callsign) => Aircraft.Where(ac => ac.Value.Metadata.Callsign.Equals(callsign, StringComparison.InvariantCultureIgnoreCase)).ToImmutableDictionary();

	public bool RemoveAircraft(Guid aircraftId)
	{
		_batchingEvent.Wait();

		_updateBatch.TryRemove(aircraftId, out _);
		_updateBatch[aircraftId] = new(aircraftId, UserUpdate.UpdatedField.Delete, null, null, null);

		return _aircraft.ContainsKey(aircraftId);
	}

	public Guid[] RemoveAircraft(string callsign) =>
		Aircraft.Where(kvp => kvp.Value.Metadata.Callsign.Equals(callsign, StringComparison.InvariantCultureIgnoreCase))
				.Select(kvp => kvp.Key)
				.Where(RemoveAircraft)
				.ToArray();

	public bool UpdateAircraft(Guid aircraftId, Aircraft newData)
	{
		_batchingEvent.Wait();

		if (!Aircraft.TryGetValue(aircraftId, out Aircraft? ac))
			return false;

		_updateBatch.TryGetValue(aircraftId, out AircraftUpdate? acUpd);
		acUpd ??= new(aircraftId);
		acUpd += AircraftUpdate.Difference(aircraftId, ac, newData);
		_updateBatch[aircraftId] = acUpd;

		return true;
	}

	public async Task MessageReceivedAsync(NetworkMessage message)
	{
		_batchingEvent.Wait();

		switch (message)
		{
			case ControllerUpdate cupd:
				if (_controllers.TryGetValue(cupd.Controller, out var c))
				{
					_controllers[cupd.Controller] = c + cupd;
					OnControllerUpdated?.Invoke(cupd);
				}
				else
				{
					_controllers[cupd.Controller] = cupd.ToController();
					OnControllerAdded?.Invoke(cupd.Controller);
				}
				break;

			case UserUpdate:
				throw new ArgumentException("Not expecting aircraft/authoritative updates from client.", nameof(message));

			case TextMessage txt:
				OnTextMessageReceived?.Invoke(txt);
				break;

			case KillMessage k:
				_updateBatch[k.Victim] = new(k.Victim, UserUpdate.UpdatedField.Delete, null, null, null);
				break;
		}

		await Task.CompletedTask;
	}

	private readonly ManualResetEventSlim _batchingEvent = new(true);
	internal void CommitBatch()
	{
		_batchingEvent.Wait();
		_batchingEvent.Reset();
		Dictionary<Guid, Aircraft> newAircraft = new(_aircraft);

		AircraftUpdate[] updates = [.._updateBatch.Values];
		_updateBatch.Clear();
		_batchingEvent.Set();

		foreach (AircraftUpdate update in updates)
		{
			if (update.Update.HasFlag(UserUpdate.UpdatedField.Delete))
				continue;

			if (_aircraft.TryGetValue(update.Aircraft, out var oldAc))
				newAircraft[update.Aircraft] = oldAc + update;
			else
			{
				newAircraft[update.Aircraft] = update.ToAircraft();
				OnAircraftAdded?.Invoke(update.Aircraft);
			}
		}

		_aircraft = newAircraft.Where(na => !updates.Any(u => na.Key == u.Aircraft && u.Update.HasFlag(UserUpdate.UpdatedField.Delete))).ToImmutableDictionary();

		if (updates.Length != 0)
			OnAircraftUpdated?.Invoke(updates);
	}
}
