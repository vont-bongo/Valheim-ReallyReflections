using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using ReallyReflections.Shared;

namespace ReallyReflections;

// Client-side only: the reflection is local rendering, nothing here is networked, so other players and servers don't need
// this mod.
//
// The sea reflects what is really across it: a planar mirror (Mirror.cs) drawn into the mod's own copy of the game's water
// shader (Custom/Water, ported; prebuilt in bundle/; ReallyReflections/Water), which replaces the game's on every water tile and on
// the far plane while the mirror is on. Other water mods' seam fixes, colours and reflection-probe refresh work on the same tiles and are not needed for
// this one.
[BepInPlugin(ModGuid, ModName, ModVersion)]
public sealed class Plugin : BaseUnityPlugin
{
	public const string ModGuid = "ReallyReflections";

	public const string ModName = "ReallyReflections";

	public const string ModVersion = ModInfo.Version;


	private static ConfigEntry<bool> mirrorEnabled;

	private static ConfigEntry<MirrorResolution> mirrorResolution;

	private static ConfigEntry<float> mirrorDistance;

	private static ConfigEntry<MirrorDetail> mirrorDetail;

	private static ConfigEntry<float> waveDistortion;

	private static ConfigEntry<float> skySoftness;

	private static ConfigEntry<float> reflectionBlur;

	private static ConfigEntry<float> stormBlur;

	private static ConfigEntry<bool> shadows;

	// Cached config values.
	private static bool mirror;

	private static readonly int MainTex = Shader.PropertyToID("_MainTex");

	// The own water shader (ReallyReflections/Water, a port of the game's Custom/Water) from the embedded bundle.
	private const string GameShaderName = "Custom/Water";

	private const string OwnShaderName = "ReallyReflections/Water";

	private const string FarPlanePath = "_GameMain/_Environment/WaterPlane/watersurface";

	private static Shader gameShader;

	private static Shader ownShader;

	private static bool shaderChecked;   // loaded and compared once per session; a failure is final

	private static bool swapping;

	private static Renderer farPlane;


	private ConfigFileReloader configReloader;

