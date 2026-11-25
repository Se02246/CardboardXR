using System;
using UnityEngine;

namespace CardboardXR.UI
{
    // Fades and scales a panel cleanly in when shown and out when hidden, as Material's motion does
    // for surfaces that appear and leave: opacity with a subtle zoom, decelerating smoothly in and
    // shrinking cleanly out. Fading in is automatic whenever the object is turned on; to animate out,
    // hide it with M3Fade.Hide rather than SetActive(false), which disables it once finished (and
    // optionally invokes an onHidden callback). Show cancels a fade out.
    [DisallowMultipleComponent]
    public class M3Fade : MonoBehaviour
    {
        public const float k_InSeconds = 0.24f;
        public const float k_OutSeconds = 0.18f;
        // The panel grows from k_StartScale when opening and shrinks toward k_EndScale when closing.
        const float k_StartScale = 0.86f;
        const float k_EndScale = 0.88f;
        public const float k_ClickGuardSeconds = 0.35f;

        CanvasGroup m_Group;
        Vector3 m_BaseScale = Vector3.one;
        float m_Value;
        bool m_Hiding;
        float m_ShownAt;
        Action m_OnHidden;

        public static float EvaluateAlpha(float value01, bool hiding)
        {
            var t = Mathf.Clamp01(value01);
            if (!hiding)
            {
                var inv = 1f - t;
                return 1f - inv * inv * inv;
            }
            return t * t * (3f - 2f * t);
        }

        public static float EvaluateScaleFactor(float value01, bool hiding)
        {
            var eased = EvaluateAlpha(value01, hiding);
            return Mathf.Lerp(hiding ? k_EndScale : k_StartScale, 1f, eased);
        }

        void Awake()
        {
            m_Group = GetComponent<CanvasGroup>();
            if (m_Group == null)
                m_Group = gameObject.AddComponent<CanvasGroup>();
        }

        // From nothing, each time it's turned on. Its size is taken now, after whatever set it (such
        // as M3Canvas's dp scale) has run.
        void OnEnable()
        {
            if (m_Hiding)
                return;
            m_BaseScale = transform.localScale;
            m_Value = 0f;
            m_ShownAt = Time.unscaledTime;
            m_OnHidden = null;
            Apply();
        }

        void OnDisable()
        {
            // Left at full size, so the next fade in starts from the right one.
            if (m_Group != null)
                transform.localScale = m_BaseScale;
            m_Hiding = false;
            m_OnHidden = null;
        }

        void Update()
        {
            var clickable = !m_Hiding && Time.unscaledTime - m_ShownAt >= k_ClickGuardSeconds;
            if (m_Group.interactable != clickable)
                m_Group.interactable = clickable;
            var target = m_Hiding ? 0f : 1f;
            if (m_Value == target)
            {
                if (m_Hiding)
                {
                    m_Hiding = false;
                    transform.localScale = m_BaseScale;
                    var cb = m_OnHidden;
                    m_OnHidden = null;
                    gameObject.SetActive(false);
                    cb?.Invoke();
                }
                return;
            }
            m_Value = Mathf.MoveTowards(m_Value, target, Time.unscaledDeltaTime / (m_Hiding ? k_OutSeconds : k_InSeconds));
            Apply();
        }

        void Apply()
        {
            if (m_Group == null)
            {
                m_Group = GetComponent<CanvasGroup>();
                if (m_Group == null)
                    m_Group = gameObject.AddComponent<CanvasGroup>();
            }
            m_Group.alpha = EvaluateAlpha(m_Value, m_Hiding);
            m_Group.blocksRaycasts = !m_Hiding;
            transform.localScale = m_BaseScale * EvaluateScaleFactor(m_Value, m_Hiding);
        }

        // Shows an object, fading it in if it can; cancels a fade out.
        public static void Show(GameObject target)
        {
            if (target == null)
                return;
            if (target.TryGetComponent<M3Fade>(out var fade) && fade.m_Hiding)
            {
                fade.m_Hiding = false;
                fade.m_OnHidden = null;
                fade.Apply();
            }
            if (!target.activeSelf)
                target.SetActive(true);
        }

        // Hides an object, fading it out first if it can and it's showing.
        public static void Hide(GameObject target) => Hide(target, null);

        public static void Hide(GameObject target, Action onHidden)
        {
            if (target == null)
            {
                onHidden?.Invoke();
                return;
            }
            if (!target.activeSelf)
            {
                onHidden?.Invoke();
                return;
            }
            if (!target.activeInHierarchy || !target.TryGetComponent<M3Fade>(out var fade) || !fade.enabled)
            {
                target.SetActive(false);
                onHidden?.Invoke();
                return;
            }
            if (fade.m_Group == null)
                fade.m_Group = target.GetComponent<CanvasGroup>() ?? target.AddComponent<CanvasGroup>();
            fade.m_Hiding = true;
            fade.m_OnHidden = onHidden;
            fade.Apply();
        }

        public static void SetShown(GameObject target, bool shown)
        {
            if (shown)
                Show(target);
            else
                Hide(target);
        }

        // Whether it's showing and staying: on, and not fading out.
        public static bool IsShown(GameObject target) =>
            target != null && target.activeSelf && (!target.TryGetComponent<M3Fade>(out var fade) || !fade.m_Hiding);

        public static Vector3 GetBaseScale(GameObject target)
        {
            if (target == null)
                return Vector3.one;
            return target.TryGetComponent<M3Fade>(out var fade) ? fade.m_BaseScale : target.transform.localScale;
        }

        public static void SetBaseScale(GameObject target, Vector3 scale)
        {
            if (target == null)
                return;
            if (target.TryGetComponent<M3Fade>(out var fade))
            {
                fade.m_BaseScale = scale;
                if (target.activeSelf && fade.m_Group != null)
                {
                    target.transform.localScale = scale * EvaluateScaleFactor(fade.m_Value, fade.m_Hiding);
                    return;
                }
            }
            target.transform.localScale = scale;
        }

        // Keeps M3Fade's stored base scale in sync after runtime resizes (e.g. Watch crown or palm-mode Home).
        public static void SyncBaseScale(GameObject target)
        {
            if (target != null && target.TryGetComponent<M3Fade>(out var fade))
                fade.m_BaseScale = target.transform.localScale;
        }
    }
}
