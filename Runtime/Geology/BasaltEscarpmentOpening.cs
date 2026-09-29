using System;
using System.Collections.Generic;
using UnityEngine;

namespace LoogaSoft.Terrain.Geology
{
    public enum BasaltOpeningMode
    {
        RemoveColumns,
        RecessColumnTops
    }

    [ExecuteAlways]
    [DisallowMultipleComponent]
    [AddComponentMenu("Looga Terrain/Basalt Escarpment Opening")]
    public sealed class BasaltEscarpmentOpening : MonoBehaviour
    {
        [SerializeField] private BasaltOpeningMode _mode = BasaltOpeningMode.RecessColumnTops;
        [SerializeField, Min(0.1f)] private float _width = 70f;
        [SerializeField, Min(0f)] private float _shoulderFalloff = 24f;
        [SerializeField, Min(0.1f)] private float _depth = 100f;
        [SerializeField, Min(0f), Tooltip("Removes the front columns so water can render in front of the recessed wall.")]
        private float _frontClearance;
        [SerializeField, Min(0f)] private float _forwardAllowance = 12f;
        [SerializeField, Tooltip("Moves front columns behind the opening plane instead of removing them.")]
        private bool _repositionFrontColumns;
        [SerializeField, Min(0f)] private float _repositionDepthVariation = 10f;
        [SerializeField, Min(0.5f)] private float _minimumColumnHeight = 2f;
        [SerializeField, Min(0f)] private float _topVariation = 2.5f;

        public BasaltOpeningMode Mode => _mode;
        public float Width => _width;
        public float ShoulderFalloff => _shoulderFalloff;
        public float Depth => _depth;

        public bool Apply(ref BasaltColumnPlacement placement)
        {
            Vector3 basePosition = placement.Matrix.MultiplyPoint3x4(Vector3.down * 0.5f);
            Vector3 local = transform.InverseTransformPoint(basePosition);
            float halfWidth = _width * 0.5f;
            float lateralDistance = Mathf.Abs(local.x);
            if (lateralDistance > halfWidth + _shoulderFalloff ||
                local.z < -_depth || local.z > _forwardAllowance)
            {
                return true;
            }

            float influence = _shoulderFalloff <= 0.001f
                ? 1f
                : 1f - Mathf.SmoothStep(0f, 1f,
                    Mathf.InverseLerp(halfWidth, halfWidth + _shoulderFalloff,
                        lateralDistance));
            if (_mode == BasaltOpeningMode.RemoveColumns && influence >= 0.999f)
                return false;
            if (_mode == BasaltOpeningMode.RecessColumnTops &&
                _frontClearance > 0.001f && local.z > -_frontClearance &&
                influence > 0.001f)
            {
                if (!_repositionFrontColumns) return false;

                float positionNoise = StableUnit(basePosition + Vector3.one * 17.3f);
                float targetDepth = -_frontClearance -
                    positionNoise * _repositionDepthVariation;
                Vector3 offset = transform.TransformVector(
                    Vector3.forward * ((targetDepth - local.z) * influence));
                Matrix4x4 shifted = placement.Matrix;
                Vector3 shiftedCenter = (Vector3)shifted.GetColumn(3) + offset;
                shifted.SetColumn(3, new Vector4(shiftedCenter.x, shiftedCenter.y,
                    shiftedCenter.z, 1f));
                placement = placement.WithMatrixAndHeight(shifted, placement.Height);
                basePosition += offset;
            }

            float random = StableUnit(basePosition);
            float targetTop = transform.position.y - random * _topVariation;
            float currentTop = basePosition.y + placement.Height;
            float blendedTop = Mathf.Lerp(currentTop, Mathf.Min(currentTop, targetTop), influence);
            float newHeight = Mathf.Max(_minimumColumnHeight, blendedTop - basePosition.y);
            if (newHeight >= placement.Height - 0.001f) return true;

            Matrix4x4 matrix = placement.Matrix;
            Vector3 axisY = matrix.GetColumn(1);
            if (axisY.sqrMagnitude < 0.0001f) return true;
            Vector3 newAxisY = axisY.normalized * newHeight;
            Vector3 center = basePosition + newAxisY * 0.5f;
            matrix.SetColumn(1, new Vector4(newAxisY.x, newAxisY.y, newAxisY.z, 0f));
            matrix.SetColumn(3, new Vector4(center.x, center.y, center.z, 1f));
            placement = placement.WithMatrixAndHeight(matrix, newHeight);
            return true;
        }

        public static void ApplyAll(List<BasaltColumnPlacement> placements,
            IReadOnlyList<BasaltEscarpmentOpening> openings)
        {
            if (placements == null) throw new ArgumentNullException(nameof(placements));
            if (openings == null || openings.Count == 0) return;

            for (int index = placements.Count - 1; index >= 0; index--)
            {
                BasaltColumnPlacement placement = placements[index];
                bool keep = true;
                foreach (BasaltEscarpmentOpening opening in openings)
                {
                    if (!opening || !opening.isActiveAndEnabled) continue;
                    if (!opening.Apply(ref placement))
                    {
                        keep = false;
                        break;
                    }
                }

                if (keep) placements[index] = placement;
                else placements.RemoveAt(index);
            }

            BasaltColumnLayoutGenerator.RecalculateContactOcclusion(placements);
        }

        private static float StableUnit(Vector3 position)
        {
            unchecked
            {
                uint hash = (uint)Mathf.FloorToInt(position.x * 11.7f) * 73856093u;
                hash ^= (uint)Mathf.FloorToInt(position.y * 7.3f) * 19349663u;
                hash ^= (uint)Mathf.FloorToInt(position.z * 13.1f) * 83492791u;
                hash ^= hash >> 16;
                hash *= 0x7FEB352Du;
                hash ^= hash >> 15;
                return (hash & 0x00FFFFFFu) / 16777216f;
            }
        }

        private void OnDrawGizmosSelected()
        {
            Matrix4x4 previous = Gizmos.matrix;
            Gizmos.matrix = transform.localToWorldMatrix;
            Gizmos.color = new Color(0.15f, 0.75f, 1f, 0.24f);
            Vector3 center = Vector3.back * ((_depth - _forwardAllowance) * 0.5f);
            float totalDepth = _depth + _forwardAllowance;
            Gizmos.DrawCube(center, new Vector3(_width, 0.4f, totalDepth));
            Gizmos.color = new Color(0.15f, 0.75f, 1f, 0.9f);
            Gizmos.DrawWireCube(center,
                new Vector3(_width + _shoulderFalloff * 2f, 0.5f, totalDepth));
            Gizmos.matrix = previous;
        }
    }
}
