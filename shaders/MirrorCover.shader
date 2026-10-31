// The glued ships' coverage. Mirror.cs draws each glued ship (and the local player aboard) a second time
// with this as a replacement shader for every object (empty tag), over the ships' own colour picture: alpha 1 wherever the ship
// shows, colour untouched. The water shader blends that picture over the rest of the mirror by its alpha, blurred with its mips;
// the game's shaders leave alpha at random (about one drawn pixel in nine above 0.5, tested 2026-10-29). Faces and cutout as the
// colour pass and the distance pass (the material's _Cull; its cutout); no depth test: front faces are
// what the colour pass shows, whatever their order.
Shader "ReallyReflections/MirrorCover"
{
	Properties
	{
		_MainTex ("", 2D) = "white" {}
		_Cutoff ("", Float) = 0
		_Cull ("", Float) = 2
	}
	SubShader
	{
		Pass
		{
			Cull [_Cull]
			ZTest Always
			ZWrite Off
			ColorMask A
			CGPROGRAM
			#pragma vertex vert
			#pragma fragment frag
			#include "UnityCG.cginc"

			sampler2D _MainTex;
			float4 _MainTex_ST;
			float _Cutoff;

			struct v2f
			{
				float4 pos : SV_POSITION;
				float2 uv : TEXCOORD0;
			};

			v2f vert(appdata_base v)
			{
				v2f o;
				o.pos = UnityObjectToClipPos(v.vertex);
				o.uv = TRANSFORM_TEX(v.texcoord, _MainTex);
				return o;
			}

			float4 frag(v2f i) : SV_Target
			{
				if (_Cutoff > 0.0)
				{
					clip(tex2D(_MainTex, i.uv).a - _Cutoff);
				}
				return float4(0.0, 0.0, 0.0, 1.0);
			}
			ENDCG
		}
	}
}
