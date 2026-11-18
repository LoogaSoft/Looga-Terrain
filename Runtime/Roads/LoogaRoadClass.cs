using System;
using UnityEngine;

namespace LoogaSoft.Terrain.Roads
{
    /// <summary>
    /// One kind of road of a <see cref="LoogaRoadNetwork"/>: its width, its embankment, its pad flare, its height
    /// profile and its surface. Roads name their class (<see cref="LoogaRoad"/>).
    /// </summary>
    [Serializable]
    public sealed class LoogaRoadClass
    {
        [Tooltip("Name that the roads use to select this class.")]
        [SerializeField] private string _name = "Main";

        [Header("Shape")]
        [Tooltip("Half of the road width, in meters.")]
        [Min(0.1f)]
        [SerializeField] private float _halfWidth = 5.625f;
        [Tooltip("Width of the cut and fill slope beside the road, in meters.")]
        [Min(0f)]
        [SerializeField] private float _embankment = 12f;
        [Tooltip("At a pad, each side widens by this much over the flare length, in meters.")]
        [Min(0f)]
        [SerializeField] private float _flareWidth = 7f;
        [Min(0f)]
        [SerializeField] private float _flareLength = 30f;

        [Header("Profile")]
        [Tooltip("Sigma of the ground filter of the height profile, in meters.")]
        [Min(0f)]
        [SerializeField] private float _smoothing = 15f;
        [Tooltip("Planned grade, in degrees.")]
        [Range(0f, 45f)]
        [SerializeField] private float _maxGrade = 10f;
        [Tooltip("Largest grade of a route that cannot follow the ground at the planned grade, in degrees.")]
        [Range(0f, 45f)]
        [SerializeField] private float _steepestGrade = 20f;

        [Header("Surface")]
        [Tooltip("Terrain layer that the road paints.")]
        [SerializeField] private TerrainLayer _surfaceLayer;
        [Tooltip("Paved roads, and pads that paint the same layer, get asphalt slabs.")]
        [SerializeField] private bool _paved = true;

        public string Name => _name;
        public float HalfWidth => _halfWidth;
        public float Embankment => _embankment;
        public float FlareWidth => _flareWidth;
        public float FlareLength => _flareLength;
        public float Smoothing => _smoothing;
        public float MaxGrade => _maxGrade;
        public float SteepestGrade => _steepestGrade;
        public TerrainLayer SurfaceLayer => _surfaceLayer;
        public bool Paved => _paved;

        public LoogaRoadClass()
        {
        }

        public LoogaRoadClass(string name, float halfWidth, float embankment, float flareWidth, float flareLength,
            float smoothing, float maxGrade, float steepestGrade, TerrainLayer surfaceLayer, bool paved)
        {
            _name = name;
            _halfWidth = halfWidth;
            _embankment = embankment;
            _flareWidth = flareWidth;
            _flareLength = flareLength;
            _smoothing = smoothing;
            _maxGrade = maxGrade;
            _steepestGrade = steepestGrade;
            _surfaceLayer = surfaceLayer;
            _paved = paved;
        }
    }
}
