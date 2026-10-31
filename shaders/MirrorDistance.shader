// The mirror's distance pass (Mirror.cs renders the mirror's scene with it as a replacement shader).
// Writes each reflected point's view depth (from the main camera, through the mirror; and in g a glued ship's pinned water level,
// _RRMirrorShipLift, 0 elsewhere) so the water shader can fog the
// reflection: the mirror's own scene pass can't, because its oblique projection breaks the materials' fog. Everything under the
// sea is cut here instead of by an oblique near plane (_RRMirrorLevel). Culled as each material says (_Cull, as every game shader:
// Custom/Vegetation 0, two-sided; Custom/Piece and Custom/Creature 2, back; read from the bundle 2026-10-28): two-sided for all,
// the inside of a hull cut at the sea (which the colour pass culls, showing the sky) was a solid boat here, and the water shader
// fogged and wave-shifted that sky as a boat: the invisible copies (user 2026-10-28). Cutout as Custom/Vegetation
// (_MainTex.a * _Color.a - _Cutoff, decompiled 2026-10-28), at the texture's own mip (without it leaf cards are solid quads:
// the sky between leaves got the tree's fog, 2026-10-28).
Shader "ReallyReflections/MirrorDistance"
{
	Properties
	{
		_MainTex ("", 2D) = "white" {}
		_Cutoff ("", Float) = 0
		_Cull ("", Float) = 2
	}
	// Only materials that say they are solid or cut out (Unity's RenderType tag): invisible helper meshes (a ship's water mask)
	// and transparent effects would otherwise fog the reflection as if something solid stood there.
	SubShader
	{
		Tags { "RenderType" = "Opaque" }
		Pass
		{
			Cull [_Cull]
			CGPROGRAM
			#pragma vertex vert
			#pragma fragment frag
			#include "UnityCG.cginc"

			sampler2D _MainTex;
			float4 _MainTex_ST;
			float _Cutoff;
			float _RRMirrorLevel;
			float _RRMirrorShipLift;

			struct v2f
			{
				float4 pos : SV_POSITION;
				float2 uv : TEXCOORD0;
				float worldY : TEXCOORD1;
				float depth : TEXCOORD2;
			};

			v2f vert(appdata_base v)
			{
				v2f o;
				o.pos = UnityObjectToClipPos(v.vertex);
				o.uv = TRANSFORM_TEX(v.texcoord, _MainTex);
				o.worldY = mul(unity_ObjectToWorld, v.vertex).y;
				o.depth = -UnityObjectToViewPos(v.vertex).z;
				return o;
			}

			float4 frag(v2f i) : SV_Target
			{
				clip(i.worldY - _RRMirrorLevel);
				if (_Cutoff > 0.0)
				{
					clip(tex2D(_MainTex, i.uv).a - _Cutoff);
				}
				return float4(i.depth, _RRMirrorShipLift, 0.0, 0.0);
			}
			ENDCG
		}
	}
	SubShader
	{
		Tags { "RenderType" = "TransparentCutout" }
		Pass
		{
			Cull [_Cull]
			CGPROGRAM
			#pragma vertex vert
			#pragma fragment frag
			#include "UnityCG.cginc"

			sampler2D _MainTex;
			float4 _MainTex_ST;
			float _Cutoff;
			float _RRMirrorLevel;
			float _RRMirrorShipLift;

			struct v2f
			{
				float4 pos : SV_POSITION;
				float2 uv : TEXCOORD0;
				float worldY : TEXCOORD1;
				float depth : TEXCOORD2;
			};

			v2f vert(appdata_base v)
			{
				v2f o;
				o.pos = UnityObjectToClipPos(v.vertex);
				o.uv = TRANSFORM_TEX(v.texcoord, _MainTex);
				o.worldY = mul(unity_ObjectToWorld, v.vertex).y;
				o.depth = -UnityObjectToViewPos(v.vertex).z;
				return o;
			}

			float4 frag(v2f i) : SV_Target
			{
				clip(i.worldY - _RRMirrorLevel);
				if (_Cutoff > 0.0)
				{
					clip(tex2D(_MainTex, i.uv).a - _Cutoff);
				}
				return float4(i.depth, _RRMirrorShipLift, 0.0, 0.0);
			}
			ENDCG
		}
	}
}
