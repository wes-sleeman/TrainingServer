namespace TrainingServer.Extensibility.Parallel;

public static partial class Extensions
{
	public static float[] DistancesTo(this Coordinate coord, IEnumerable<Coordinate> others)
	{
		float2 refPoint = new(coord.Longitude, coord.Latitude);

		using ReadOnlyBuffer<float2> inputBuffer = GraphicsDevice.GetDefault().AllocateReadOnlyBuffer([.. others.Select(c => new float2(c.Longitude, c.Latitude))]);
		float[] answer = new float[inputBuffer.Length];
		using ReadWriteBuffer<float> outputBuffer = GraphicsDevice.GetDefault().AllocateReadWriteBuffer<float>(answer.Length);
		GraphicsDevice.GetDefault().For(answer.Length, new GetDistanceTo(refPoint, inputBuffer, outputBuffer));
		outputBuffer.CopyTo(answer);
		return answer;
	}
}

[ThreadGroupSize(DefaultThreadGroupSizes.X)]
[GeneratedComputeShaderDescriptor]
internal readonly partial struct GetDistanceTo(float2 reference, ReadOnlyBuffer<float2> inputs, ReadWriteBuffer<float> outputs) : IComputeShader
{
	const float R = 3440.07f;
	static readonly float DEG_TO_RAD = (float)(Math.Tau / 360);

	public void Execute()
	{
		float2 other = inputs[ThreadIds.X];

		float dlat = (other.Y - reference.Y) * DEG_TO_RAD,
			  dlon = (other.X - reference.X) * DEG_TO_RAD;

		float lat1 = reference.Y * DEG_TO_RAD,
			   lat2 = other.Y * DEG_TO_RAD;

		float sinDLatOver2 = Hlsl.Sin(dlat / 2),
			   sinDLonOver2 = Hlsl.Sin(dlon / 2);

		float a = sinDLatOver2 * sinDLatOver2 +
				   sinDLonOver2 * sinDLonOver2 * Hlsl.Cos(lat1) * Hlsl.Cos(lat2);

		float c = 2 * Hlsl.Atan2(Hlsl.Sqrt(a), Hlsl.Sqrt(1 - a));

		outputs[ThreadIds.X] = R * c;
	}
}