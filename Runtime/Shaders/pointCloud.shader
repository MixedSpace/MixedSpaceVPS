Shader "MixedSpace/Point Cloud"
{
    Properties
    {
        _PointColor ("Point Color", Color) = (0.57, 0.93, 0.12, 1)
        _PointSize ("Point Size", Float) = 2
        _PerspectiveEnabled ("Perspective Size", Float) = 0
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" }
        Cull Off

        Pass
        {
            CGPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma target 3.0
            #pragma multi_compile_instancing
            #include "UnityCG.cginc"

            float _PointSize;
            float _PerspectiveEnabled;
            fixed4 _PointColor;

            struct Attributes
            {
                float4 position : POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 position : SV_POSITION;
                float pointSize : PSIZE;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings Vert(Attributes input)
            {
                UNITY_SETUP_INSTANCE_ID(input);
                Varyings output;
                UNITY_INITIALIZE_OUTPUT(Varyings, output);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

                output.position = UnityObjectToClipPos(input.position);
                float size = _PointSize;
                if (_PerspectiveEnabled >= 0.5)
                    size *= _ScreenParams.y / max(output.position.w, 0.0001);
                output.pointSize = max(1.0, size);
                return output;
            }

            fixed4 Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                return _PointColor;
            }
            ENDCG
        }
    }
}
