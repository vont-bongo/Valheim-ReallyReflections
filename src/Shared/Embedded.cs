using System.IO;
using System.Reflection;

namespace ReallyReflections.Shared
{
	// Files embedded in the mod's DLL (icons, textures). Shared source (Valheim/mods/Shared), compiled into every mod.
	internal static class Embedded
	{
		/// <summary>The whole resource, or null if the DLL doesn't have it. Reads to the end (one Stream.Read call may
		/// return fewer bytes than asked).</summary>
		internal static byte[] Read(Assembly assembly, string name)
		{
			using (Stream stream = assembly.GetManifestResourceStream(name))
			{
				if (stream == null)
				{
					return null;
				}
				using (var memory = new MemoryStream())
				{
					stream.CopyTo(memory);
					return memory.ToArray();
				}
			}
		}
	}
}
