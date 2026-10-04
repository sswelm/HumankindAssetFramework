// A selected source plate may face inward before fusion repairs its winding.
// Draw both sides of the highlight, retaining depth testing against the rest of the model.
// SketchUp can export front/back materials as coincident, opposite-facing nodes. A small depth bias keeps
// the unselected twin from overwriting this highlight without making it visible through nearer geometry.
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
            Offset -1, -1
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
