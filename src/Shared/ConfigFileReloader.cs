using System;
using System.IO;
using System.Threading;
using BepInEx;
using BepInEx.Configuration;

namespace ReallyReflections.Shared;

// Edits to a plugin's .cfg apply while the game runs. Editors write in bursts (and FileSystemWatcher fires
// several times on Linux), so the reload waits until the file has been quiet for half a second, then runs
// on the main thread; values changed this way raise SettingChanged like any other change.
// Shared source (Valheim/mods/Shared), compiled into each mod that sets <UseConfigReloader>true.
internal sealed class ConfigFileReloader : IDisposable
{
	private readonly ConfigFile config;

	private readonly FileSystemWatcher watcher;

	private readonly System.Threading.Timer timer;   // in full: the game has its own Timer class

	internal ConfigFileReloader(ConfigFile config)
	{
		this.config = config;
		timer = new System.Threading.Timer(_ => ThreadingHelper.Instance.StartSyncInvoke(Reload));
		watcher = new FileSystemWatcher(Path.GetDirectoryName(config.ConfigFilePath), Path.GetFileName(config.ConfigFilePath))
		{
			NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName
		};
		FileSystemEventHandler changed = (_, __) => timer.Change(500, Timeout.Infinite);
		watcher.Changed += changed;
		watcher.Created += changed;
		watcher.Renamed += (_, __) => timer.Change(500, Timeout.Infinite);
		watcher.EnableRaisingEvents = true;
	}

	private void Reload()
	{
		if (!File.Exists(config.ConfigFilePath))
		{
			return;
		}
		try
		{
			config.Reload();
		}
		catch (Exception e)
		{
			ModLog.Warning($"Could not reload the config file: {e.Message}");
		}
	}

	public void Dispose()
	{
		watcher.Dispose();
		timer.Dispose();
	}
}
