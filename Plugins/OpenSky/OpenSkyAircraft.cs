using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;

using TrainingServer.Extensibility;

namespace OpenSky;

[JsonConverter(typeof(OpenSkyAircraftJsonConverter))]
public class OpenSkyAircraft(IEnumerable<Aircraft> aircraft)
{
	public ImmutableArray<Aircraft> Aircraft { get; } = [.. aircraft];

	public class OpenSkyAircraftJsonConverter : JsonConverter<OpenSkyAircraft>
	{
		public override OpenSkyAircraft? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
		{
			if (reader.TokenType != JsonTokenType.StartObject || !reader.Read() ||
				reader.TokenType != JsonTokenType.PropertyName || reader.GetString() != "time" || !reader.Read() ||
				reader.TokenType != JsonTokenType.Number || !reader.TryGetInt32(out int timestampInt) || !reader.Read() ||
				reader.TokenType != JsonTokenType.PropertyName || reader.GetString() != "states" || !reader.Read())
				throw new JsonException();

			if (reader.TokenType == JsonTokenType.Null)
			{
				if (!reader.Read())
					throw new JsonException();

				return new([]);
			}
			else if (reader.TokenType != JsonTokenType.StartArray)
				throw new JsonException();

			DateTimeOffset defaultTimestamp = DateTimeOffset.FromUnixTimeSeconds(timestampInt);
			List<Aircraft> aircraft = [];

			while (reader.Read() && reader.TokenType == JsonTokenType.StartArray && reader.Read())
			{
				if (reader.GetString() is not string icao24 || !reader.Read() ||
					reader.GetString() is not string callsign || !reader.Read() ||
					reader.GetString() is not string originCountry || !reader.Read())
					throw new JsonException();

				if (!reader.TryGetInt32(out int timePosition))
					timePosition = timestampInt;
				if (!reader.Read())
					throw new JsonException();

				if (!reader.TryGetInt32(out int lastContact))
					lastContact = timestampInt;
				if (!reader.Read() ||
					reader.TokenType != JsonTokenType.Number || !reader.TryGetSingle(out float longitude) || !reader.Read() ||
					reader.TokenType != JsonTokenType.Number || !reader.TryGetSingle(out float latitude) || !reader.Read() ||
					reader.TokenType != JsonTokenType.Number || !reader.TryGetSingle(out float baroAltMetres) || !reader.Read())
				{
					while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
						;

					continue;
				}

				bool onGround = reader.GetBoolean();

				if (!reader.Read() ||
					reader.TokenType != JsonTokenType.Number || !reader.TryGetSingle(out float velocityMetres) || !reader.Read() ||
					reader.TokenType != JsonTokenType.Number || !reader.TryGetSingle(out float headingTrue) || !reader.Read() ||
					reader.TokenType != JsonTokenType.Number || !reader.TryGetSingle(out float vertRateMetres) || !reader.Read())
				{
					while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
						;

					continue;
				}

				if (reader.TokenType != JsonTokenType.Null)
				{
					while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
						;
				}

				if (!reader.Read() ||
					!reader.TryGetSingle(out float geoAltitudeMetres) || !reader.Read())
					throw new JsonException();

				if (!ushort.TryParse(reader.GetString(), out ushort squawk))
					squawk = 2000;

				if (!reader.Read())
					throw new JsonException();

				bool spi = reader.GetBoolean();

				while (reader.TokenType != JsonTokenType.EndArray)
					if (!reader.Read())
						throw new JsonException();

				aircraft.Add(new(
					metadata: new(callsign.TrimEnd(), "ZZZZ", "ZZZZ", squawk == 1200 ? FlightData.FlightRules.VFR : FlightData.FlightRules.IFR, "?", "?", "RMK/Courtesy OpenSky"),
					position: new(headingTrue, (int)(baroAltMetres * 3.2808f), new(latitude, longitude), new(squawk, Squawk.SquawkMode.Altitude)),
					movement: new((uint)(velocityMetres * 1.9438f), (int)(vertRateMetres * 3.2808f), 0f)
				) { Time = DateTimeOffset.FromUnixTimeSeconds(timePosition) });
			}

			if (!reader.Read())
				throw new JsonException();

			return new(aircraft);
		}

		public override void Write(Utf8JsonWriter writer, OpenSkyAircraft value, JsonSerializerOptions options) => throw new NotImplementedException();
	}
}
