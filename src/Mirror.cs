using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;
using ReallyReflections.Shared;

namespace ReallyReflections;

public enum MirrorResolution
{
	Quarter,
	Half,
	Full,
}

public enum MirrorDetail
{
	TerrainAndBuildings,
	Everything,
}

// The sea's mirror: a planar reflection. One disabled camera, placed as the main camera mirrored in the sea plane and rendered by
// hand once per main-camera frame into a half-float texture (_RRMirrorTex) that the own water shader samples. Client only,
// nothing networked. Runs only while "Enabled" is on (and our shader is loaded): the camera and the hooks exist only then.
//
// Rendered from Camera.onPreRender of the main camera: VUU (DLSS) puts the jittered projection on the camera in its OnPreCull,
// so by onPreRender it is in projectionMatrix, and the mirror gets the same jitter as the frame DLSS resolves.
internal static class Mirror
{
	// Settings (set by the plugin, read each frame).
	internal static MirrorResolution Resolution = MirrorResolution.Half;

	internal static float Distance = 1000f;

	internal static MirrorDetail Detail = MirrorDetail.Everything;

	internal static float Distort = 0.3f;

	internal static float SkySoftness = 0.1f;

	// Reflection blur, in mip levels at the screen's resolution; the mirror's own resolution already takes log2(1 / scale) of it.
	internal static float Blur = 1f;

	// Storm blur, the same units, scaled in the shader by the wind's strength.
	internal static float StormBlur = 2f;

	// Shadows in the reflection (the game's shadow distance); off: about 0.05-0.25 ms cheaper.
	internal static bool Shadows = true;

	// For the harness: mirror renders so far, the camera and its texture (null while off).
	internal static int Renders;

	internal static Camera MirrorCamera => camera;

	internal static RenderTexture Texture => texture;

	internal static RenderTexture DepthTexture => depthBuffer;

	internal static RenderTexture DistanceTexture => distance;

	internal static RenderTexture ShipTexture => shipTexture;

	// The distance pass's replacement shader (from the bundle; null: the reflection gets no fog of its own).
	internal const string DistanceShaderName = "ReallyReflections/MirrorDistance";

	internal static Shader DistanceShader;

	// The glued ships' coverage (alpha 1 where a ship shows) in their colour picture, for the water's blend (from the bundle; null:
	// no ship pictures, the ships stay in the shared one).
	internal const string CoverShaderName = "ReallyReflections/MirrorCover";

	internal static Shader CoverShader;

	// The weather particles' coverage, for laying them over the fogged reflection (from the bundle; null: they are drawn into the
	// mirror's picture and fogged with it, as before 2026-10-29).
	internal const string WeatherCoverShaderName = "ReallyReflections/MirrorWeatherCover";

	internal static Shader WeatherCoverShader;

	// For the harness's diagnostics only: a culling mask for the scene pass instead of the setting's (-1: none).
	internal static int DebugMask = -1;

	// For the harness's cost tool only: while Profile is on, each pass's main-thread time is added to PassTicks (Stopwatch
	// ticks): 0 sky, 1 scene, 2 distance, 3 characters (no pass of its own any more: stays 0), 4 weather, 5 mips, 6 ships.
	internal static bool Profile = false;   // set by the harness only

	internal static readonly long[] PassTicks = new long[7];

	private static long passStart;

	private static void PassBegin()
	{
		if (Profile)
		{
			passStart = System.Diagnostics.Stopwatch.GetTimestamp();
		}
	}

	private static void PassEnd(int pass)
	{
		if (Profile)
		{
			PassTicks[pass] += System.Diagnostics.Stopwatch.GetTimestamp() - passStart;
		}
	}

	private static readonly int TexId = Shader.PropertyToID("_RRMirrorTex");

	private static readonly int OnId = Shader.PropertyToID("_RRMirrorOn");

	private static readonly int LightingId = Shader.PropertyToID("_RRWaterLighting");

	private static readonly int DistortId = Shader.PropertyToID("_RRMirrorDistort");

	private static readonly int SizeId = Shader.PropertyToID("_RRMirrorSize");

	private static readonly int DistId = Shader.PropertyToID("_RRMirrorDist");

	private static readonly int LevelId = Shader.PropertyToID("_RRMirrorLevel");

	private static readonly int DepthTexId = Shader.PropertyToID("_RRMirrorDepth");

	private static readonly int SkySoftId = Shader.PropertyToID("_RRMirrorSkySoft");

	private static readonly int CameraDepthId = Shader.PropertyToID("_CameraDepthTexture");

	private static readonly int FlipVId = Shader.PropertyToID("_RRMirrorFlipV");

	private static readonly int TwoSidedNormalsId = Shader.PropertyToID("_TwoSidedNormals");

	private static readonly int ShipLiftId = Shader.PropertyToID("_RRMirrorShipLift");

	private static readonly int ShipTexId = Shader.PropertyToID("_RRMirrorShipTex"), ShipDistId = Shader.PropertyToID("_RRMirrorShipDist"),
		ShipsId = Shader.PropertyToID("_RRMirrorShips"), ShipCountId = Shader.PropertyToID("_RRMirrorShipCount");

	private static readonly int WeatherTexId = Shader.PropertyToID("_RRMirrorWeatherTex"), WeatherCoverId = Shader.PropertyToID("_RRMirrorWeatherCover"),
		WeatherOnId = Shader.PropertyToID("_RRMirrorWeatherOn");

	// The game's "skybox" layer (the Yggdrasil branch, kilometres away): drawn in the sky pass, with no Mirror distance limit
	// (the limit had culled it since the first fixes, 2026-10-27; user 2026-10-28: "the branches were gone").
	private static int skyboxMask = -1;

	private static int SkyboxMask => skyboxMask >= 0 ? skyboxMask : skyboxMask = Layers("skybox");

	private static readonly int SeaLevelId = Shader.PropertyToID("_RRSeaLevel");

	private static readonly int BlurId = Shader.PropertyToID("_RRMirrorBlur");

	private static readonly int StormBlurId = Shader.PropertyToID("_RRMirrorStormBlur");

	// The mirror plane sits this far (m) under the sea level, so the shoreline doesn't open a gap between the water and the
	// reflected land.
	private const float PlaneOffset = 0.05f;

	// The mirror's level-of-detail bias against the game's: coarser models sooner (cheaper).
	private const float LodFactor = 0.5f;

	// Deeper than this under the sea the camera is under water: no mirror, the probe as now.
	private const float UnderWaterDepth = 2f;

	// Near the surface (swimming, looking along the water) the mirror plane stays this far (m) under the
	// camera; the shader's per-pixel wave-height shift puts the reflection back where it belongs. The mirror used to switch off
	// below sea level + 0.5 m, so it dropped out and came back as the camera bobbed on the waves (user, 2026-10-27).

	private static bool enabled;

	private const float CameraClearance = 0.1f;

	private static bool rendering;

	private static Camera main;

	private static Camera camera;

	private static RenderTexture texture;

	private static RenderTexture distance;   // the reflected points' view depth (0: sky), for the reflection's fog

	// The glued ships' own pictures (colour, and distance with 100 + the ship's shift in g), apart from the rest of the mirror: the
	// water shader moves each ship's picture as one piece and lays it over the rest (2026-10-29: in the shared picture the
	// per-pixel wave shift tore a ship apart, a copy left at rest and holes where it moved away).
	private static RenderTexture shipTexture, shipDistance;

