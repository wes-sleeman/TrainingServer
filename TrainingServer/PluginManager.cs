using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Reflection;

using TrainingServer.Extensibility;

namespace TrainingServer;

internal class PluginManager
{
	public event Action? OnPluginsListUpdated;

	public HashSet<string> SearchPath = [ ".",
#if DEBUG
		"../../../../plugins"
#else
		"./plugins"
#endif
	];
	public readonly ConcurrentDictionary<IPlugin, bool> _loadedPlugins = new();

	readonly IServer _managingServer;

	readonly Dictionary<string, DateTime> _loadedPluginTimes = [];
	readonly Dictionary<Type, object> _injectionDict;

	public PluginManager(IServer managingServer)
	{
		_managingServer = managingServer;

		_injectionDict = new() {
			{ typeof(IServer), _managingServer },
			{ typeof(Aircraft[]), () => _managingServer.Aircraft.Values.ToArray() }
		};

		Task.Run(ScanPluginsAsync);
	}

	public Task TickAsync(TimeSpan delta) => Task.WhenAll(
		_loadedPlugins.Where(kvp => kvp.Value).AsParallel().Select(async kvp =>
		{
			try
			{
				await kvp.Key.TickAsync(delta);
			}
			catch (Exception ex)
			{
				System.Diagnostics.Debug.WriteLine($"Exception in plugin {kvp.Key}: {ex.Message}.\nStack Trace:\n{ex.StackTrace}");
			}
		})
	);

	public Task ProcessTextMessageAsync(string sender, string recipient, string message) => Task.WhenAll(
		_loadedPlugins.Where(kvp => kvp.Value).AsParallel().Select(kvp => kvp.Key.ProcessTextMessageAsync(sender, recipient, message))
	);

	/// <summary>Scans for plugins being added/removed/updated.</summary>
	private async Task ScanPluginsAsync()
	{
		while (true)
		{
			HashSet<string> locatedDlls = [];

			// Find _EVERYTHING_.
			foreach (string path in SearchPath)
			{
				if (!Directory.Exists(path))
					continue;

				locatedDlls.UnionWith(Directory.EnumerateFiles(Path.GetFullPath(path), "*.dll", SearchOption.AllDirectories));
			}

			// Remove anything unmodified since the last sweep.
			foreach (string path in locatedDlls)
			{
				if (_loadedPluginTimes.TryGetValue(path, out var oldTime) && File.GetLastWriteTime(path) == oldTime)
				{
					locatedDlls.Remove(path);
					continue;
				}

				_loadedPluginTimes[path] = File.GetLastWriteTime(path);
			}

			// Reflect the DLLs into something we can use.
			HashSet<Type> pluginTypes = [];
			HashSet<Assembly> loadedAssemblies = [];

			foreach (string path in locatedDlls)
			{
				try
				{
					Assembly asm = Assembly.LoadFile(path);
					loadedAssemblies.Add(asm);

					pluginTypes.UnionWith(asm.GetExportedTypes().Where(et => et.GetInterfaces().Contains(typeof(IPlugin))));
				}
				catch (Exception) { /* Oh so many things can go wrong here! */ }
			}

			AppDomain.CurrentDomain.AssemblyResolve += (object? _, ResolveEventArgs e) => {
				foreach (Assembly asm in loadedAssemblies)
					if (asm.FullName == e.Name)
						return asm;

				return null;
			};

			pluginTypes = pluginTypes.DistinctBy(t => t.AssemblyQualifiedName).ToHashSet();

			/// <summary>Generate a plugin from a given <see cref="ConstructorInfo"/>.</summary>
			IPlugin Instantiate(ConstructorInfo ci)
			{
				List<object> paramList = [];
				foreach (ParameterInfo pi in ci.GetParameters())
				{
					object v = _injectionDict[pi.ParameterType];

					if (v.GetType() == typeof(Func<>).MakeGenericType(pi.ParameterType))
						v = ((dynamic)v).Invoke();

					paramList.Add(v);
				}

				return (IPlugin)ci.Invoke([.. paramList]);
			}

			bool updated = pluginTypes.Count != 0;

			// Instantiate the types while building the injection dictionary.
			for (int iteration = pluginTypes.Count; pluginTypes.Count != 0 && iteration > 0; --iteration)
			{
				foreach (Type pluginType in pluginTypes)
				{
					if (pluginType.GetConstructors().OrderByDescending(c => c.GetParameters().Length).Where(c => c.GetParameters().All(pi => _injectionDict.ContainsKey(pi.ParameterType))).FirstOrDefault() is ConstructorInfo ci)
					{
						IPlugin plugin = Instantiate(ci);

						bool loadState = false;

						if (_injectionDict.FirstOrDefault(kvp => kvp.Key.FullName == pluginType.FullName).Value is IPlugin oldPlugin)
						{
							if (oldPlugin is IAsyncDisposable iadOp)
								await iadOp.DisposeAsync();
							else if (oldPlugin is IDisposable idOp)
								idOp.Dispose();

							_loadedPlugins.TryRemove(oldPlugin, out loadState);
						}

						loadState |= pluginType.GetCustomAttributes().Any(a => a is EnabledAttribute);

						_loadedPlugins[plugin] = loadState;
						_injectionDict[pluginType] = plugin;

						pluginTypes.Remove(pluginType);
					}
				}
			}

			if (pluginTypes.Count != 0)
				throw new TypeLoadException("Cannot load plugins due to missing dependencies: " + string.Join(", ", pluginTypes.Select(t => t.FullName)));

			if (updated)
				OnPluginsListUpdated?.Invoke();

			await Task.Delay(5000);
		}
	}
}
