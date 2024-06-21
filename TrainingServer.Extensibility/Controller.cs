using System;
using System.Linq;
using System.Text.Json.Serialization;

namespace TrainingServer.Extensibility;

public record Controller(DateTimeOffset Time, ControllerData Metadata, ControllerSnapshot Position)
{
	public Controller(ControllerData metadata, ControllerSnapshot position) : this(DateTimeOffset.Now, metadata, position) { }

	public virtual bool Equals(Controller? other) => other is Controller o && GetHashCode() == o.GetHashCode();
	public override int GetHashCode() => HashCode.Combine(Metadata.Callsign, Position.RadarAntennae?.Aggregate(0, (s, i) => HashCode.Combine(s, i.Latitude, i.Longitude)) ?? 0);
}

public record struct ControllerSnapshot(Coordinate[] RadarAntennae) { }

public record struct ControllerData(string Facility, ControllerData.Level Type, string? Discriminator = null)
{
	[JsonIgnore]
	public readonly string Callsign => $"{Facility}{(Discriminator is string s ? "_" + s : "")}_{Enum.GetName(Type)}";

	public override readonly string ToString() => Callsign;


	public enum Level
	{
		DEL,
		GND,
		TWR,
		APP,
		DEP,
		CTR,
		FSS
	}
}