using System.Net.Http.Json;

using TrainingServer.Networking;

namespace TrainingServer.Connector;

internal class HubNegotiator(CancellationToken token = default)
{
	public const string HUB_ADDRESS = "hub.wsleeman.com:5031";

	public event Action<ServerInfo[]>? ServerListUpdated;

	readonly CancellationToken _token = token;

	public async Task SyncServersAsync()
	{
		HttpClient cli = new();

		while (!_token.IsCancellationRequested)
		{
			if (await cli.GetFromJsonAsync<ServerInfo[]>($"http://{HUB_ADDRESS}/servers") is ServerInfo[] info)
				ServerListUpdated?.Invoke(info);

			await Task.Delay(5000);
		}
	}
}