	private void Awake()
	{
		ModLog.Init(Logger, Config);
		Config.SaveOnConfigSet = false;
		mirrorEnabled = Config.Bind("Reflections", "Enabled", true,
			"The sea reflects what is really across it (shore, trees, buildings, boats, sky) instead of the game's blurry " +
			"reflection probe: the scene is drawn a second time, mirrored in the sea, at reduced resolution and detail. " +
			"Costs GPU time. Also draws the sea with this mod's own copy of the water shader. Off = the game's shader and probe.");
		mirrorResolution = Config.Bind("Reflections", "Mirror resolution", MirrorResolution.Half,
			"Size of the mirror picture against the screen, on each side: Quarter, Half or Full. Smaller is cheaper and softer.");
		mirrorDistance = Config.Bind("Reflections", "Mirror distance", 1000f, new ConfigDescription(
			"How far (metres) the mirror draws. Land further away than this is not reflected; the sky is.",
			new AcceptableValueRange<float>(100f, 4000f)));
		mirrorDetail = Config.Bind("Reflections", "Mirror detail", MirrorDetail.Everything,
			"What the mirror draws. TerrainAndBuildings: ground, pieces and sky. Everything: also creatures, players, ships and items.");
		waveDistortion = Config.Bind("Reflections", "Wave distortion strength", 0.3f, new ConfigDescription(
			"How much the waves bend the reflection. 1 = as much as the game's own water bends its reflection; 0 = a flat mirror.",
			new AcceptableValueRange<float>(0f, 2f)));
		skySoftness = Config.Bind("Reflections", "Sky softness", 0.1f, new ConfigDescription(
			"Softens the reflected sky and clouds: their dark undersides and hard edges blend into the sky around them. 0 = as " +
			"sharp as the mirror, 1 = very soft. Land, buildings and ships are not affected.",
			new AcceptableValueRange<float>(0f, 1f)));
		reflectionBlur = Config.Bind("Reflections", "Reflection blur", 1f, new ConfigDescription(
			"Softens the mirror. 0 = sharp, 1 = slightly soft, 2 = soft, 3 = very soft. Looks about the same at every Mirror " +
			"resolution (the lower resolutions are already softer, so they get less of it). Costs nothing measurable.",
			new AcceptableValueRange<float>(0f, 3f)));
		stormBlur = Config.Bind("Reflections", "Storm blur", 2f, new ConfigDescription(
			"Extra softness that grows with the wind: none in calm weather, all of it in a storm, where rough water blurs real " +
			"reflections and the flat mirror can't follow every wave. Same scale as Reflection blur; 0 = off.",
			new AcceptableValueRange<float>(0f, 4f)));
		shadows = Config.Bind("Reflections", "Shadows", true,
			"Shadows in the reflection (the game's shadow distance). Off: the branches under a crown, shaded in the main view, are " +
			"lit in the reflection (light, washed-out trees at midday); about 0.05-0.25 ms cheaper.");
		Config.Save();
		Config.SaveOnConfigSet = true;

		mirror = mirrorEnabled.Value;
		mirrorEnabled.SettingChanged += (_, __) =>
		{
			mirror = mirrorEnabled.Value;
			ApplyAll();
			UpdateMirror();
		};
		Mirror.Resolution = mirrorResolution.Value;
		mirrorResolution.SettingChanged += (_, __) => Mirror.Resolution = mirrorResolution.Value;
		Mirror.Distance = mirrorDistance.Value;
		mirrorDistance.SettingChanged += (_, __) => Mirror.Distance = mirrorDistance.Value;
		Mirror.Detail = mirrorDetail.Value;
		mirrorDetail.SettingChanged += (_, __) =>
		{
			Mirror.Detail = mirrorDetail.Value;
			Mirror.SettingsChanged();
		};
		Mirror.Distort = waveDistortion.Value;
		waveDistortion.SettingChanged += (_, __) => Mirror.Distort = waveDistortion.Value;
		Mirror.SkySoftness = skySoftness.Value;
		skySoftness.SettingChanged += (_, __) => Mirror.SkySoftness = skySoftness.Value;
		Mirror.Blur = reflectionBlur.Value;
		reflectionBlur.SettingChanged += (_, __) => Mirror.Blur = reflectionBlur.Value;
		Mirror.StormBlur = stormBlur.Value;
		stormBlur.SettingChanged += (_, __) => Mirror.StormBlur = stormBlur.Value;
		Mirror.Shadows = shadows.Value;
		shadows.SettingChanged += (_, __) => Mirror.Shadows = shadows.Value;

		new Harmony(ModGuid).PatchAll(typeof(Plugin).Assembly);
		configReloader = new ConfigFileReloader(Config);
		ModLog.Info($"{ModName} {ModVersion} loaded (mirror {(mirror ? "on" : "off")}).");
	}

	private void OnDestroy()
	{
		Mirror.SetEnabled(false);
		configReloader?.Dispose();
	}

	private static void ApplyAll()
	{
		var volumes = WaterVolume.Instances;
		for (int i = 0; i < volumes.Count; i++)
		{
			Apply(volumes[i]);
		}
		ApplyFarPlane();
	}

	// True while the game draws its water with the tessellated subshader (LOD 250): GraphicsSettingsManager.ApplyTesselation sets
	// the shader's maximumLOD to 250 with the Tessellation setting on, 200 off (unset, -1: the global limit, unset too: all).
	internal static bool GameTessellates()
	{
		if (!gameShader)
		{
			return false;
		}
		int lod = gameShader.maximumLOD >= 0 ? gameShader.maximumLOD : Shader.globalMaximumLOD;
		return lod < 0 || lod >= 250;
	}

	// The mirror camera runs while the setting is on and our shader is the one drawing the sea.
	private static void UpdateMirror()
	{
		Mirror.SetEnabled(mirror && ownShader);
	}

