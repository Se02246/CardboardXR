using CardboardXR.UI;
using UnityEngine;

namespace CardboardXR.Demos
{
    // The lobby's one line of guidance, low in front of where the user starts: how to bring back the
    // settings panel and its app library, which open by themselves on entering the lobby (AppLibrary)
    // but can be closed. The lobby is otherwise just a room to start from.
    public class LobbyHint : MonoBehaviour
    {
        [Tooltip("How far the hint is from the user, in meters.")]
        [SerializeField] float m_Distance = M3Theme.k_PanelDistance;
        [Tooltip("Where it is, in degrees above (+) or below (-) eye level.")]
        [SerializeField] float m_Pitch = -21f;

        void Start()
        {
            // Floating guidance text removed for a clean, uncluttered spatial environment.
        }
    }
}
