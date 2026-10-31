using BepInEx.Configuration;
using BepInEx.Logging;

namespace ReallyReflections.Shared
{
	// A mod's log. Warnings and errors always reach BepInEx/LogOutput.log; info lines only with the mod's
	// "Debug logging" setting on (off for players), so a normal game's log only shows what needs attention.
	// Report is for output a player asked for with a dump setting of its own.
	// Shared source (Valheim/mods/Shared), compiled into every mod.
	internal static class ModLog
	{
		private static ManualLogSource log;

		private static ConfigEntry<bool> debug;

		// First thing in Awake, so later setup can log. The setting is a plain Bind: personal, never synced by Jotunn.
		internal static void Init(ManualLogSource source, ConfigFile config)
		{
			log = source;
			debug = config.Bind("Debug", "Debug logging", false, "Write info lines (the startup summary, what the mod found and did) to BepInEx/LogOutput.log. Warnings and errors are always written. Personal setting (not forced by the server).");
		}

		internal static bool DebugOn => debug != null && debug.Value;

		internal static void Info(string message)
		{
			if (DebugOn)
			{
				log?.LogInfo(message);
			}
		}

		internal static void Report(string message) => log?.LogInfo(message);

		internal static void Warning(string message) => log?.LogWarning(message);

		internal static void Error(string message) => log?.LogError(message);
	}
}