	// Loads our shader once and checks it against the game's: the same property names and types, or the game has changed
	// Custom/Water and the port is out of date (then the game's shader stays, with one warning).
	private static Shader OwnShader(Shader game)
	{
		if (shaderChecked)
		{
			return ownShader;
		}
		shaderChecked = true;
		byte[] data = Embedded.Read(typeof(Plugin).Assembly, "ReallyReflections.reallyreflections");
		AssetBundle bundle = data == null ? null : AssetBundle.LoadFromMemory(data);
		if (!bundle)
		{
			ModLog.Warning("Mirror reflections: the shader bundle did not load; the game's water shader stays.");
			return null;
		}
		Shader own = null;
		foreach (Shader shader in bundle.LoadAllAssets<Shader>())
		{
			if (shader.name == OwnShaderName)
			{
				own = shader;
			}
			else if (shader.name == Mirror.DistanceShaderName && shader.isSupported)
			{
				shader.hideFlags |= HideFlags.DontUnloadUnusedAsset;
				Mirror.DistanceShader = shader;
			}
			else if (shader.name == Mirror.CoverShaderName && shader.isSupported)
			{
				shader.hideFlags |= HideFlags.DontUnloadUnusedAsset;
				Mirror.CoverShader = shader;
			}
			else if (shader.name == Mirror.WeatherCoverShaderName && shader.isSupported)
			{
				shader.hideFlags |= HideFlags.DontUnloadUnusedAsset;
				Mirror.WeatherCoverShader = shader;
			}
		}
		bundle.Unload(false);
		if (!own || !own.isSupported)
		{
			ModLog.Warning("Mirror reflections: the water shader is missing or not supported here; the game's water shader stays.");
			return null;
		}
		string difference = PropertyDifference(game, own);
		if (difference != null)
		{
			ModLog.Warning($"Mirror reflections: the game's {GameShaderName} no longer matches the port ({difference}); the game's water shader stays.");
			return null;
		}
		own.hideFlags |= HideFlags.DontUnloadUnusedAsset;
		ownShader = own;
		UpdateMirror();
		return own;
	}

	private static string PropertyDifference(Shader game, Shader own)
	{
		if (game.GetPropertyCount() != own.GetPropertyCount())
		{
			return $"{game.GetPropertyCount()} properties against {own.GetPropertyCount()}";
		}
		for (int i = 0; i < game.GetPropertyCount(); i++)
		{
			string name = game.GetPropertyName(i);
			int j = own.FindPropertyIndex(name);
			if (j < 0)
			{
				return $"no property {name}";
			}
			if (game.GetPropertyType(i) != own.GetPropertyType(j))
			{
				return $"property {name} is {game.GetPropertyType(i)}, ours {own.GetPropertyType(j)}";
			}
		}
		return null;
	}

	// Puts the shader the setting asks for on a material. True if it changed.
	private static bool SwapShader(Material material)
	{
		Shader current = material.shader;
		if (!gameShader && current && current.name == GameShaderName)
		{
			gameShader = current;
		}
		Shader wanted = mirror ? (gameShader ? OwnShader(gameShader) : null) : gameShader;
		if (!wanted)
		{
			wanted = gameShader;   // ours failed to load or doesn't match: the game's
		}
		if (!wanted || wanted == current)
		{
			return false;
		}
		material.shader = wanted;
		return true;
	}

	// The far plane (4 km, the water beyond the tile ring) keeps one shared material, water_lod.
	private static void ApplyFarPlane()
	{
		if (!farPlane)
		{
			GameObject plane = GameObject.Find(FarPlanePath);
			farPlane = plane ? plane.GetComponent<Renderer>() : null;
		}
		if (farPlane && farPlane.sharedMaterial)
		{
			SwapShader(farPlane.sharedMaterial);
		}
	}

	private static void Apply(WaterVolume volume)
	{
		// Fixed-depth water (menu scene, special water): the game's shader stays, as before the move.
		if (!volume || volume.m_forceDepth >= 0f || !volume.m_waterSurface)
		{
			return;
		}
		// Already a per-tile copy: the game's SetupMaterial made it through .material.
		Material material = volume.m_waterSurface.material;
		if (material.HasProperty(MainTex))
		{
			return;   // the game's shader has a real _MainTex now: it changed, and the port is out of date
		}
		if (SwapShader(material))
		{
			// A new shader may drop values the game set from code (_depth, _UseGlobalWind): set them again. Other mods'
			// SetupMaterial postfixes (other mods' colours and seam fixes) run again with it.
			swapping = true;
			try
			{
				volume.SetupMaterial();
			}
			finally
			{
				swapping = false;
			}
		}
		ApplyFarPlane();
	}

	// Runs once per water tile when its zone loads (and again after a terrain refresh).
	[HarmonyPatch(typeof(WaterVolume), nameof(WaterVolume.SetupMaterial))]
	private static class WaterSetup
	{
		[HarmonyWrapSafe]
		private static void Postfix(WaterVolume __instance)
		{
			if (!swapping)
			{
				Apply(__instance);
			}
		}
	}
}
