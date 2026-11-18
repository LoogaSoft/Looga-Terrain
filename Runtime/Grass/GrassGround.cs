using System.Collections.Generic;
using UnityEngine;

namespace LoogaSoft.Terrain.Grass
{
    /// <summary>
    /// One submesh whose surface is ground that grows grass, for example the ground-covered top of a rock wall. With a
    /// ground mask, red of the mask at the first UV marks the ground for each pixel (the vertex cover weight is then a
    /// lower limit), and the grass thins out where red falls toward the edge of the ground.
    /// </summary>
    public readonly struct GrassGroundMesh
    {
        public readonly Mesh Mesh;
        public readonly Matrix4x4 LocalToWorld;
        public readonly int SubMesh;
        public readonly Texture GroundMask;

        public GrassGroundMesh(Mesh mesh, Matrix4x4 localToWorld, int subMesh) : this(mesh, localToWorld, subMesh, null)
        {
        }

        public GrassGroundMesh(Mesh mesh, Matrix4x4 localToWorld, int subMesh, Texture groundMask)
        {
            Mesh = mesh;
            LocalToWorld = localToWorld;
            SubMesh = subMesh;
            GroundMask = groundMask;
        }
    }

    /// <summary>
    /// A generated object with mesh parts that show ground, for example the top of a cliff wall. The procedural grass
    /// grows on these parts as on the terrain. The vertices hold in TEXCOORD2 the world XZ offset to the terrain point
    /// whose ground they show (xy) and a cover weight (z), as for the terrain surface mesh. The grass takes its type
    /// and density from that terrain point.
    /// </summary>
    public interface IGrassGround
    {
        /// <summary>World bounds of the ground parts.</summary>
        Bounds GrassGroundBounds { get; }

        /// <summary>Adds the ground parts.</summary>
        void GetGrassGroundMeshes(List<GrassGroundMesh> result);
    }

    /// <summary>
    /// The objects with ground parts that the procedural grass grows on. An object registers after it builds its
    /// meshes and unregisters before it destroys them. The grass renderers draw the ground parts from above again
    /// after each change.
    /// </summary>
    public static class GrassGround
    {
        private static readonly List<IGrassGround> Surfaces = new();

        /// <summary>Changes after each registry change. The grass renderers compare it.</summary>
        internal static int Revision { get; private set; }

        internal static IReadOnlyList<IGrassGround> All => Surfaces;

        /// <summary>Adds the object, or marks it as changed when it is already in the registry.</summary>
        public static void Register(IGrassGround surface)
        {
            if (surface == null) return;
            if (!Surfaces.Contains(surface)) Surfaces.Add(surface);
            Revision++;
        }

        /// <summary>Removes the object.</summary>
        public static void Unregister(IGrassGround surface)
        {
            if (Surfaces.Remove(surface)) Revision++;
        }
    }
}
