using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace CardboardXR.UI
{
    // Forces UI graphics and TMP labels on palm/overlay canvases to render in a higher render queue
    // (above the 2D hand contour shader at Transparent+20 = 3020, below GazeReticle at Overlay = 4000)
    // so the palm app window is ALWAYS cleanly on top of hand contours.
    public class UiRenderQueue : MonoBehaviour, IMaterialModifier
    {
        public const int k_HandContourQueue = 3020;
        public const int k_PalmOverlayQueue = 3050;
        public const int k_HandContourSortingOrder = 10;
        public const int k_PalmOverlaySortingOrder = 50;
        public const int k_HandRaySortingOrder = 100;
        public const int k_ReticleSortingOrder = 1000;
        public const int k_ScreenFadeSortingOrder = 2000;

        static readonly Dictionary<(Material, int), Material> s_Cache = new();

        [SerializeField] int m_Queue = k_PalmOverlayQueue;

        void OnEnable() => ApplySelf();

        void Start() => ApplySelf();

        void ApplySelf()
        {
            if (TryGetComponent<TMP_Text>(out var tmp))
            {
                ApplyToTmp(tmp, m_Queue);
                return;
            }
            if (TryGetComponent<Graphic>(out var g))
                g.SetMaterialDirty();
        }

        static void ApplyToTmp(TMP_Text tmp, int queue)
        {
            if (tmp == null)
                return;
            var baseMat = tmp.fontSharedMaterial != null
                ? tmp.fontSharedMaterial
                : (tmp.font != null ? tmp.font.material : null);
            if (baseMat != null && baseMat.renderQueue != queue)
                tmp.fontSharedMaterial = GetQueuedMaterial(baseMat, queue);
        }

        public Material GetModifiedMaterial(Material baseMaterial) => GetQueuedMaterial(baseMaterial, m_Queue);

        public static Material GetQueuedMaterial(Material baseMaterial, int queue)
        {
            if (baseMaterial == null)
                return null;
            if (baseMaterial.renderQueue == queue)
                return baseMaterial;
            var key = (baseMaterial, queue);
            if (!s_Cache.TryGetValue(key, out var queued) || queued == null)
            {
                queued = new Material(baseMaterial)
                {
                    name = $"{baseMaterial.name} (Queue {queue})",
                    renderQueue = queue,
                    hideFlags = HideFlags.HideAndDontSave,
                };
                s_Cache[key] = queued;
            }
            else if (queued.renderQueue != queue)
            {
                queued.renderQueue = queue;
            }
            return queued;
        }

        public static void ApplyRecursive(Transform root, int queue = k_PalmOverlayQueue)
        {
            if (root == null)
                return;
            if (root.TryGetComponent<Canvas>(out var canvas))
            {
                canvas.overrideSorting = true;
                canvas.sortingOrder = k_PalmOverlaySortingOrder;
            }
            var graphics = root.GetComponentsInChildren<Graphic>(true);
            for (var i = 0; i < graphics.Length; i++)
            {
                var g = graphics[i];
                if (g == null)
                    continue;
                if (!g.TryGetComponent<UiRenderQueue>(out var mod))
                    mod = g.gameObject.AddComponent<UiRenderQueue>();
                mod.m_Queue = queue;
                if (g is TMP_Text tmp)
                    ApplyToTmp(tmp, queue);
                else
                    g.SetMaterialDirty();
            }
        }
    }
}
