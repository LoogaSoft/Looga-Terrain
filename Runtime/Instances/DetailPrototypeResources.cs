using LoogaSoft.Instancing;
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace LoogaSoft.Terrain.Instances
{
    internal sealed class DetailPrototypeResources : IDisposable
    {
        private readonly List<UnityEngine.Object> _owned = new();

        internal InstancePrototype Capture(DetailPrototype detail, InstanceMaterialProfile profile)
        {
            if (!detail.Validate(out string reason))
            {
                throw new NotSupportedException(reason);
            }
            if (detail.usePrototypeMesh)
            {
                if (!detail.useInstancing)
                {
                    throw new NotSupportedException("Native mesh details must use instancing.");
                }
                return InstancePrototype.FromPrefab(detail.prototype, profile);
            }
            var shader = Shader.Find("Looga/Terrain/Vegetation Lit");
            if (!shader || !shader.isSupported)
            {
                throw new NotSupportedException("The Looga vegetation shader is unavailable.");
            }
            var material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave, name = "Looga texture grass" };
            _owned.Add(material);
            material.SetTexture("_BaseMap", detail.prototypeTexture);
            material.SetColor("_BaseColor", Color.white);
            material.SetFloat("_Billboard", detail.renderMode == DetailRenderMode.GrassBillboard ? 1 : 0);
            material.EnableKeyword("_ALPHATEST_ON");
            var mesh = new Mesh { hideFlags = HideFlags.HideAndDontSave, name = "Looga grass cards" };
            _owned.Add(mesh);
            bool billboard = detail.renderMode == DetailRenderMode.GrassBillboard;
            var vertices = new List<Vector3>();
            var normals = new List<Vector3>();
            var tangents = new List<Vector4>();
            var uvs = new List<Vector2>();
            var triangles = new List<int>();
            for (int card = 0; card < (billboard ? 1 : 3); card++)
            {
                Quaternion rotation = Quaternion.Euler(0, card * 60, 0);
                int begin = vertices.Count;
                foreach (var vertex in new[] { new Vector3(-0.5f,0,0),new Vector3(0.5f,0,0),new Vector3(-0.5f,1,0),new Vector3(0.5f,1,0) })
                {
                    vertices.Add(rotation * vertex);
                    normals.Add(rotation * Vector3.forward);
                    tangents.Add(rotation * Vector3.right);
                }
                uvs.AddRange(new[] { Vector2.zero,Vector2.right,Vector2.up,Vector2.one });
                triangles.AddRange(new[] { begin,begin+2,begin+1,begin+1,begin+2,begin+3 });
            }
            for (int i = 0; i < tangents.Count; i++)
            {
                Vector4 tangent = tangents[i];
                tangent.w = 1;
                tangents[i] = tangent;
            }
            mesh.SetVertices(vertices);
            mesh.SetNormals(normals);
            mesh.SetTangents(tangents);
            mesh.SetUVs(0,uvs);
            mesh.SetTriangles(triangles,0);
            mesh.RecalculateBounds();
            if (billboard)
            {
                mesh.bounds = new Bounds(new Vector3(0,0.5f,0), Vector3.one);
            }
            var source = new GameObject("Looga detail prototype") { hideFlags = HideFlags.HideAndDontSave };
            _owned.Add(source);
            source.SetActive(false);
            source.AddComponent<MeshFilter>().sharedMesh = mesh;
            var renderer = source.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            return InstancePrototype.FromPrefab(source);
        }

        internal static InstanceAppearance Appearance(Vector3 position, DetailPrototype detail)
        {
            if (detail.usePrototypeMesh) return InstanceAppearance.Default;
            float spread = Mathf.Max(0.001f, detail.noiseSpread);
            float noise = Mathf.PerlinNoise(position.x / spread + detail.noiseSeed * 0.013f,
                position.z / spread + detail.noiseSeed * 0.027f);
            return new InstanceAppearance(Color.Lerp(detail.dryColor, detail.healthyColor, noise), Color.white,
                new Vector4(noise * 6.283185f, 0, 0, 0));
        }

        /// <remarks>Pass the terrain size and origin once per batch. Both values are native reads.</remarks>
        internal static Matrix4x4 Transform(DetailInstanceTransform item, DetailPrototype detail, TerrainData data, Vector3 size, Vector3 origin)
        {
            Quaternion rotation = Quaternion.Euler(0, item.rotationY * Mathf.Rad2Deg, 0);
            if (detail.alignToGround > 0)
            {
                Vector3 normal = data.GetInterpolatedNormal(item.posX / size.x, item.posZ / size.z);
                rotation = Quaternion.Slerp(Quaternion.identity, Quaternion.FromToRotation(Vector3.up, normal), detail.alignToGround) * rotation;
            }
            return Matrix4x4.TRS(origin + new Vector3(item.posX,item.posY,item.posZ), rotation, new Vector3(item.scaleXZ,item.scaleY,item.scaleXZ));
        }

        public void Dispose()
        {
            for (int i = _owned.Count - 1; i >= 0; i--)
            {
                UnityEngine.Object.DestroyImmediate(_owned[i]);
            }
            _owned.Clear();
        }
    }
}