	// The weather pass's particles apart (premultiplied colour, and coverage in r), for the water shader to lay over the reflection
	// after its fog, as the game's view draws particles after its fog.
	private static RenderTexture weatherTexture, weatherCover;

	// Per glued ship for the water shader: x its shift, y its distance from the camera (the first guess of where it lands).
	private static readonly Vector4[] shipData = new Vector4[16];

	// The sky and scene passes' depth buffer, sampled by the water shader: only the scene pass's objects leave depth in it,
	// so it tells sky pixels from objects (for Sky softness).
	private static RenderTexture depthBuffer;

	private static int cullingMask;

	private static int cullingMaskFor = -1;

	private static int skyLayer = -2;   // -2: not looked up yet

	// The weather's particle systems (EnvSetup.m_psystems: mist, rain, snow), looked up again when the weather changes.
	private static GameObject[] weatherFor;

	private static readonly List<ParticleSystemRenderer> weather = new List<ParticleSystemRenderer>();

	private static ParticleMist mistFor;

	private static ParticleSystemRenderer mistRenderer;

	private static bool mistHidden;

	private static readonly List<Renderer> effectParts = new List<Renderer>();

	// effectParts from here on are additive (the beams): they add light and cover nothing, so the coverage picture leaves them out.
	private static int effectAdditiveFrom;

	private static readonly List<bool> effectHidden = new List<bool>();

	private static readonly AccessTools.FieldRef<SmokeRenderer, Dictionary<Vector3Int, ParticleSystem>> SmokeSystems =
		AccessTools.FieldRefAccess<SmokeRenderer, Dictionary<Vector3Int, ParticleSystem>>("m_chunkedParticleSystems");

	private static readonly List<int> weatherLayers = new List<int>();

	private static readonly List<Renderer> skyObjects = new List<Renderer>(), found = new List<Renderer>(), hiddenFish = new List<Renderer>();

	private static readonly List<int> skyLayers = new List<int>();

	private static float skyObjectsTime;

	// The game's vegetation materials (Custom/Vegetation) with _TwoSidedNormals off, from the prefabs, looked up again per ZNetScene.
	private static readonly List<Material> oneSidedLeaves = new List<Material>();

	private static ZNetScene leavesFor;

	private static bool leavesOn;

	// Ships riding the waves: each one (up to 16, nearest first) with a water level gets passes of its own, drawn as if it
	// floated at rest (the view moved by the difference), so the mirror cuts it at its own waterline and the water shader's wave
	// shift puts its reflection under it. Per ship: its shift and its renderers' range in shipParts; the local player's renderers
	// when aboard (shipPlayer, -1: not aboard a glued ship).
	private static int shipLayer = -2;

	private static readonly List<float> shipShifts = new List<float>();

	private static readonly List<float> shipDistances = new List<float>();

	private static readonly List<Ship> shipsNear = new List<Ship>();

	private static Vector3 sortEye;

	private static readonly Comparison<Ship> NearestFirst = (a, b) =>
		(a.transform.position - sortEye).sqrMagnitude.CompareTo((b.transform.position - sortEye).sqrMagnitude);

	private static readonly List<int> shipStarts = new List<int>();

	private static readonly List<Renderer> shipParts = new List<Renderer>(), shipFound = new List<Renderer>(), shipPlayerParts = new List<Renderer>();

	private static readonly List<int> shipPartLayers = new List<int>(), shipPlayerLayers = new List<int>();

	private static int shipPlayer = -1;

	// The ships' water masks (Custom/WaterMask: an invisible sheet across the hull that keeps the sea out of the boat), off for the
	// mirror's renders: seen from below, inside the hull cut at the waterline, it hid the hull's inside and left the sky in the
	// hull's shape, a pale upside-down boat (user 2026-10-28, most visible on a VikingShip). The mirror draws no water.
	private static readonly List<Renderer> hiddenMasks = new List<Renderer>();

	private static Shader waterMaskShader;

	// Characters (the player's and creatures' shaders show nothing under the oblique projection, like the clouds) get a pass of
	// their own with the plain projection. The local player's renderers that sit on other layers (clothes and gear on Default)
	// go onto a second spare layer for the frame; looked up again every 30 frames (gear changes).
	private static int charLayer = -2;

	private static int charLayersMask;

	private static Player playerFor;

	private static int playerFrame;

	private static readonly List<Renderer> playerParts = new List<Renderer>();

	private static readonly List<Renderer> playerMoved = new List<Renderer>();

	private static readonly List<int> playerLayers = new List<int>();

	// Mirror distance per layer (Camera.layerCullDistances); the sky layer keeps the far plane.
	private static readonly float[] cullDistances = new float[32];

	private static float cullDistancesFor = -1f;

	internal static void SetEnabled(bool on)
	{
		if (on == enabled)
		{
			return;
		}
		enabled = on;
		if (on)
		{
			Camera.onPreRender += OnPreRender;
			Camera.onPostRender += OnPostRender;
			return;
		}
		Camera.onPreRender -= OnPreRender;
		Camera.onPostRender -= OnPostRender;
		Shader.SetGlobalFloat(OnId, 0f);
		Shader.SetGlobalTexture(TexId, null);
		if (camera)
		{
			UnityEngine.Object.Destroy(camera.gameObject);
		}
		camera = null;
		main = null;
		ReleaseTexture();
		cullDistancesFor = -1f;
	}

	// Changing the detail setting rebuilds the culling mask on the next frame.
	internal static void SettingsChanged()
	{
		cullingMaskFor = -1;
	}

	private static void OnPreRender(Camera cam)
	{
		if (rendering)
		{
			return;
		}
		if (cam != main)
		{
			if (main || !GameCamera.instance || cam.gameObject != GameCamera.instance.gameObject)
			{
				return;
			}
			main = cam;
		}
		// The water's lighting as the game's shader draws it now (its Tessellation setting can change at any time): 1 Standard
		// (the tessellated subshader), 0 Lightweight; the cameras drawn after this one (the reflection probes) use it too.
		Shader.SetGlobalFloat(LightingId, Plugin.GameTessellates() ? 1f : 0f);
		Shader.SetGlobalFloat(OnId, 0f);
		ZoneSystem zones = ZoneSystem.instance;
		float camY = cam.transform.position.y;
		if (!zones || camY < zones.m_waterLevel - UnderWaterDepth)
		{
			return;
		}
		rendering = true;
		try
		{
			Render(cam, Mathf.Min(zones.m_waterLevel, camY - CameraClearance));
			Shader.SetGlobalFloat(OnId, 1f);
		}
		catch (Exception e)
		{
			ModLog.Warning($"Mirror reflections: the mirror render failed ({e.GetType().Name}: {e.Message}); mirror switched off for this session.");
			SetEnabled(false);
		}
		finally
		{
			rendering = false;
		}
	}

	private static void OnPostRender(Camera cam)
	{
		if (cam == main)
		{
			Shader.SetGlobalFloat(OnId, 0f);   // the probes and any other camera that draws the water use the probe
		}
	}

