Shader "MixedSpace/Point Cloud"
{
	Properties
	{

	}

		SubShader
	{
		Cull Off
		Tags{ "RenderType" = "Opaque" }
		LOD 100

		Pass
		{
			CGPROGRAM
			#pragma vertex vert
			#pragma fragment frag
			#pragma target 3.0
			#pragma multi_compile_instancing

			#include "UnityCG.cginc"

			float _PointSize;
			fixed _PerspectiveEnabled;
			fixed4 _PointColor;

			struct Vertex
			{
				float3 vertex : POSITION;
				UNITY_VERTEX_INPUT_INSTANCE_ID
			};

			struct VertexOut
			{
				float psize : PSIZE;
				float4 center : TEXCOORD0;
				half size : TEXCOORD1;
				UNITY_VERTEX_OUTPUT_STEREO
			};

			VertexOut vert(Vertex vertex, out float4 outpos : SV_POSITION)
			{
				// Select the correct XR eye before reading its view/projection matrix.
				UNITY_SETUP_INSTANCE_ID(vertex);
				VertexOut o;
				UNITY_INITIALIZE_OUTPUT(VertexOut, o);
				UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
				outpos = UnityObjectToClipPos(vertex.vertex);

                o.psize = lerp(_PointSize, _PointSize / outpos.w * _ScreenParams.y, step(0.5, _PerspectiveEnabled));
				o.size = o.psize;

				o.center = ComputeScreenPos(outpos);
				return o;
			}
            
			fixed4 frag(VertexOut i, UNITY_VPOS_TYPE vpos : VPOS) : SV_Target
			{
				UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
				fixed4 c = _PointColor;
				float4 center = i.center;
				center.xy /= center.w;
				center.xy *= _ScreenParams.xy;
				float d = distance(vpos.xy, center.xy);

				if (d > i.size * 0.5) {
                    discard;
				}

				return c;
			}
			ENDCG
		}
	}
}
