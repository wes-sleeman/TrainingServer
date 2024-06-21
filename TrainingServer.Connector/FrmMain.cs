using TrainingServer.Networking;

namespace TrainingServer.Connector;

public partial class FrmMain : Form
{
	readonly CancellationTokenSource _cts = new();
	readonly HubNegotiator _hub;
	readonly ServerNegotiator _server;
	readonly FsdNegotiator _client;

	public FrmMain()
	{
		_hub = new(_cts.Token);
		_server = new(_cts.Token);
		_client = new(_cts.Token);
		InitializeComponent();

		_hub.ServerListUpdated += UpdateServers;
		_ = _hub.SyncServersAsync();

		_server.PacketReceived += ServerPacketReceived;
		_client.PacketReceived += FsdPacketReceived;
	}

	void UpdateServers(ServerInfo[] servers)
	{
		if (InvokeRequired)
		{
			Invoke(() => UpdateServers(servers));
			return;
		}

		LbxServerList.BeginUpdate();

		HashSet<ServerInfo> oldServers = LbxServerList.Items.Cast<ServerInfo>().ToHashSet();

		foreach (ServerInfo server in oldServers.Except(servers))
			LbxServerList.Items.Remove(server);

		LbxServerList.Items.AddRange(servers.Except(oldServers).Cast<object>().ToArray());

		LbxServerList.EndUpdate();
	}

	private void LbxServerList_SelectedIndexChanged(object sender, EventArgs e)
	{
		if (LbxServerList.SelectedItem is not ServerInfo selectedServer)
		{
			_server.Disconnect();
			return;
		}

		if (selectedServer == _server.SelectedServer)
			return;

		_ = _server.ConnectAsync(selectedServer);
	}

	private void ServerPacketReceived(DateTimeOffset sent, NetworkMessage message)
	{
		static uint TupleToPBH((decimal Pitch, decimal Bank, decimal Heading, bool OnGround) PBH)
		{
			uint TenBitMask = 0b00000011_11111111;

			static ushort scale(decimal value) => (ushort)((value + 360) % 360 * 128m / 45);

			uint retval = scale(PBH.Pitch) & TenBitMask;
			retval <<= 10;
			retval += scale(PBH.Bank) & TenBitMask;
			retval <<= 10;
			retval += scale(PBH.Heading) & TenBitMask;
			retval <<= 1;
			retval += (uint)(PBH.OnGround ? 1 : 0);
			retval <<= 1;

			return retval;
		}

		switch (message)
		{
			case AircraftUpdate acu:
				_client.Aircraft.AddOrUpdate(acu.Aircraft, acu.ToAircraft(), (_, ac) => ac + acu);
				var ac = _client.Aircraft[acu.Aircraft].Extrapolate(DateTimeOffset.Now.AddSeconds(-1));
				_client.Send($"@{ac.Position.Squawk.Mode switch {
					Extensibility.Squawk.SquawkMode.Altitude => "N",
					_ => "S"
				}}{ac.Metadata.Callsign}:{ac.Position.Squawk.Code:0000}::{ac.Position.Position.Latitude}:{ac.Position.Position.Longitude}:{ac.Position.Altitude}:{ac.Movement.Speed}:{TupleToPBH((0, 0, (decimal)ac.Position.Heading, false))}:0");
				break;

			case ControllerUpdate cu:
				_client.Controllers.AddOrUpdate(cu.Controller, cu.ToController(), (_, c) => c + cu);
				break;

			case AuthoritativeUpdate au:
				_client.Aircraft.Clear();
				_client.Controllers.Clear();

				foreach (var update in au.Aircraft.Cast<NetworkMessage>().Concat(au.Controllers))
					ServerPacketReceived(sent, update);

				break;

			case KillMessage k when k.Victim == _server.Me:
				_server.Disconnect();
				break;

			case ChannelMessage cm:
				_client.MessageReceived(_client.GetCallsign(cm.From), $"@{(cm.Frequency - 100) * 1000:00000}", $"{_client.GetCallsign(cm.To)}, {cm.Message}");
				break;

			case TextMessage tm:
				_client.MessageReceived(_client.GetCallsign(tm.From), _client.GetCallsign(tm.To), tm.Message);
				break;

			default:
				System.Diagnostics.Debug.WriteLine($"Unknown message: {message}");
				break;
		}
	}

	private void FsdPacketReceived(StreamWriter sender, string[] packetSegments)
	{
		if (packetSegments.Length == 0)
			return;

		switch (packetSegments[0])
		{
			case "#":
				string addCommand = packetSegments[1][..2];
				string addCallsign = packetSegments[1][2..];
				string addRecipient = packetSegments[2];

				switch (addCommand)
				{
					case "AA" when addRecipient == "SERVER" && int.TryParse(packetSegments[4], out int _):
						_client.Callsign = addCallsign;

						string[] callsignSegs = addCallsign.Split('_');
						if (!Enum.TryParse<Extensibility.ControllerData.Level>(callsignSegs.Last(), out var level))
							break;

						_server.User = new(new(callsignSegs[0], level, callsignSegs.Length > 2 ? string.Join("_", callsignSegs[1..^1]) : null), new());
						_server.SendPositionAsync();

						_client.Controllers[_server.Me] = _server.User;
						break;

					case "TM" when addRecipient.StartsWith('@') && addRecipient.Length == 6 && decimal.TryParse($"1{addRecipient[1..3]}.{addRecipient[3..]}", out decimal addChannelTextFrequency):
						_server.SendTextAsync(new ChannelMessage(_server.Me, addChannelTextFrequency, packetSegments[3].Replace(":", "$C")));
						break;

					case "TM":
						foreach (Guid recipient in _client.GetGuids(addRecipient))
							_server.SendTextAsync(new(_server.Me, recipient, packetSegments[3].Replace(":", "$C")));
						break;

					case "AC":
					case "AT":
						// ATIS.
						break;

					default: throw new NotImplementedException();
				}
				break;

			case "%" when _server.User is not null:
				string posCallsign = packetSegments[1];
				if (!decimal.TryParse($"1{packetSegments[2][..2]}.{packetSegments[2][2..]}", out decimal posFrequency))
					posFrequency = 122.8m;

				Extensibility.Coordinate posNewPos = new(float.Parse(packetSegments[6]), float.Parse(packetSegments[7])),
										 posOldPos = _server.User.Position.RadarAntennae is null ? new() : _server.User.Position.RadarAntennae.FirstOrDefault();

				_server.User = new(_server.User.Metadata, new([posNewPos]));
				_server.SendPositionAsync();
				break;

			case "%":
				// Preemptive position report. Ignore.
				break;

			case "$":
				// Not really sure what to do with these. Ignore them for now.
				break;

			case "=":
				string assumeCallsign = packetSegments[1][1..];
				string assumedCallsign = packetSegments[2];
				switch (packetSegments[1][0])
				{
					case 'A':
						// Assume aircraft.
						_client.Send($"=A{assumeCallsign}:{assumedCallsign}");
						break;

					case 'R':
						// Release aircraft.
					case 'F':
						// Cleared flight level.
						break;

					default: throw new NotImplementedException();
				}
				break;

			case "!":
				switch (packetSegments[1][0])
				{
					case 'C':
						// Sign in.
						_client.Send($"!RSERVER:{packetSegments[1][1..]}:B:0:12:12:127.0.0.1");
						break;

					default: throw new NotImplementedException();
				}
				break;

			default:
				throw new NotImplementedException();
		}
	}
}