	private static void Render(Camera cam, float level)
	{
		if (!camera)
		{
			CreateCamera();
		}
		EnsureTexture(cam);
		if (cullingMaskFor != (int)Detail)
		{
			cullingMaskFor = (int)Detail;
			cullingMask = CullingMask(Detail);
		}

		// Placed as the main camera mirrored in the plane y = level (for culling and LODs; the matrices below are what is drawn).
		Transform from = cam.transform;
		Vector3 pos = from.position;
		Vector3 fwd = from.forward;
		Vector3 up = from.up;
		Transform to = camera.transform;
		to.SetPositionAndRotation(new Vector3(pos.x, 2f * level - pos.y, pos.z),
			Quaternion.LookRotation(new Vector3(fwd.x, -fwd.y, fwd.z), new Vector3(up.x, -up.y, up.z)));

		Matrix4x4 reflect = Matrix4x4.identity;
		reflect.m11 = -1f;
		reflect.m13 = 2f * level;
		// The mirrored view turned over (camera-space y flipped), so triangles keep their winding and two-sided shaders
		// (leaves, clouds) light the side they really show; the picture is upside down and the water shader reads it flipped.
		// (The reflection alone, culling flipped, had gl_FrontFacing wrong: leaves lit on their sunny side when the main view
		// sees their shaded one, young pines near-white in the reflection at sunrise.)
		Matrix4x4 turn = Matrix4x4.identity;
		turn.m11 = -1f;
		Matrix4x4 view = turn * cam.worldToCameraMatrix * reflect;
		camera.worldToCameraMatrix = view;
		Shader.SetGlobalFloat(FlipVId, 1f);

		// Three renders. The sky and the clouds first, with the main camera's own projection (DLSS jitter in m02/m12): the game's
		// cloud shader draws nothing under an oblique projection (tested 2026-10-27; why is unknown), and the clouds never reach
		// below the sea, so they need no clipping. Then everything else over it, with the near plane made oblique on the sea, so
		// nothing under the water is drawn. Then the distance pass for the reflection's fog.
		Matrix4x4 proj = cam.projectionMatrix;
		proj.m12 = -proj.m12;   // the vertical jitter, turned over with the picture
		// Mirror distance is a per-object cull distance, not the projection's far plane: the oblique matrix rebuilds the depth row,
		// so a far plane set in the projection ends up tilted and cuts the scene in odd places (user, 2026-10-27: near objects
		// vanished at 100 m). Every pass keeps the main camera's far plane.
		float near = cam.nearClipPlane, far = cam.farClipPlane;
		Matrix4x4 sceneProj = proj;
		camera.projectionMatrix = sceneProj;
		Matrix4x4 distanceProj = sceneProj;
		if (cullDistancesFor != Distance)
		{
			cullDistancesFor = Distance;
			for (int i = 0; i < cullDistances.Length; i++)
			{
				cullDistances[i] = i == SkyLayer || (SkyboxMask & (1 << i)) != 0 ? 0f : Distance;   // 0 = the far plane (the sky pass)
			}
			camera.layerCullDistances = cullDistances;
		}
		Vector3 planePos = view.MultiplyPoint(new Vector3(0f, level - PlaneOffset, 0f));
		Vector3 planeNormal = view.MultiplyVector(Vector3.up).normalized;
		sceneProj = camera.CalculateObliqueMatrix(new Vector4(planeNormal.x, planeNormal.y, planeNormal.z, -Vector3.Dot(planePos, planeNormal)));
		Shader.SetGlobalFloat(DistortId, Distort);
		Shader.SetGlobalFloat(SkySoftId, SkySoftness);
		Shader.SetGlobalFloat(SeaLevelId, level);
		float scale = Resolution == MirrorResolution.Full ? 1f : Resolution == MirrorResolution.Half ? 0.5f : 0.25f;
		Shader.SetGlobalFloat(BlurId, Mathf.Max(Blur + Mathf.Log(scale, 2f), 0f));
		Shader.SetGlobalFloat(StormBlurId, StormBlur);

		EnvMan env = EnvMan.instance;
		MeshRenderer clouds = env ? env.m_clouds : null, rain = env ? env.m_rainClouds : null, rainBelow = env ? env.m_rainCloudsDownside : null;
		int cloudsLayer = clouds ? clouds.gameObject.layer : 0, rainLayer = rain ? rain.gameObject.layer : 0, rainBelowLayer = rainBelow ? rainBelow.gameObject.layer : 0;
		bool fog = RenderSettings.fog;
		float shadows = QualitySettings.shadowDistance;
		float lod = QualitySettings.lodBias;
		bool invert = GL.invertCulling;
		try
		{
			if (!Shadows)
			{
				QualitySettings.shadowDistance = 0f;
			}
			QualitySettings.lodBias = lod * LodFactor;
			GL.invertCulling = false;   // the picture is turned over instead of the winding flipped
			int mask = DebugMask >= 0 ? DebugMask : cullingMask;
			HideFish();

			// Sky pass: the skybox, the game's "skybox" layer and the game's cloud planes and other sky objects that follow the
			// camera, moved for this render onto a layer nothing else uses.
			camera.targetTexture = null;
			camera.SetTargetBuffers(texture.colorBuffer, depthBuffer.depthBuffer);
			camera.projectionMatrix = proj;
			camera.farClipPlane = far;
			camera.nearClipPlane = near;
			camera.clearFlags = CameraClearFlags.Skybox;
			camera.backgroundColor = Color.black;
			if (SkyLayer >= 0)
			{
				SetLayer(clouds, SkyLayer);
				SetLayer(rain, SkyLayer);
				SetLayer(rainBelow, SkyLayer);
				MoveSkyObjects(SkyLayer, clouds, rain, rainBelow);
				camera.cullingMask = 1 << SkyLayer | (mask & SkyboxMask);
			}
			else
			{
				camera.cullingMask = mask & SkyboxMask;
			}
			// The cloud shader fades a cloud out where the scene's depth (_CameraDepthTexture) is in front of it, and that texture
			// still holds the main view (last frame's: the mirror renders before it), so the reflected clouds were cut wherever the
			// main view saw land or the sea floor. An empty depth (far everywhere) for this pass.
			Texture sceneDepth = Shader.GetGlobalTexture(CameraDepthId);
			Shader.SetGlobalTexture(CameraDepthId, SystemInfo.usesReversedZBuffer ? Texture2D.blackTexture : Texture2D.whiteTexture);
			// The game's sky is faked close to the camera: the cloud planes and the distant cloud and fog planes follow it
			// (FollowPlayer), the cloud planes only about 18 m above it. Reflected in the sea, the mirror camera saw them from
			// twice its height further below: other parts of the planes, far below the horizon, from underneath (2026-10-28).
			// Reflected in the plane through the camera instead, each shows in the water where the sky has it.
			Matrix4x4 reflectAtCamera = Matrix4x4.identity;
			reflectAtCamera.m11 = -1f;
			reflectAtCamera.m13 = 2f * pos.y;
			to.position = pos;
			camera.worldToCameraMatrix = turn * cam.worldToCameraMatrix * reflectAtCamera;
			PassBegin();
			camera.Render();
			PassEnd(0);
			to.position = new Vector3(pos.x, 2f * level - pos.y, pos.z);
			camera.worldToCameraMatrix = view;
			if (sceneDepth != null)
			{
				Shader.SetGlobalTexture(CameraDepthId, sceneDepth);
			}
			// The clouds stay on that layer through the scene and distance passes: drawn there (as Default) they showed nothing
			// under the oblique projection but wrote depth, so the water shader took every cloud for an object and Sky softness
			// never reached them (2026-10-27).

			// Scene pass, over the sky (depth cleared only). Without fog: the oblique projection breaks the materials' fog (it
			// comes from clip-space depth); the water shader fogs the reflection from the distance pass instead.
			bool characters = CharLayer >= 0 && DebugMask < 0 && (mask & charLayersMask) != 0;
			if (characters)
			{
				MovePlayer(CharLayer);
			}
			int charMask = characters ? (charLayersMask & mask) | (1 << CharLayer) : 0;
			if (DebugMask < 0)
			{
				HideShips(cam, level, mask);
			}
			FindWeather(env);
			FindEffects(cam.transform.position);
			HideMist();
			camera.projectionMatrix = sceneProj;
			camera.farClipPlane = far;
			camera.clearFlags = CameraClearFlags.Depth;
			// The characters are in this pass, cut at the sea by its oblique plane, instead of in a pass of their own with the plain
			// projection, uncut (a swimmer's body under the water showed as a huge copy). In 2026-10-27 their shaders showed
			// nothing here; now they do (Tests.Tools.MirrorSwimCheck), the cause then unknown.
			camera.cullingMask = mask & ~SkyboxMask;
			RenderSettings.fog = false;
			SetLeafUndersides(1f);
			PassBegin();
			camera.Render();
			PassEnd(1);
			SetLeafUndersides(0f);
			RenderSettings.fog = fog;

			// Distance pass: the same scene with a plain projection and our replacement shader, which cuts below the sea itself.
			if (DistanceShader)
			{
				Shader.SetGlobalFloat(LevelId, level - PlaneOffset);
				Shader.SetGlobalFloat(ShipLiftId, 0f);
				camera.projectionMatrix = distanceProj;
				camera.targetTexture = distance;
				camera.clearFlags = CameraClearFlags.SolidColor;
				camera.backgroundColor = Color.clear;
				camera.cullingMask = (mask | charMask) & ~SkyboxMask;
				PassBegin();
				camera.RenderWithShader(DistanceShader, "RenderType");
				PassEnd(2);
				camera.targetTexture = null;

				// Ship pictures: each glued ship alone, at rest, into the ships' own colour (the scene pass's oblique cut, its own
				// depth) and distance (the distance pass's projection and cut), then the local player aboard over its colour,
				// depth-tested against the ship's distance (as the character pass).
				bool ships = shipShifts.Count > 0;
				Shader.SetGlobalFloat(ShipCountId, ships ? shipShifts.Count : 0);
				if (ships)
				{
					PassBegin();
					for (int i = 0; i < shipData.Length; i++)
					{
						shipData[i] = i < shipShifts.Count ? new Vector4(shipShifts[i], shipDistances[i], 0f, 0f) : Vector4.zero;
					}
					Shader.SetGlobalVectorArray(ShipsId, shipData);
					Graphics.SetRenderTarget(shipTexture);
					GL.Clear(true, true, Color.clear);
					camera.SetTargetBuffers(shipTexture.colorBuffer, shipTexture.depthBuffer);
					camera.clearFlags = CameraClearFlags.Nothing;
					camera.projectionMatrix = sceneProj;
					RenderSettings.fog = false;
					RenderShips(view, mask, false, 0f);
					RenderSettings.fog = fog;
					camera.projectionMatrix = distanceProj;
					Graphics.SetRenderTarget(shipDistance);
					GL.Clear(true, true, Color.clear);
					camera.targetTexture = shipDistance;
					camera.clearFlags = CameraClearFlags.Nothing;
					RenderShips(view, mask | charMask, true, level - PlaneOffset);
					camera.targetTexture = null;
					if (shipPlayer >= 0 && characters)
					{
						camera.SetTargetBuffers(shipTexture.colorBuffer, shipDistance.depthBuffer);
						RenderShipPlayer(view, charMask);
					}
					shipTexture.GenerateMips();
					PassEnd(6);
				}
				camera.worldToCameraMatrix = view;

				RestorePlayer();

				// Weather pass: the weather's particles (the mist that hides the shore in misty weather) and the effects
				// (FindEffects: smoke, fires, beams) over the mirror, with the plain projection and depth-tested against the distance
				// pass's depth (the scene pass's oblique depth can't be shared, and transparent shaders fail under the oblique
				// projection like the clouds). No fog of their own: the water shader fogs the reflection.
				SetLayer(clouds, cloudsLayer);   // the weather pass uses the same spare layer
				RestoreSkyObjects();
				SetLayer(rain, rainLayer);
				SetLayer(rainBelow, rainBelowLayer);
				ShowMist();
				bool weatherApart = weatherTexture && WeatherCoverShader;
				bool weatherDrawn = weather.Count + effectParts.Count > 0 && SkyLayer >= 0;
				Shader.SetGlobalFloat(WeatherOnId, weatherDrawn && weatherApart ? 1f : 0f);
				if (weatherDrawn)
				{
					MoveWeather(SkyLayer);
					if (weatherApart)
					{
						Graphics.SetRenderTarget(weatherTexture.colorBuffer, distance.depthBuffer);
						GL.Clear(false, true, Color.clear);
						camera.SetTargetBuffers(weatherTexture.colorBuffer, distance.depthBuffer);
					}
					else
					{
						camera.SetTargetBuffers(texture.colorBuffer, distance.depthBuffer);
					}
					camera.clearFlags = CameraClearFlags.Nothing;
					camera.cullingMask = 1 << SkyLayer;
					// Soft particles (the mist's _FADING_ON) fade by the main view's depth texture, which doesn't match the
					// mirror's picture: an empty one here, as for the clouds (the depth test against the distance pass hides
					// them behind the land).
					Texture viewDepth = Shader.GetGlobalTexture(CameraDepthId);
					Shader.SetGlobalTexture(CameraDepthId, SystemInfo.usesReversedZBuffer ? Texture2D.blackTexture : Texture2D.whiteTexture);
					PassBegin();
					camera.Render();
					if (weatherApart)
					{
						Graphics.SetRenderTarget(weatherCover.colorBuffer, distance.depthBuffer);
						GL.Clear(false, true, Color.clear);
						camera.SetTargetBuffers(weatherCover.colorBuffer, distance.depthBuffer);
						for (int i = effectAdditiveFrom; i < effectParts.Count; i++)
						{
							effectParts[i].forceRenderingOff = true;
						}
						camera.RenderWithShader(WeatherCoverShader, "");
						for (int i = effectAdditiveFrom; i < effectParts.Count; i++)
						{
							effectParts[i].forceRenderingOff = false;
						}
						weatherTexture.GenerateMips();
						weatherCover.GenerateMips();
					}
					PassEnd(4);
					if (viewDepth != null)
					{
						Shader.SetGlobalTexture(CameraDepthId, viewDepth);
					}
					RestoreWeather();
				}
			}
			PassBegin();
			texture.GenerateMips();   // the roughness blur reads them
			PassEnd(5);
			Renders++;
		}
		finally
		{
			RestorePlayer();
			RestoreWeather();
			ShowMist();
			RenderSettings.fog = fog;
			camera.targetTexture = null;
			SetLayer(clouds, cloudsLayer);
			SetLayer(rain, rainLayer);
			SetLayer(rainBelow, rainBelowLayer);
			RestoreSkyObjects();
			ShowFish();
			ShowShips();
			camera.worldToCameraMatrix = view;
			SetLeafUndersides(0f);
			GL.invertCulling = invert;
			QualitySettings.lodBias = lod;
			QualitySettings.shadowDistance = shadows;
		}
	}

