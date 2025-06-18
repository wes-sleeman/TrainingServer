export type Guid = string;
export type Coordinate = number[];

export interface Settings {
	run: string,
	args: string[],
	env: string,
	hide?: boolean
}

export interface ServerMessage {
	$: string
}

export interface ErrorMessage extends ServerMessage {
	$: "err",
	msg: string
}

export interface Tick extends ServerMessage {
	$: "tick",
	deltaMs: number
}

export interface ServerState extends ServerMessage {
	$: 'sync',
	aircraft: Map<Guid, Aircraft>,
	controllers: Map<Guid, Controller>
}

export interface TextMessage extends ServerMessage {
	$: "pm",
	from: Guid,
	to: Guid,
	msg: string
}

export interface ChannelMessage extends ServerMessage {
	$: "txt",
	to: number,
	msg: string
}

export interface CreateAircraft extends ServerMessage {
	$: "addac",
	aircraft: Aircraft
}

export interface DeleteAircraft extends ServerMessage {
	$: "delac",
	id: Guid
}

export interface Aircraft {
	time: string,
	meta: FlightData,
	pos: AircraftSnapshot,
	delta: AircraftMotion
}

export interface FlightData {
	callsign: string,
	origin: string,
	dest: string,
	rules: 'VFR' | 'IFR' | 'Y' | 'Z',
	type: string,
	rte: string,
	rmk: string
}

export interface AircraftSnapshot {
	hdg: number,
	alt: number,
	pos: Coordinate,
	sqk: number
}

export interface AircraftMotion {
	spd: number,
	climb: number,
	turn: number
}

export interface Controller {
	time: string,
	meta: ControllerData,
	pos: ControllerSnapshot
}

export interface ControllerData {
	facility: string,
	type: 'DEL' | 'GND' | 'TWR' | 'APP' | 'DEP' | 'CTR' | 'FSS',
	discriminator: string | null
}

export interface ControllerSnapshot {
	antennae: Coordinate[]
}