using System;
using System.Linq;
using System.Text.Json.Serialization;

namespace TrainingServer.Extensibility;

public record Controller(
	[property: JsonPropertyName("time")] DateTimeOffset Time, 
	[property: JsonPropertyName("meta")] ControllerData Metadata, 
	[property: JsonPropertyName("pos")] ControllerSnapshot Position
)
{
	public Controller(ControllerData metadata, ControllerSnapshot position) : this(DateTimeOffset.Now, metadata, position) { }

	public virtual bool Equals(Controller? other) => other is Controller o && GetHashCode() == o.GetHashCode();
	public override int GetHashCode() => HashCode.Combine(Metadata.Callsign, Position.RadarAntennae?.Aggregate(0, (s, i) => HashCode.Combine(s, i.Latitude, i.Longitude)) ?? 0);
}

public record struct ControllerSnapshot(
	[property: JsonPropertyName("antennae")] Coordinate[] RadarAntennae
) { }

public record struct ControllerData(
	[property: JsonPropertyName("facility")] string Facility, 
	[property: JsonPropertyName("type")] ControllerData.Level Type, 
	[property: JsonPropertyName("discriminator")] string? Discriminator = null
)
{
	[JsonIgnore]
	public readonly string Callsign => $"{Facility}{(Discriminator is string s ? "_" + s : "")}_{Enum.GetName(Type)}";

	public override readonly string ToString() => Callsign;

	[JsonConverter(typeof(JsonStringEnumConverter<Level>))]
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