	// The weather's particle systems and the Mistlands mist (ParticleMist: one particle system on Default that follows the player,
	// not one of the environment's; a transparent shader, so it failed in the scene pass and the reflection showed the land bare
	// where the main view has mist, user 2026-10-29), looked up again when the environment or the mist changes.
	private static void FindWeather(EnvMan env)
	{
		EnvSetup setup = env ? env.GetCurrentEnvironment() : null;
		GameObject[] systems = setup?.m_psystems;
		ParticleMist mist = ParticleMist.instance;
		if (systems == weatherFor && mist == mistFor)
		{
			return;
		}
		weatherFor = systems;
		mistFor = mist;
		weather.Clear();
		mistRenderer = mist ? mist.GetComponent<ParticleSystemRenderer>() : null;
		if (mistRenderer)
		{
			weather.Add(mistRenderer);
		}
		foreach (GameObject system in systems ?? Array.Empty<GameObject>())
		{
			if (system)
			{
				weather.AddRange(system.GetComponentsInChildren<ParticleSystemRenderer>(true));
			}
		}
	}

	// The Mistlands mist out of the scene pass (it is drawn in the weather pass); ShowMist puts it back.
	// Particles for the weather pass besides the weather's, looked up every render (they come and go):
	// - the fires' smoke: the Smoke puffs have no renderer; SmokeRenderer draws them through a particle system per 10 m chunk that
	//   it makes and fills itself, so neither the scene nor the weather pass had them (user 2026-10-29: a Fuling village's fires
	//   reflected, their smoke not);
	// - with Fires over the fog, the burning fires' flames, sparks and glow (Fireplace, within Mirror distance): in the scene pass
	//   they were fogged by what lies behind them, where the game's view draws them after its fog, fogging themselves;
	// - the beams (within Mirror distance), last: additive.
	private static void FindEffects(Vector3 eye)
	{
		effectParts.Clear();
		effectAdditiveFrom = int.MaxValue;
		SmokeRenderer smoke = SmokeRenderer.Instance;
		if (smoke)
		{
			foreach (ParticleSystem ps in SmokeSystems(smoke).Values)
			{
				if (ps && ps.TryGetComponent(out ParticleSystemRenderer r) && r.enabled)
				{
					effectParts.Add(r);
				}
			}
		}
		for (int i = fires.Count - 1; i >= 0; i--)
		{
			Fireplace fire = fires[i].fire;
			if (!fire)
			{
				fires.RemoveAt(i);
				continue;
			}
			if ((fire.transform.position - eye).sqrMagnitude > Distance * Distance)
			{
				continue;
			}
			foreach (ParticleSystemRenderer r in fires[i].parts)
			{
				if (r && r.enabled && r.gameObject.activeInHierarchy)
				{
					effectParts.Add(r);
				}
			}
		}
		effectAdditiveFrom = effectParts.Count;
		for (int i = beams.Count - 1; i >= 0; i--)
		{
			GameObject owner = beams[i].owner;
			if (!owner)
			{
				beams.RemoveAt(i);
				continue;
			}
			if ((owner.transform.position - eye).sqrMagnitude > Distance * Distance)
			{
				continue;
			}
			foreach (MeshRenderer r in beams[i].parts)
			{
				if (r && r.enabled && r.gameObject.activeInHierarchy)
				{
					effectParts.Add(r);
				}
			}
		}
	}

