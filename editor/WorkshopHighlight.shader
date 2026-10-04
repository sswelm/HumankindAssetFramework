// A selected source plate may face inward before fusion repairs its winding.
// Draw both sides of the highlight, retaining depth testing against the rest of the model.
Shader "Hidden/HAF/WorkshopHighlight"
{
    Properties { _Color ("Highlight colour", Color) = (1, 0.85, 0.1, 1) }
    SubShader
    {
        Tags { "RenderType" = "Opaque" }
        Pass
        {
            Cull Off
            ZWrite On
            ZTest LEqual
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            fixed4 _Color;
            float4 vert(float4 vertex : POSITION) : SV_POSITION { return UnityObjectToClipPos(vertex); }
            fixed4 frag() : SV_Target { return _Color; }
            ENDCG
        }
    }
}
