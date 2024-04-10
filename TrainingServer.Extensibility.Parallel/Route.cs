namespace TrainingServer.Extensibility.Parallel;

public static partial class Extensions
{
	public static IEnumerable<bool> GetFilterMask(IEnumerable<float2> coords, float bottom, float top, float left, float right)
	{
		float2[] points = [.. coords];
		using var input = GraphicsDevice.GetDefault().AllocateReadOnlyBuffer(points);
		using var output = GraphicsDevice.GetDefault().AllocateReadWriteBuffer<int>(input.Length);
		GraphicsDevice.GetDefault().For(points.Length, new SingleOnScreen(input, output, bottom, top, left, right));
		return output.ToArray().Select(b => b == 1);
	}

	public static IEnumerable<Coordinate> FilterOnScreen(IEnumerable<Coordinate> coords, float bottom, float top, float left, float right)
	{
		Coordinate[] coordPoints = [.. coords];
		bool[] hits = [.. GetFilterMask(coordPoints.Select(s => new float2(s.Longitude, s.Latitude)), bottom, top, left, right)];
		return Enumerable.Range(0, hits.Length).Where(idx => hits[idx]).Select(idx => coordPoints[idx]);
	}

	public static Route FilterOnScreen(this Route route, float bottom, float top, float left, float right)
	{
		if (!route.Any() || (route.Count() == 1 && route[0].Point.Latitude < top && route[0].Point.Latitude > bottom && route[0].Point.Longitude < right && route[0].Point.Longitude > left))
			return route;
		else if (route.Count() == 1)
			return new(route.Name, segments: []);

		return Route.FilterInternal(route, [.. GetFilterMask(route.Select(s => new float2(s.Point.Longitude, s.Point.Latitude)), bottom, top, left, right)]);
	}

	public static IEnumerable<Route> FilterOnScreen(IEnumerable<Route> routeSource, float bottom, float top, float left, float right)
	{
		Route[] routes = [.. routeSource];
		float2[][] routePoints = [.. routes.Select(r => r.Points.Select(p => new float2(p.Longitude, p.Latitude)).ToArray())];
		int[] lengths = [.. routePoints.Select(r => r.Length)];
		int2[] ranges = new int2[lengths.Length];

		for (int idx = 0; idx < lengths.Length; ++idx)
			ranges[idx] = new(idx == 0 ? 0 : ranges[idx - 1].Y, idx == 0 ? lengths[0] : ranges[idx - 1].Y + lengths[idx]);

		float2[] allPoints = [.. routePoints.SelectMany(p => p)];
		int[] pointsOnScreen = new int[allPoints.Length];

		GraphicsDevice gpu = GraphicsDevice.GetDefault();

		using ReadOnlyBuffer<float2> allPointsBuffer = gpu.AllocateReadOnlyBuffer(allPoints);
		using ReadWriteBuffer<int> pointsOnScreenBuffer = gpu.AllocateReadWriteBuffer(pointsOnScreen);
		using ReadOnlyBuffer<int2> routeRangesBuffer = gpu.AllocateReadOnlyBuffer(ranges);
		using ReadWriteBuffer<int> routesOnScreenBuffer = gpu.AllocateReadWriteBuffer<int>(ranges.Length);

		using (ComputeContext context = gpu.CreateComputeContext())
		{
			context.For(allPoints.Length, new SingleOnScreen(allPointsBuffer, pointsOnScreenBuffer, bottom, top, left, right));
			context.For(ranges.Length, new RoutesOnScreen(pointsOnScreenBuffer, routeRangesBuffer, routesOnScreenBuffer));
		}

		pointsOnScreenBuffer.CopyTo(pointsOnScreen);
		bool[] routesOnScreen = [.. routesOnScreenBuffer.ToArray().Select(b => b == 1)];

		return Enumerable.Range(0, ranges.Length).AsParallel().AsUnordered().Where(idx => routesOnScreen[idx]).Select(idx =>
		{
			Route route = routes[idx];
			if (!route.Any() || (route.Count() == 1 && route[0].Point.Latitude < top && route[0].Point.Latitude > bottom && route[0].Point.Longitude < right && route[0].Point.Longitude > left))
				return route;
			else if (route.Count() == 1)
				return new(route.Name, segments: []);

			return Route.FilterInternal(route, [.. pointsOnScreen[ranges[idx].X..ranges[idx].Y].Select(p => p == 1)]);
		});
	}
}

[ThreadGroupSize(DefaultThreadGroupSizes.X)]
[GeneratedComputeShaderDescriptor]
internal readonly partial struct SingleOnScreen(ReadOnlyBuffer<float2> input, ReadWriteBuffer<int> output, float bottom, float top, float left, float right) : IComputeShader
{
	public void Execute()
	{
		float2 point = input[ThreadIds.X];

		if (point.Y > bottom && point.Y < top && point.X > left && point.X < right)
			output[ThreadIds.X] = 1;
		else
			output[ThreadIds.X] = 0;
	}
}

[ThreadGroupSize(DefaultThreadGroupSizes.X)]
[GeneratedComputeShaderDescriptor]
internal readonly partial struct RoutesOnScreen(ReadWriteBuffer<int> pointsOnScreen, ReadOnlyBuffer<int2> procGroupRanges, ReadWriteBuffer<int> onScreen) : IComputeShader
{
	public void Execute()
	{
		int2 routeStartEnd = procGroupRanges[ThreadIds.X];
		bool isOnScreen = false;

		for (int idx = routeStartEnd.X; !isOnScreen && idx < routeStartEnd.Y; ++idx)
			isOnScreen |= pointsOnScreen[idx] == 1;

		onScreen[ThreadIds.X] = isOnScreen ? 1 : 0;
	}
}