	// Additive beams: the Ashlands fortresses' bell altar beacon (Unlit/BeaconBeam: blend One One, no depth, no fog). The game's view
	// adds them after its fog, so they glow through the ash rain; in the scene pass the reflection's fog by the distance behind them
	// all but hid them (user's showcase 08, 2026-10-30). The prefabs that carry one (by ZDO prefab hash, from ZNetScene.Awake) and
	// each live one's beam renderers (registered as it wakes; destroyed ones drop out in FindEffects).
	private const string BeamShaderName = "Unlit/BeaconBeam";

	private static readonly HashSet<int> beamPrefabs = new HashSet<int>();

	private static readonly List<(GameObject owner, MeshRenderer[] parts)> beams = new List<(GameObject, MeshRenderer[])>();

	private static bool IsBeam(MeshRenderer r) => r.sharedMaterial && r.sharedMaterial.shader.name == BeamShaderName;

	[HarmonyPatch(typeof(ZNetScene), nameof(ZNetScene.Awake))]
	private static class BeamPrefabs
	{
		[HarmonyWrapSafe]
		private static void Postfix(ZNetScene __instance)
		{
			beamPrefabs.Clear();
			beams.Clear();
			var names = new List<string>();
			foreach (GameObject prefab in __instance.m_prefabs)
			{
				if (prefab && prefab.GetComponentsInChildren<MeshRenderer>(true).Any(IsBeam))
				{
					beamPrefabs.Add(prefab.name.GetStableHashCode());
					names.Add(prefab.name);
				}
			}
			ModLog.Info($"Mirror: beams drawn over the reflection's fog, from {string.Join(", ", names)}");
		}
	}

	[HarmonyPatch(typeof(ZNetView), nameof(ZNetView.Awake))]
	private static class BeamAwake
	{
		[HarmonyWrapSafe]
		private static void Postfix(ZNetView __instance)
		{
			ZDO zdo = __instance.GetZDO();   // null when Awake destroyed it
			if (zdo != null && beamPrefabs.Contains(zdo.GetPrefab()))
			{
				beams.Add((__instance.gameObject, __instance.GetComponentsInChildren<MeshRenderer>(true).Where(IsBeam).ToArray()));
			}
		}
	}

	// Every fire (registered as it wakes; destroyed ones drop out in FindEffects) with its particle renderers.
	private static readonly List<(Fireplace fire, ParticleSystemRenderer[] parts)> fires = new List<(Fireplace, ParticleSystemRenderer[])>();

	[HarmonyPatch(typeof(Fireplace), nameof(Fireplace.Awake))]
	private static class FireAwake
	{
		[HarmonyWrapSafe]
		private static void Postfix(Fireplace __instance)
		{
			fires.Add((__instance, __instance.GetComponentsInChildren<ParticleSystemRenderer>(true)));
		}
	}

	// The Mistlands mist and the smoke out of the scene pass (they are drawn in the weather pass); ShowMist puts them back.
	private static void HideMist()
	{
		mistHidden = mistRenderer && mistRenderer.enabled && !mistRenderer.forceRenderingOff;
		if (mistHidden)
		{
			mistRenderer.forceRenderingOff = true;
		}
		effectHidden.Clear();
		foreach (Renderer r in effectParts)
		{
			effectHidden.Add(!r.forceRenderingOff);
			r.forceRenderingOff = true;
		}
	}

	private static void ShowMist()
	{
		if (mistHidden && mistRenderer)
		{
			mistRenderer.forceRenderingOff = false;
		}
		mistHidden = false;
		for (int i = 0; i < effectHidden.Count && i < effectParts.Count; i++)
		{
			if (effectHidden[i] && effectParts[i])
			{
				effectParts[i].forceRenderingOff = false;
			}
		}
		effectHidden.Clear();
	}

