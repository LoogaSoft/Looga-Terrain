Shader "Hidden/Looga/Tests/Generic BRG"
{
    Properties { _BaseColor ("Color", Color) = (0.2, 0.8, 0.3, 1) _BaseMap ("Map", 2D) = "white" {} _Cull ("Cull", Float) = 2 _SrcBlend ("Source Blend", Float) = 1 _DstBlend ("Destination Blend", Float) = 0 _ZWrite ("Write Depth", Float) = 1 _Surface ("Surface", Float) = 0 _Cutoff ("Cutoff", Float) = 0.5 }
    SubShader
    {
        Tags { "RenderPipeline"="UniversalPipeline" "RenderType"="Opaque" }
        UsePass "Universal Render Pipeline/Unlit/Unlit"
        UsePass "Universal Render Pipeline/Unlit/DepthOnly"
        UsePass "Universal Render Pipeline/Unlit/DepthNormalsOnly"
        UsePass "Universal Render Pipeline/Unlit/MotionVectors"
    }
}
