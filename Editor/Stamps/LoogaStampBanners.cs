using LoogaSoft.Inspector.Editor;
using UnityEditor;

namespace LoogaSoft.Terrain.Stamps.Editor
{
    /// <summary>Registers the LoogaSoft inspector banner for the stamp world and the stamp components.</summary>
    [InitializeOnLoad]
    internal static class LoogaStampBanners
    {
        static LoogaStampBanners()
        {
            LoogaBanner.Register(typeof(LoogaStampWorld));
            LoogaBanner.Register(typeof(LoogaHeightStamp));
            LoogaBanner.Register(typeof(LoogaStampTextureSource));
            LoogaBanner.Register(typeof(LoogaHeightSnapshot));
            LoogaBanner.Register(typeof(LoogaSnapshotLimit));
            LoogaBanner.Register(typeof(LoogaWaterBed));
            LoogaBanner.Register(typeof(LoogaSmoothStamp));
            LoogaBanner.Register(typeof(LoogaTextureStamp));
            LoogaBanner.Register(typeof(LoogaObjectStamp));
            LoogaBanner.Register(typeof(LoogaDetailStamp));
            LoogaBanner.Register(typeof(LoogaClearStamp));
            LoogaBanner.Register(typeof(LoogaSplinePath));
            LoogaBanner.Register(typeof(LoogaPathTexture));
            LoogaBanner.Register(typeof(LoogaPathClear));
            LoogaBanner.Register(typeof(LoogaSplineArea));
            LoogaBanner.Register(typeof(LoogaWaterArea));
            LoogaBanner.Register(typeof(LoogaMeshWaterSource));
            LoogaBanner.Register(typeof(LoogaPaintedArea));
        }
    }
}