	// Moves the active weather particles and the smoke onto a layer for one render; RestoreWeather puts them back.
	private static void MoveWeather(int layer)
	{
		weatherLayers.Clear();
		for (int i = 0; i < weather.Count + effectParts.Count; i++)
		{
			Renderer r = i < weather.Count ? weather[i] : effectParts[i - weather.Count];
			weatherLayers.Add(r ? r.gameObject.layer : 0);
			if (r)
			{
				r.gameObject.layer = layer;
			}
		}
	}

	private static void RestoreWeather()
	{
		for (int i = 0; i < weatherLayers.Count && i < weather.Count + effectParts.Count; i++)
		{
			Renderer r = i < weather.Count ? weather[i] : effectParts[i - weather.Count];
			if (r)
			{
				r.gameObject.layer = weatherLayers[i];
			}
		}
		weatherLayers.Clear();
	}

	private static void SetLayer(MeshRenderer r, int layer)
	{
		if (r && r.gameObject.layer != layer)
		{
			r.gameObject.layer = layer;
		}
	}

	// A second spare layer, for the character pass (the next free one below SkyLayer); -1 if there is none.
	private static int CharLayer
	{
		get
		{
			if (charLayer == -2)
			{
				charLayer = -1;
				for (int i = SkyLayer - 1; i > 0; i--)
				{
					if (string.IsNullOrEmpty(LayerMask.LayerToName(i)))
					{
						charLayer = i;
						break;
					}
				}
				charLayersMask = Layers("character", "character_net", "character_noenv");
			}
			return charLayer;
		}
	}

	// Moves the local player's renderers that aren't on a character layer (clothes, gear) onto the given layer for this frame.
	private static void MovePlayer(int layer)
	{
		Player player = Player.m_localPlayer;
		if (player != playerFor || Time.frameCount - playerFrame >= 30)
		{
			playerFor = player;
			playerFrame = Time.frameCount;
			playerParts.Clear();
			if (player)
			{
				player.GetComponentsInChildren(false, playerParts);
			}
		}
		playerMoved.Clear();
		playerLayers.Clear();
		for (int i = 0; i < playerParts.Count; i++)
		{
			Renderer r = playerParts[i];
			if (r && (charLayersMask & (1 << r.gameObject.layer)) == 0 && !(r is ParticleSystemRenderer))
			{
				playerMoved.Add(r);
				playerLayers.Add(r.gameObject.layer);
				r.gameObject.layer = layer;
			}
		}
	}

	private static void RestorePlayer()
	{
		for (int i = 0; i < playerMoved.Count; i++)
		{
			if (playerMoved[i])
			{
				playerMoved[i].gameObject.layer = playerLayers[i];
			}
		}
		playerMoved.Clear();
		playerLayers.Clear();
	}

	// The renderers that follow the camera (FollowPlayer on the camera, most at a fixed height: the cloud planes, the distant cloud
	// and fog planes, the cloud cylinder), but not the weather's particles (their own pass) or the given cloud planes (moved by the caller); looked
	// up again every 5 s.
	private static void MoveSkyObjects(int layer, params Renderer[] skip)
	{
		if (Time.unscaledTime >= skyObjectsTime)
		{
			skyObjectsTime = Time.unscaledTime + 5f;
			skyObjects.Clear();
			foreach (FollowPlayer f in UnityEngine.Object.FindObjectsByType<FollowPlayer>(FindObjectsSortMode.None))
			{
				if (f.m_follow != FollowPlayer.Type.Camera)
				{
					continue;
				}
				f.GetComponentsInChildren(false, found);
				foreach (Renderer r in found)
				{
					if (System.Array.IndexOf(skip, r) < 0 && !(r is ParticleSystemRenderer p && weather.Contains(p)))
					{
						skyObjects.Add(r);
					}
				}
			}
		}
		skyLayers.Clear();
		foreach (Renderer r in skyObjects)
		{
			skyLayers.Add(r ? r.gameObject.layer : 0);
			if (r)
			{
				r.gameObject.layer = layer;
			}
		}
	}

	private static void RestoreSkyObjects()
	{
		for (int i = 0; i < skyLayers.Count && i < skyObjects.Count; i++)
		{
			if (skyObjects[i])
			{
				skyObjects[i].gameObject.layer = skyLayers[i];
			}
		}
		skyLayers.Clear();
	}

	// Fish swim just under the surface, right above the mirror camera: they showed as huge shapes in the water. Off for the
	// mirror's renders (user, 2026-10-28: filter them out).
	private static void HideFish()
	{
		hiddenFish.Clear();
		foreach (IMonoUpdater u in Fish.Instances)
		{
			if (u is Fish fish && fish)
			{
				fish.GetComponentsInChildren(false, found);
				foreach (Renderer r in found)
				{
					if (!r.forceRenderingOff)
					{
						r.forceRenderingOff = true;
						hiddenFish.Add(r);
					}
				}
			}
		}
	}

	private static void ShowFish()
	{
		foreach (Renderer r in hiddenFish)
		{
			if (r)
			{
				r.forceRenderingOff = false;
			}
		}
		hiddenFish.Clear();
	}

	// A third free layer (below the character layer), for the glued ships' passes; -1 if there is none.
	private static int ShipLayer
	{
		get
		{
			if (shipLayer == -2)
			{
				shipLayer = -1;
				for (int i = CharLayer - 1; i > 0; i--)
				{
					if (string.IsNullOrEmpty(LayerMask.LayerToName(i)))
					{
						shipLayer = i;
						break;
					}
				}
			}
			return shipLayer;
		}
	}

	// Hides the water masks of the ships within Mirror distance; takes the ships nearest first (so the one in view never switches
	// with the game's list order), up to 16, finds those with a water level (the mean of the five points the ship floats on, as
	// Ship.CustomFixedUpdate) and hides their drawn renderers (on the layers in mask) from the shared passes.
	private static void HideShips(Camera cam, float level, int mask)
	{
		bool glue = ShipLayer >= 0 && CoverShader && DistanceShader;
		shipShifts.Clear();
		shipDistances.Clear();
		shipStarts.Clear();
		shipParts.Clear();
		shipPartLayers.Clear();
		shipPlayer = -1;
		Vector3 eye = cam.transform.position;
		Player player = Player.m_localPlayer;
		Ship aboard = player ? player.GetStandingOnShip() ?? player.GetControlledShip() : null;
		shipsNear.Clear();
		foreach (IMonoUpdater u in Ship.Instances)
		{
			if (u is Ship near && near && (near.transform.position - eye).sqrMagnitude <= Distance * Distance)
			{
				shipsNear.Add(near);
			}
		}
		sortEye = eye;
		shipsNear.Sort(NearestFirst);
		foreach (Ship ship in shipsNear)
		{
			Vector3 at = ship.transform.position;
			ship.GetComponentsInChildren(false, shipFound);
			if (!waterMaskShader)
			{
				waterMaskShader = Shader.Find("Custom/WaterMask");
			}
			foreach (Renderer part in shipFound)
			{
				if (!part.forceRenderingOff && waterMaskShader && part.sharedMaterial && part.sharedMaterial.shader == waterMaskShader)
				{
					part.forceRenderingOff = true;
					hiddenMasks.Add(part);
				}
			}
			if (!glue || shipShifts.Count >= shipData.Length || ship.m_floatCollider == null)
			{
				continue;
			}
			Transform box = ship.m_floatCollider.transform;
			Vector3 size = ship.m_floatCollider.size;
			Vector3 c = box.position, f = box.forward * size.z / 2f, r = box.right * size.x / 2f;
			float water = (Floating.GetLiquidLevel(c) + Floating.GetLiquidLevel(c + f) + Floating.GetLiquidLevel(c - f)
				+ Floating.GetLiquidLevel(c + r) + Floating.GetLiquidLevel(c - r)) / 5f;
			if (water < -1000f)
			{
				continue;
			}
			if (ship == aboard)
			{
				shipPlayer = shipShifts.Count;
			}
			shipShifts.Add(level - water);
			shipDistances.Add(Vector3.Distance(eye, at));
			shipStarts.Add(shipParts.Count);
			foreach (Renderer part in shipFound)
			{
				if (part.enabled && !part.forceRenderingOff && !(part is ParticleSystemRenderer) && (mask & (1 << part.gameObject.layer)) != 0)
				{
					part.forceRenderingOff = true;
					shipParts.Add(part);
					shipPartLayers.Add(part.gameObject.layer);
				}
			}
		}
		shipStarts.Add(shipParts.Count);
		shipPlayerParts.Clear();
		shipPlayerLayers.Clear();
		if (shipPlayer >= 0)
		{
			player.GetComponentsInChildren(false, shipFound);
			foreach (Renderer part in shipFound)
			{
				if (part.enabled && !part.forceRenderingOff && !(part is ParticleSystemRenderer))
				{
					part.forceRenderingOff = true;
					shipPlayerParts.Add(part);
				}
			}
		}
	}

