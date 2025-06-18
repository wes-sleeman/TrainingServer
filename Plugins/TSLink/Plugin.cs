namespace TSLink;

using System;
using System.Diagnostics;
using System.Text.Json;
using System.Threading.Tasks;

using TrainingServer.Extensibility;

using TSLink.Interop;

public class Plugin : IPlugin, IDisposable
{
	const string SETTINGS_FILE_PATH = "ts-shim-settings.json";

	public string Name => "TypeScript Interop Shim";

	public string Description => "Allows for interfacing with TypeScript plugins over STDIO.";

	public string Maintainer => "Wes (644899)";

	public override string ToString() => ((IPlugin)this).ToString();

	private CancellationTokenSource _cts = new();
	private IServer _server;
	private StreamWriter? _writer = null;
	private Process? _nodeProcess = null;

	public Plugin(IServer server)
	{
		_server = server;

		if (!File.Exists(SETTINGS_FILE_PATH) || JsonSerializer.Deserialize<Settings>(File.ReadAllText(SETTINGS_FILE_PATH)) is not Settings settings)
		{
			File.WriteAllText(SETTINGS_FILE_PATH, JsonSerializer.Serialize(new Settings("node", [], ".", true)));
			Console.WriteLine($"ERROR! Missing or invalid {SETTINGS_FILE_PATH} file. Please go add your details here and re-activate the plugin.");
			return;
		}
		else if (!Directory.Exists(settings.LaunchDirectory))
		{
			Console.WriteLine($"ERROR! Invalid TS file launch directory {settings.LaunchDirectory}. Does it exist?");
			return;
		}

		_nodeProcess = Process.Start(new ProcessStartInfo(settings.Command, settings.Arguments) {
			UseShellExecute = true,
			RedirectStandardOutput = true,
			RedirectStandardInput = true,
			CreateNoWindow = settings.HideWindow,
			WorkingDirectory = settings.LaunchDirectory
		});

		if (_nodeProcess is null)
		{
			Console.WriteLine($"ERROR! Failed to start the TS program.");
			return;
		}

		Task.Run(async () => await ListenAsync(_nodeProcess.StandardOutput, _cts.Token));

		_writer = _nodeProcess.StandardInput;
		_writer.WriteLine("""{ "$": "init" }""");
	}

	public Task ProcessTextMessageAsync(string sender, string recipient, string message)
	{
		_writer?.WriteLine(JsonSerializer.Serialize<TextMessage>(new(sender, recipient, message)));
		return Task.CompletedTask;
	}

	TimeSpan _timeSinceLastServerSync = TimeSpan.Zero;
	public async Task TickAsync(TimeSpan delta)
	{
		_timeSinceLastServerSync += delta;
		if (_writer is null)
			return;

		await _writer.WriteLineAsync($$"""{ "$": "tick", "deltaMs": "{{delta.TotalMilliseconds}}" }""");

		if (_timeSinceLastServerSync < TimeSpan.FromSeconds(1))
			return;

		_timeSinceLastServerSync = TimeSpan.Zero;
		await _writer.WriteLineAsync(JsonSerializer.Serialize<ServerState>(new(_server)));
	}

	async Task ListenAsync(StreamReader reader, CancellationToken token)
	{
		while (!token.IsCancellationRequested && _nodeProcess?.HasExited is false)
		{
			if (await reader.ReadLineAsync(token) is not string input)
				// EOF or closed pipe or something like that.
				break;

			if (JsonSerializer.Deserialize<InteropMessage>(input) is not InteropMessage baseMessage)
			{
				// Invalid message
				await (_writer?.WriteLineAsync("""{ "$": "err", "msg": "Unrecognised message" }""") ?? Task.CompletedTask);
				continue;
			}

			if (baseMessage is TextMessage tm)
			{
				if (Guid.TryParse(tm.Sender, out var sender) && Guid.TryParse(tm.Recipient, out var recipient))
					_server.SendTextMessage(sender, recipient, tm.Message.Replace(":", "$C"));
				else
					await (_writer?.WriteLineAsync("""{ "$": "err", "msg": "Invalid text message" }""") ?? Task.CompletedTask);
			}
			else if (baseMessage is ChannelMessage cm)
				_server.SendChannelMessage(cm.Channel, cm.Message);
			else if (baseMessage is CreateAircraft ca)
			{
				Guid newAc = _server.AddAircraft(ca.Aircraft);
				await (_writer?.WriteLineAsync($$"""{ "$": "acadded", "id": "{{newAc}}" }""") ?? Task.CompletedTask);
			}
			else if (baseMessage is DeleteAircraft da && Guid.TryParse(da.Aircraft, out var delAcGuid))
				_server.RemoveAircraft(delAcGuid);
			else
				await (_writer?.WriteLineAsync("""{ "$": "err", "msg": "Unrecognised message" }""") ?? Task.CompletedTask);
		}
	}

	public void Dispose()
	{
		_cts.Cancel();
		_writer?.Dispose();

		if (_nodeProcess is null)
			return;

		_nodeProcess.Kill();
		_nodeProcess.WaitForExit();
	}
}
