using UnityEngine;

namespace LoogaSoft.Terrain.Stamps
{
    /// <summary>
    /// Base of the height modifiers that read the heights around each sample at their place in the stamp order, also on
    /// the neighbour tiles. The stamp world splits the stamp order at each one: every tile that the stamp touches gets
    /// an apron with its heights before the stamp and the heights of its neighbours within the reach. A
    /// <see cref="LoogaSnapshotLimit"/> cannot read a snapshot from before a neighbourhood stamp.
    /// </summary>
    /// <remarks>
    /// A project stamp implements <see cref="Apply"/>, usually with its own compute shader that reads the apron and
    /// writes the target. <see cref="LoogaSmoothStamp"/> is the built-in stamp of this kind.
    /// </remarks>
    public abstract class LoogaNeighbourhoodStamp : LoogaHeightModifier
    {
        private Rect? _changedRegion;

        /// <summary>Distance in metres that the stamp reads around each sample.</summary>
        public abstract float Reach { get; }

        /// <summary>False while the stamp has no result, for example before a generator prepares it. The stamp world
        /// then leaves the stamp out.</summary>
        public virtual bool IsReady => true;

        /// <summary>World XZ rectangle where the stamp can change the heights.</summary>
        protected abstract Rect GetArea();

        /// <summary>Changes the heights of one tile that the area touches. It runs on each evaluation of the tile.</summary>
        protected abstract void Apply(NeighbourhoodTile tile);

        internal void ApplyTile(NeighbourhoodTile tile)
        {
            Apply(tile);
        }

        /// <summary>
        /// Marks a change of the result inside a world XZ region only, so the editor updater updates only that region,
        /// not the whole area. Outside the region, the stamp must give the same result as before. Call
        /// <see cref="LoogaHeightModifier.MarkChanged()"/> for a change of the whole area.
        /// </summary>
        protected void MarkChanged(Rect changedRegion)
        {
            _changedRegion = _changedRegion.HasValue
                ? Rect.MinMaxRect(Mathf.Min(_changedRegion.Value.xMin, changedRegion.xMin),
                    Mathf.Min(_changedRegion.Value.yMin, changedRegion.yMin),
                    Mathf.Max(_changedRegion.Value.xMax, changedRegion.xMax),
                    Mathf.Max(_changedRegion.Value.yMax, changedRegion.yMax))
                : changedRegion;
            LoogaStampWorld.RevisionChanges++;
        }

        /// <summary>Takes the region of the changes since the last call. The editor updater calls it.</summary>
        internal bool TakeChangedRegion(out Rect region)
        {
            region = _changedRegion ?? default;
            bool changed = _changedRegion.HasValue;
            _changedRegion = null;
            return changed;
        }

        internal override Rect GetCullRect(HeightMapping mapping)
        {
            return GetArea();
        }
    }

    /// <summary>
    /// One tile for a <see cref="LoogaNeighbourhoodStamp"/>. Heights are in heightmap texture units: world height =
    /// <see cref="TerrainHeight"/> + value * <see cref="RealHeight"/>. The target is the evaluation state of the tile.
    /// Target texel (x, y) is the heightmap sample at world XZ <see cref="TexelOrigin"/> + (x, y) *
    /// <see cref="TexelSize"/>.
    /// </summary>
    public sealed class NeighbourhoodTile
    {
        private readonly NeighbourhoodStampPass _pass;
        private readonly StampTile _tile;
        private readonly LoogaNeighbourhoodStamp _stamp;
        private readonly HeightMapping _mapping;
        private readonly int _halo;
        private readonly UnityEngine.Terrain[] _neighbours;
        private readonly SplineDistanceField _splineFields;
        private readonly WaterDistanceField _waterFields;
        private RenderTexture _apron;

        internal NeighbourhoodTile(NeighbourhoodStampPass pass, in StampTile tile, LoogaNeighbourhoodStamp stamp,
            HeightMapping mapping, int halo, RenderTexture target, UnityEngine.Terrain[] neighbours,
            SplineDistanceField splineFields, WaterDistanceField waterFields)
        {
            _pass = pass;
            _tile = tile;
            _stamp = stamp;
            _mapping = mapping;
            _halo = halo;
            Target = target;
            _neighbours = neighbours;
            _splineFields = splineFields;
            _waterFields = waterFields;
        }

        /// <summary>Evaluation state of the tile (read and write, one float channel). The pass writes its result here.</summary>
        public RenderTexture Target { get; }

        /// <summary>World XZ rectangle of the terrain tile.</summary>
        public Rect WorldRect => _tile.WorldRect;

        /// <summary>Distance between heightmap samples in metres.</summary>
        public float TexelSize => _tile.Size.x / (_tile.Resolution - 1);

        /// <summary>World XZ position of target texel (0, 0).</summary>
        public Vector2 TexelOrigin => new Vector2(_tile.Position.x, _tile.Position.z) - Vector2.one * (_halo * TexelSize);

        /// <summary>Height of one heightmap texture unit in metres.</summary>
        public float RealHeight => _tile.RealHeight;

        /// <summary>World height of heightmap value 0.</summary>
        public float TerrainHeight => _tile.Position.y;

        /// <summary>Apron texels on each side of the target. Apron texel (x + reach, y + reach) is target texel (x, y).</summary>
        public int ApronReach => NeighbourhoodStampPass.Reach(_tile, _stamp);

        /// <summary>
        /// Heights before the stamp over the target plus <see cref="ApronReach"/> texels on each side (one float
        /// channel). Past the edge of the world the apron repeats the edge samples. The stamp world releases the apron
        /// after <see cref="LoogaNeighbourhoodStamp"/> Apply.
        /// </summary>
        public Texture GetApron()
        {
            if (!_apron)
            {
                _apron = _pass.BuildApron(_tile, _stamp, _mapping, _halo, Target, _neighbours);
            }
            return _apron;
        }

        internal void ApplySmooth(LoogaSmoothStamp stamp)
        {
            _pass.ApplySmooth(_tile, stamp, _mapping, _halo, Target, _neighbours, _splineFields, _waterFields);
        }

        internal void ApplyTalus(LoogaTalusStamp stamp)
        {
            _pass.ApplyTalus(_tile, stamp, _mapping, _halo, Target, _neighbours, _splineFields, _waterFields);
        }

        internal void ApplyErosion(LoogaErosionStamp stamp)
        {
            _pass.ApplyErosion(_tile, stamp, _mapping, _halo, Target, _neighbours, _splineFields, _waterFields);
        }

        internal void Release()
        {
            if (_apron)
            {
                RenderTexture.ReleaseTemporary(_apron);
            }
            _apron = null;
        }
    }
}