	// Each hidden ship alone, on the ship layer, with the view moved by its shift (the world raised by it: the projection's oblique
	// plane, made for the sea at rest, then cuts the ship at its own waterline). distance: the distance pass's replacement shader
	// into the target the caller set, with the local player when aboard; its cut (_RRMirrorLevel, in the world as it is) moved to
	// the ship's waterline, cutLevel - shift. Colour: the front faces.
	private static void RenderShips(Matrix4x4 view, int mask, bool distance, float cutLevel)
	{
		int layer = ShipLayer;
		camera.cullingMask = 1 << layer;
		for (int i = 0; i < shipShifts.Count; i++)
		{
			camera.worldToCameraMatrix = view * Matrix4x4.Translate(new Vector3(0f, shipShifts[i], 0f));
			SetShipParts(i, layer, true);
			bool player = distance && i == shipPlayer;
			if (player)
			{
				SetPlayerParts(layer, true, mask);
			}
			if (distance)
			{
				Shader.SetGlobalFloat(LevelId, cutLevel - shipShifts[i]);
				// The raise that drew this ship at rest (100 + it: a flag beside the depth), for the water shader to undo.
				Shader.SetGlobalFloat(ShipLiftId, 100f + shipShifts[i]);
				camera.RenderWithShader(DistanceShader, "RenderType");
			}
			else
			{
				camera.Render();
				camera.RenderWithShader(CoverShader, "");
			}
			if (player)
			{
				SetPlayerParts(layer, false, mask);
			}
			SetShipParts(i, layer, false);
		}
		camera.worldToCameraMatrix = view;
		if (distance)
		{
			Shader.SetGlobalFloat(LevelId, cutLevel);
			Shader.SetGlobalFloat(ShipLiftId, 0f);
		}
	}

	// The local player aboard a glued ship: the character pass's plain projection with that ship's moved view.
	private static void RenderShipPlayer(Matrix4x4 view, int charMask)
	{
		int layer = ShipLayer;
		camera.cullingMask = 1 << layer;
		camera.worldToCameraMatrix = view * Matrix4x4.Translate(new Vector3(0f, shipShifts[shipPlayer], 0f));
		SetPlayerParts(layer, true, charMask);
		camera.Render();
		camera.RenderWithShader(CoverShader, "");
		SetPlayerParts(layer, false, charMask);
		camera.worldToCameraMatrix = view;
	}

	private static void SetShipParts(int ship, int layer, bool on)
	{
		for (int i = shipStarts[ship]; i < shipStarts[ship + 1]; i++)
		{
			Renderer part = shipParts[i];
			if (part)
			{
				part.gameObject.layer = on ? layer : shipPartLayers[i];
				part.forceRenderingOff = !on;
			}
		}
	}

	// Only the parts the pass would draw (on a layer in mask, at the time: some sit on the character layer for the frame).
	private static void SetPlayerParts(int layer, bool on, int mask)
	{
		if (on)
		{
			shipPlayerLayers.Clear();
		}
		for (int i = 0; i < shipPlayerParts.Count; i++)
		{
			Renderer part = shipPlayerParts[i];
			if (!part)
			{
				if (on)
				{
					shipPlayerLayers.Add(-1);
				}
				continue;
			}
			if (on)
			{
				int was = part.gameObject.layer;
				shipPlayerLayers.Add((mask & (1 << was)) != 0 ? was : -1);
				if ((mask & (1 << was)) != 0)
				{
					part.gameObject.layer = layer;
					part.forceRenderingOff = false;
				}
			}
			else if (shipPlayerLayers[i] >= 0)
			{
				part.gameObject.layer = shipPlayerLayers[i];
				part.forceRenderingOff = true;
			}
		}
	}

	private static void ShowShips()
	{
		for (int i = 0; i < shipParts.Count; i++)
		{
			if (shipParts[i])
			{
				shipParts[i].gameObject.layer = shipPartLayers[i];
				shipParts[i].forceRenderingOff = false;
			}
		}
		foreach (Renderer part in shipPlayerParts)
		{
			if (part)
			{
				part.forceRenderingOff = false;
			}
		}
		foreach (Renderer part in hiddenMasks)
		{
			if (part)
			{
				part.forceRenderingOff = false;
			}
		}
		hiddenMasks.Clear();
		shipParts.Clear();
		shipPartLayers.Clear();
		shipPlayerParts.Clear();
		shipShifts.Clear();
		shipDistances.Clear();
		shipStarts.Clear();
		shipPlayer = -1;
	}

	// Custom/Vegetation flips a leaf's normal on its back side (gl_FrontFacing) only with _TwoSidedNormals on; the firs have it off,
	// so their branches seen from below were lit as if they faced the sun: neon green at midday in the reflection, where the
	// main view mostly sees the tops (user, 2026-10-28). On for the scene pass (1), back to off (0) after it.
	private static void SetLeafUndersides(float on)
	{
		if (on > 0f && leavesFor != ZNetScene.instance)
		{
			leavesFor = ZNetScene.instance;
			oneSidedLeaves.Clear();
			if (leavesFor)
			{
				var seen = new HashSet<Material>();
				foreach (GameObject prefab in leavesFor.m_prefabs.Concat(leavesFor.m_nonNetViewPrefabs))
				{
					if (!prefab)
					{
						continue;
					}
					foreach (Renderer r in prefab.GetComponentsInChildren<Renderer>(true))
					{
						foreach (Material m in r.sharedMaterials)
						{
							if (m && seen.Add(m) && m.shader.name == "Custom/Vegetation" && m.HasProperty(TwoSidedNormalsId)
								&& m.GetFloat(TwoSidedNormalsId) < 0.5f)
							{
								oneSidedLeaves.Add(m);
							}
						}
					}
				}
				ModLog.Info($"Mirror: {oneSidedLeaves.Count} vegetation materials get two-sided normals in the reflection: " +
					string.Join(", ", oneSidedLeaves.Select(m => m.name)));
			}
		}
		if (on == 0f && !leavesOn)
		{
			return;
		}
		leavesOn = on > 0f;
		foreach (Material m in oneSidedLeaves)
		{
			if (m)
			{
				m.SetFloat(TwoSidedNormalsId, on);
			}
		}
	}

