using TrainingServer.Extensibility;

namespace AirPlanner;

/// <summary>Encodes one single instruction to the planning engine.</summary>
/// <param name="Lnav">The direction in which the plane should fly.</param>
/// <param name="Altitude">The range of acceptable altitudes.</param>
/// <param name="Speed">The range of acceptable speeds.</param>
/// <param name="Termination">When the instruction is complete.</param>
public record Instruction(LnavInstruction Lnav, AltitudeRestriction Altitude, SpeedRestriction Speed, Termination Termination)
{
	public override string ToString()
	{
		List<string> pieces = [];

		if (Lnav is not PresentHeading)
			pieces.Add(Lnav.ToString());

		if (Altitude != AltitudeRestriction.Empty)
			pieces.Add(Altitude.ToString());

		if (Speed != SpeedRestriction.Empty)
			pieces.Add(Speed.ToString());

		return string.Join(". ", pieces.Select(p => char.ToUpperInvariant(p[0]) + p[1..])) + ".";
	}
}

/// <summary>The direction in which the aircraft may fly while executing this <see cref="Instruction"/>.</summary>
public abstract record LnavInstruction() { }
/// <summary>The aircraft will continue along whatever course it is already flying.</summary>
public record PresentHeading() : LnavInstruction
{
	public override string ToString() => "maintain present heading";
}

/// <summary>The aircraft will turn to and fly the specified heading.</summary>
/// <param name="Degrees">The true heading at which the aircraft will fly.</param>
public record Heading(float Degrees) : LnavInstruction
{
	public override string ToString() => $"{Degrees:000} degrees";
}

/// <summary>The aircraft will turn and fly towards the specified <see cref="Coordinate"/>.</summary>
/// <param name="Endpoint">The point towards which the aircraft will fly.</param>
public record Direct(Coordinate Endpoint) : LnavInstruction
{
	public override string ToString() => $"direct {Endpoint}";
}

/// <summary>The range of altitudes above sea level at which the aircraft may fly while executing this <see cref="Instruction"/>.</summary>
/// <param name="MinimumMSL">The minimum altitude at which the aircraft may fly, or <see langword="null"/> if no minimum.</param>
/// <param name="MaximumMSL">The maximum altitude at which the aircraft may fly, or <see langword="null"/> if no maximum.</param>
/// <remarks>Use <see cref="Empty"/> when no restriction is desired.</remarks>
public record AltitudeRestriction(int? MinimumMSL, int? MaximumMSL)
{
	public static AltitudeRestriction Empty => new(null, null);

	public bool IsCompliant(int altitude) => altitude switch {
		_ when (MinimumMSL ?? MaximumMSL) is null => true,
		int a when MinimumMSL is null => a <= MaximumMSL,
		int a when MaximumMSL is null => a >= MinimumMSL,
		int a => a <= MaximumMSL && a >= MinimumMSL
	};

	public override string ToString()
	{
		if ((MinimumMSL ?? MaximumMSL) is null)
			return "unrestricted";
		else if (MinimumMSL is null)
			return $"at or below {MaximumMSL!.Value / 100:000}{(MaximumMSL >= 18000 ? "" : $"00ft")}";
		else
			return $"at or above {MinimumMSL!.Value / 100:000}{(MinimumMSL >= 18000 ? "" : $"00ft")}";
	}
}

/// <summary>The range of speeds at which the aircraft may fly while executing this <see cref="Instruction"/>.</summary>
/// <param name="MinimumKnots">The minimum speed at which the aircraft may fly, or <see langword="null"/> if no minimum.</param>
/// <param name="MaximumKnots">The maximum speed at which the aircraft may fly, or <see langword="null"/> if no maximum.</param>
/// <remarks>Use <see cref="Empty"/> when no restriction is desired.</remarks>
public record SpeedRestriction(uint? MinimumKnots, uint? MaximumKnots)
{
	public static SpeedRestriction Empty => new(null, null);

	public bool IsCompliant(uint speed) => speed switch {
		_ when (MinimumKnots ?? MaximumKnots) is null => true,
		uint s when MinimumKnots is null => s <= MaximumKnots,
		uint s when MaximumKnots is null => s >= MinimumKnots,
		uint s => s <= MaximumKnots && s >= MinimumKnots
	};

	public override string ToString()
	{
		if ((MinimumKnots ?? MaximumKnots) is null)
			return "unrestricted";
		else if (MinimumKnots is null)
			return $"no faster than {MaximumKnots!.Value}kts";
		else
			return $"no slower than {MaximumKnots!.Value}kts";
	}
}

public enum Termination
{
	/// <summary>The instruction is terminated upon crossing the fix specified in the <see cref="Direct"/>.</summary>
	/// <remarks>Note: <see cref="Crossing"/> may only be used when the <see cref="LnavInstruction"/> is a <see cref="Direct"/>.
	/// Noncompliance will cause the <see cref="Instruction"/> to be automatically skipped.</remarks>
	Crossing,

	/// <summary>The instruction is terminated upon complying with the <see cref="AltitudeRestriction"/>.</summary>
	Altitude,

	/// <summary>The instruction will not terminate unless skipped.</summary>
	Forever
}