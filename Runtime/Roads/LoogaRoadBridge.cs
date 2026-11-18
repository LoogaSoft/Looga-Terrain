using UnityEngine;

namespace LoogaSoft.Terrain.Roads
{
    /// <summary>
    /// A straight road bridge between two end markers at the deck top. Move the markers to the road ends. The editor
    /// builds the deck, the railings, the piers and the abutments as generated children, keeps the ground under the deck
    /// below it and gives rock faces an opening under it.
    /// </summary>
    /// <remarks>
    /// The bridge takes the width of the widest road class whose road ends at one of its markers, or at a bridge that
    /// such a road reaches. Without such a road it takes the narrowest class.
    /// A structure deck is the deck of another structure, such as a dam crest road. Roads join its ends as at any
    /// bridge, but the editor builds no parts, ground clearance or rock openings for it and does not move its markers:
    /// the road ends take the marker heights.
    /// </remarks>
    [DisallowMultipleComponent]
    [AddComponentMenu("Looga/Roads/Road Bridge")]
    public sealed class LoogaRoadBridge : MonoBehaviour
    {
        [Tooltip("Deck top at the first end.")]
        [SerializeField] private Transform _start;
        [Tooltip("Deck top at the second end.")]
        [SerializeField] private Transform _finish;
        [Tooltip("Another structure carries the deck. The editor builds no parts for it and keeps its markers fixed.")]
        [SerializeField] private bool _structureDeck;

        public Transform StartMarker => _start;
        public Transform FinishMarker => _finish;
        public bool HasMarkers => _start && _finish;
        public bool StructureDeck => _structureDeck;

        public void SetMarkers(Transform start, Transform finish)
        {
            _start = start;
            _finish = finish;
        }

        public void SetStructureDeck(bool structureDeck)
        {
            _structureDeck = structureDeck;
        }
    }
}