	// A layer with no name (none of the game's), for the sky pass; the highest free one, -1 if there is none.
	private static int SkyLayer
	{
		get
		{
			if (skyLayer == -2)
			{
				skyLayer = -1;
				for (int i = 31; i > 0; i--)
				{
					if (string.IsNullOrEmpty(LayerMask.LayerToName(i)))
					{
						skyLayer = i;
						break;
					}
				}
				if (skyLayer < 0)
				{
					ModLog.Warning("Mirror reflections: no free layer for the clouds; the mirror shows the sky without them.");
				}
			}
			return skyLayer;
		}
	}

	private static void CreateCamera()
	{
		var go = new GameObject("RRMirrorCamera") { hideFlags = HideFlags.HideAndDontSave };
		camera = go.AddComponent<Camera>();
		camera.enabled = false;   // rendered by hand
		camera.renderingPath = RenderingPath.Forward;
		camera.allowHDR = true;
		camera.allowMSAA = false;
		camera.allowDynamicResolution = false;
		camera.useOcclusionCulling = false;
		camera.layerCullSpherical = true;   // Mirror distance from the camera, not along its view axis
		camera.depthTextureMode = DepthTextureMode.None;
		camera.clearFlags = CameraClearFlags.Skybox;
		camera.stereoTargetEye = StereoTargetEyeMask.None;
	}

	private static void EnsureTexture(Camera cam)
	{
		float scale = Resolution == MirrorResolution.Full ? 1f : Resolution == MirrorResolution.Half ? 0.5f : 0.25f;
		int w = Mathf.Max(16, Mathf.RoundToInt(cam.pixelWidth * scale));
		int h = Mathf.Max(16, Mathf.RoundToInt(cam.pixelHeight * scale));
		if (texture && texture.width == w && texture.height == h)
		{
			return;
		}
		ReleaseTexture();
		texture = new RenderTexture(w, h, 0, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear)
		{
			name = "RRMirror",
			useMipMap = true,
			autoGenerateMips = false,   // generated after the scene pass
			antiAliasing = 1,
			filterMode = FilterMode.Trilinear,
			wrapMode = TextureWrapMode.Clamp,
		};
		texture.Create();
		depthBuffer = new RenderTexture(w, h, 24, RenderTextureFormat.Depth)
		{
			name = "RRMirrorDepth",
			filterMode = FilterMode.Point,
			wrapMode = TextureWrapMode.Clamp,
		};
		depthBuffer.Create();
		Shader.SetGlobalTexture(TexId, texture);
		Shader.SetGlobalTexture(DepthTexId, depthBuffer);
		if (DistanceShader)
		{
			distance = new RenderTexture(w, h, 24, RenderTextureFormat.RGFloat, RenderTextureReadWrite.Linear)
			{
				name = "RRMirrorDistance",
				filterMode = FilterMode.Point,   // no blending of near and far depths at silhouettes
				wrapMode = TextureWrapMode.Clamp,
			};
			distance.Create();
			shipTexture = new RenderTexture(w, h, 24, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear)
			{
				name = "RRMirrorShips",
				useMipMap = true,
				autoGenerateMips = false,
				filterMode = FilterMode.Trilinear,
				wrapMode = TextureWrapMode.Clamp,
			};
			shipTexture.Create();
			shipDistance = new RenderTexture(w, h, 24, RenderTextureFormat.RGFloat, RenderTextureReadWrite.Linear)
			{
				name = "RRMirrorShipDistance",
				filterMode = FilterMode.Point,
				wrapMode = TextureWrapMode.Clamp,
			};
			shipDistance.Create();
			weatherTexture = new RenderTexture(w, h, 0, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear)
			{
				name = "RRMirrorWeather",
				useMipMap = true,
				autoGenerateMips = false,
				filterMode = FilterMode.Trilinear,
				wrapMode = TextureWrapMode.Clamp,
			};
			weatherTexture.Create();
			weatherCover = new RenderTexture(w, h, 0, RenderTextureFormat.RHalf, RenderTextureReadWrite.Linear)
			{
				name = "RRMirrorWeatherCover",
				useMipMap = true,
				autoGenerateMips = false,
				filterMode = FilterMode.Trilinear,
				wrapMode = TextureWrapMode.Clamp,
			};
			weatherCover.Create();
		}
		Shader.SetGlobalTexture(WeatherTexId, weatherTexture ? weatherTexture : Texture2D.blackTexture);
		Shader.SetGlobalTexture(WeatherCoverId, weatherCover ? weatherCover : Texture2D.blackTexture);
		Shader.SetGlobalFloat(WeatherOnId, 0f);
		Shader.SetGlobalTexture(ShipTexId, shipTexture ? shipTexture : Texture2D.blackTexture);
		Shader.SetGlobalTexture(ShipDistId, shipDistance ? shipDistance : Texture2D.blackTexture);
		Shader.SetGlobalFloat(ShipCountId, 0f);
		Shader.SetGlobalTexture(DistId, distance ? distance : Texture2D.blackTexture);
		Shader.SetGlobalVector(SizeId, new Vector4(w, h, Mathf.Floor(Mathf.Log(Mathf.Max(w, h), 2f)), 0f));   // z: the last mip
	}

	private static void ReleaseTexture()
	{
		if (camera)
		{
			camera.targetTexture = null;
		}
		if (texture)
		{
			texture.Release();
			UnityEngine.Object.Destroy(texture);
		}
		texture = null;
		if (depthBuffer)
		{
			depthBuffer.Release();
			UnityEngine.Object.Destroy(depthBuffer);
		}
		depthBuffer = null;
		if (distance)
		{
			distance.Release();
			UnityEngine.Object.Destroy(distance);
		}
		distance = null;
		foreach (RenderTexture rt in new[] { shipTexture, shipDistance, weatherTexture, weatherCover })
		{
			if (rt)
			{
				rt.Release();
				UnityEngine.Object.Destroy(rt);
			}
		}
		shipTexture = null;
		shipDistance = null;
		weatherTexture = null;
		weatherCover = null;
	}

	// Never the water itself, UI, effects, smoke, grass or the trigger/hitbox/blocker layers.
	private static int CullingMask(MirrorDetail detail)
	{
		int mask = Layers("Default", "piece", "terrain", "static_solid", "skybox");
		if (detail == MirrorDetail.Everything)
		{
			mask |= Layers("character", "character_net", "character_noenv", "vehicle", "item", "piece_nonsolid", "Default_small");
		}
		return mask;
	}

	private static int Layers(params string[] names)
	{
		int mask = 0;
		foreach (string name in names)
		{
			int layer = LayerMask.NameToLayer(name);
			if (layer >= 0)
			{
				mask |= 1 << layer;
			}
		}
		return mask;
	}
}
