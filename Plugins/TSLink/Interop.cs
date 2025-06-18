using System.Text.Json.Serialization;

using TrainingServer.Extensibility;

namespace TSLink.Interop;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "$")]
[JsonDerivedType(typeof(ServerState), "sync")]
[JsonDerivedType(typeof(TextMessage), "pm")]
[JsonDerivedType(typeof(ChannelMessage), "txt")]
[JsonDerivedType(typeof(CreateAircraft), "addac")]
[JsonDerivedType(typeof(DeleteAircraft), "delac")]
internal record InteropMessage();

internal record ServerState(
	[property: JsonPropertyName("aircraft")] Dictionary<string, Aircraft> Aircraft,
	[property: JsonPropertyName("controllers")] Dictionary<string, Controller> Controllers
) : InteropMessage()
{
	public ServerState(IServer server) : this(
		server.Aircraft.ToDictionary(static kvp => kvp.Key.ToString(), static kvp => kvp.Value),
		server.Controllers.ToDictionary(static kvp => kvp.Key.ToString(), static kvp => kvp.Value)
	)
	{ }
}

internal record TextMessage(
	[property: JsonPropertyName("from")] string Sender,
	[property: JsonPropertyName("to")] string Recipient,
	[property: JsonPropertyName("msg")] string Message
) : InteropMessage;

internal record ChannelMessage(
	[property: JsonPropertyName("to")] decimal Channel,
	[property: JsonPropertyName("msg")] string Message
) : InteropMessage;

internal record CreateAircraft(
	[property: JsonPropertyName("aircraft")] Aircraft Aircraft
) : InteropMessage;

internal record DeleteAircraft(
	[property: JsonPropertyName("id")] string Aircraft
) : InteropMessage;