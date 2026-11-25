using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;

namespace CardboardXR.Demos
{
    // Shows an app's tagline under the library's grid while the gaze is on its tile.
    public class AppTileGaze : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler
    {
        [SerializeField] TMP_Text m_Target;
        [SerializeField] string m_Tagline;
        [SerializeField] string m_Prompt;

        public void Initialize(TMP_Text target, string tagline, string prompt)
        {
            m_Target = target;
            m_Tagline = tagline;
            m_Prompt = prompt;
        }

        public void OnPointerEnter(PointerEventData eventData)
        {
            if (m_Target == null)
                return;
            m_Target.text = AppLibrary.IsReorderModeGlobal ? AppLibrary.ReorderPrompt : m_Tagline;
        }

        public void OnPointerExit(PointerEventData eventData)
        {
            if (m_Target == null)
                return;
            var fallback = AppLibrary.IsReorderModeGlobal ? AppLibrary.ReorderPrompt : m_Prompt;
            if (m_Target.text == m_Tagline || m_Target.text == AppLibrary.ReorderPrompt)
                m_Target.text = fallback;
        }
    }
}
