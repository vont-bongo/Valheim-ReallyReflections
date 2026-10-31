// How much the weather's particles cover. Mirror.cs draws the weather pass's particles (the weather's mist,
// rain, snow, and the Mistlands mist) into a picture of their own, then a second time with this as a replacement shader for every
// particle (empty tag) into a coverage picture: r = 1 - the product of (1 - each particle's alpha), its texture's alpha times its
// vertex colour's, depth-tested against the distance pass like the particles. The water shader fogs the reflection first and lays
// the particles over it by this, as the game's view does (its fog on the scene, then the particles): fogged by the distance behind
// them, the trees showed through the Mistlands mist (user 2026-10-29).
Shader "ReallyReflections/MirrorWeatherCover"
{
	Properties
	{
		_MainTex ("", 2D) = "white" {}
	}
	SubShader
	{
		Pass
		{
			Cull Off
			ZWrite Off
			ZTest LEqual
			Blend One OneMinusSrcColor
			CGPROGRAM
			#pragma vertex vert
			#pragma fragment frag
			#include "UnityCG.cginc"

			sampler2D _MainTex;
			float4 _MainTex_ST;

			struct appdata
			{
				float4 vertex : POSITION;
				float4 color : COLOR;
				float2 uv : TEXCOORD0;
			};

			struct v2f
			{
				float4 pos : SV_POSITION;
				float4 color : COLOR;
				float2 uv : TEXCOORD0;
			};

			v2f vert(appdata v)
			{
				v2f o;
				o.pos = UnityObjectToClipPos(v.vertex);
				o.color = v.color;
				o.uv = TRANSFORM_TEX(v.uv, _MainTex);
				return o;
			}

			float4 frag(v2f i) : SV_Target
			{
				return saturate(tex2D(_MainTex, i.uv).a * i.color.a).xxxx;
			}
			ENDCG
		}
	}
}
