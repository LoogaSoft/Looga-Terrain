using LoogaSoft.Terrain.Stamps;
using UnityEngine;
using UnityEngine.Splines;

namespace LoogaSoft.Terrain.Roads
{
    /// <summary>
    /// A set of road pieces of one class: a spline path with one spline for each piece, under a
    /// <see cref="LoogaRoadNetwork"/>. Roads of a paved class get asphalt slabs.
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(SplineContainer))]
    [AddComponentMenu("Looga/Roads/Road")]
    public sealed class LoogaRoad : MonoBehaviour
    {
        [Tooltip("Name of the road class of the network.")]
        [SerializeField] private string _roadClass = "Main";
        [Tooltip("The height profile follows the ground along the road, with no grade limit. The road stays level " +
                 "across its width.")]
        [SerializeField] private bool _followGround;

        public string RoadClassName => _roadClass;
        public bool FollowGround => _followGround;
        public SplineContainer Container => GetComponent<SplineContainer>();
        public LoogaSplinePath Path => GetComponent<LoogaSplinePath>();
        public LoogaRoadNetwork Network => GetComponentInParent<LoogaRoadNetwork>();

        /// <summary>The road class from the network, or null.</summary>
        public LoogaRoadClass RoadClass
        {
            get
            {
                LoogaRoadNetwork network = Network;
                return network ? network.GetClass(_roadClass) : null;
            }
        }

        public void Configure(string roadClass, bool followGround)
        {
            _roadClass = roadClass;
            _followGround = followGround;
        }
    }
}
