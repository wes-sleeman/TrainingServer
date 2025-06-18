using System.Text.Json.Serialization;

namespace TSLink;

public record Settings(
	[property: JsonPropertyName("run")] string Command,
	[property: JsonPropertyName("args")] string[] Arguments,
	[property: JsonPropertyName("env")] string LaunchDirectory,
	[property: JsonPropertyName("hide")] bool HideWindow = true
);