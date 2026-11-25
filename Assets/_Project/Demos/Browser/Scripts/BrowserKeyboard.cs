using System;
using System.Collections.Generic;
using CardboardXR.Hands;
using CardboardXR.Stereo;
using CardboardXR.UI;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace CardboardXR.Demos.Browser
{
    // Floating Material 3 QWERTY keyboard for Web Cinema. Summoned by looking down and clicking the
    // Keyboard icon beside the Apps button, movable with gaze or Watch gyro, and includes Voice Dictation.
    public class BrowserKeyboard : MonoBehaviour
    {
        const float k_DefaultDistance = 1.28f;
        const float k_DefaultPitch = -18f;
        const float k_KeyHeight = 44f;
        const float k_KeyGap = 6f;
        const string k_Placeholder = "Scrivi o detta un indirizzo / ricerca...";

        static readonly Vector2 k_PanelSize = new(860f, 356f);

        RectTransform m_Panel;
        TMP_Text m_PreviewText;
        M3Button m_ShiftButton;
        M3Button m_DictationButton;
        Image m_DictationHalo;
        readonly List<(M3Button button, string lower, string upper)> m_LetterKeys = new();
        Func<Transform> m_HeadProvider;
        Func<BrowserWindow> m_TargetWindowProvider;
        string m_Buffer = "";
        string m_DictationPartial = "";
        bool m_Shift;
        bool m_WasDictating;

        public RectTransform Panel => m_Panel;
        public bool IsOpen => m_Panel != null && M3Fade.IsShown(m_Panel.gameObject);

        public static BrowserKeyboard Create(Transform parent, Func<Transform> headProvider,
            Func<BrowserWindow> targetWindowProvider)
        {
            var panel = WorldUi.Panel("Browser Keyboard", parent, k_PanelSize, k_DefaultDistance, radius: 44f, scaleFactor: 0.72f);
            var kb = panel.gameObject.AddComponent<BrowserKeyboard>();
            kb.m_Panel = panel;
            kb.m_HeadProvider = headProvider;
            kb.m_TargetWindowProvider = targetWindowProvider;
            kb.BuildUi();
            FloatingWindowHandle.Configure(panel, kb.Hide, restoreOnRecenter: false);
            panel.gameObject.SetActive(false);
            return kb;
        }

        public void Toggle()
        {
            if (IsOpen)
                Hide();
            else
                Show();
        }

        public void Show()
        {
            var head = m_HeadProvider?.Invoke();
            if (head != null)
                WorldUi.PlaceInFront(m_Panel, head, k_DefaultDistance, k_DefaultPitch);
            M3Fade.Show(m_Panel.gameObject);
            Debug.Log("[Browser] Floating keyboard opened");
        }

        public void Hide()
        {
            VoiceDictation.Stop(this);
            m_DictationPartial = "";
            UpdateDictationVisuals(forceReset: true);
            if (!IsOpen)
                return;
            M3Fade.Hide(m_Panel.gameObject);
            Debug.Log("[Browser] Floating keyboard closed");
        }

        void OnDestroy() => VoiceDictation.Stop(this);

        void Update()
        {
            if (!IsOpen)
                return;
            if (ReferenceEquals(VoiceDictation.ActiveOwner, this) || VoiceDictation.IsListening)
                VoiceDictation.Poll();
            UpdateDictationVisuals(forceReset: false);
        }

        void BuildUi()
        {
            // Top bar: live preview pill (non-Selectable so double-clicking it grabs & moves the keyboard) + Clear + Close.
            const float topY = 144f;
            var previewBg = M3.Surface("Preview Pill", m_Panel, new Vector2(-62f, topY), new Vector2(670f, 44f),
                M3Theme.GlassRecessed, M3Theme.k_ShapeFull, style: VisionSurfaceStyle.Recessed);
            m_PreviewText = M3.Text("Preview", previewBg.rectTransform, k_Placeholder, M3Theme.Type.BodyLarge,
                Vector2.zero, new Vector2(634f, 38f), M3Theme.OnSurfaceVariant, TextAlignmentOptions.Left);
            m_PreviewText.textWrappingMode = TextWrappingModes.NoWrap;

            M3.IconButton("Clear", m_Panel, "delete", M3Button.Variant.Tonal, new Vector2(315f, topY), ClearAll, 44f);
            M3.IconButton("Close", m_Panel, "close", M3Button.Variant.Tonal, new Vector2(373f, topY), Hide, 44f);

            // Row 1: 1-0 + Backspace
            BuildRow(90f, new (string label, float width, Action onClick, M3Button.Variant variant)[]
            {
                ("1", 64f, () => TypeChar("1"), M3Button.Variant.Tonal),
                ("2", 64f, () => TypeChar("2"), M3Button.Variant.Tonal),
                ("3", 64f, () => TypeChar("3"), M3Button.Variant.Tonal),
                ("4", 64f, () => TypeChar("4"), M3Button.Variant.Tonal),
                ("5", 64f, () => TypeChar("5"), M3Button.Variant.Tonal),
                ("6", 64f, () => TypeChar("6"), M3Button.Variant.Tonal),
                ("7", 64f, () => TypeChar("7"), M3Button.Variant.Tonal),
                ("8", 64f, () => TypeChar("8"), M3Button.Variant.Tonal),
                ("9", 64f, () => TypeChar("9"), M3Button.Variant.Tonal),
                ("0", 64f, () => TypeChar("0"), M3Button.Variant.Tonal),
                ("Canc", 94f, Backspace, M3Button.Variant.Outlined),
            });

            // Row 2: Q-P
            BuildLetterRow(36f, new[] { "q", "w", "e", "r", "t", "y", "u", "i", "o", "p" }, 74f);

            // Row 3: A-L + .
            BuildLetterRow(-18f, new[] { "a", "s", "d", "f", "g", "h", "j", "k", "l", "." }, 74f);

            // Row 4: Shift + Z-M + / + @
            BuildShiftRow(-72f);

            // Row 5: Voice Dictation + Space + Search/Enter
            m_DictationButton = M3.Button("Key_Dictation", m_Panel, "Detta", M3Button.Variant.Tonal,
                new Vector2(-341f, -126f), OnVoiceDictationClicked, "mic", width: 112f);
            if (m_DictationButton != null)
            {
                if (m_DictationButton.transform is RectTransform rt)
                    rt.sizeDelta = new Vector2(112f, k_KeyHeight);
                if (m_DictationButton.Container != null)
                    m_DictationButton.Container.rectTransform.sizeDelta = new Vector2(112f, k_KeyHeight);
                m_DictationHalo = M3.Surface("DictationHalo", (RectTransform)m_DictationButton.transform, Vector2.zero,
                    new Vector2(124f, 54f), new Color(0.35f, 0.84f, 1f, 0f), M3Theme.k_ShapeFull,
                    raycastTarget: false, style: VisionSurfaceStyle.Glossy);
                m_DictationHalo.transform.SetAsFirstSibling();
            }
            M3.Key("Key_Spazio", m_Panel, "Spazio", M3Button.Variant.Tonal, new Vector2(-64f, -126f),
                new Vector2(430f, k_KeyHeight), () => TypeChar(" "));
            M3.Key("Key_CercaInvio", m_Panel, "Cerca / Invio", M3Button.Variant.Filled, new Vector2(277f, -126f),
                new Vector2(240f, k_KeyHeight), Submit);
        }

        void BuildRow(float y, (string label, float width, Action onClick, M3Button.Variant variant)[] keys)
        {
            var total = (keys.Length - 1) * k_KeyGap;
            for (var i = 0; i < keys.Length; i++)
                total += keys[i].width;
            var cursorX = -total * 0.5f;
            for (var i = 0; i < keys.Length; i++)
            {
                var k = keys[i];
                var centerX = cursorX + k.width * 0.5f;
                M3.Key($"Key_{k.label}", m_Panel, k.label, k.variant, new Vector2(centerX, y),
                    new Vector2(k.width, k_KeyHeight), () => k.onClick());
                cursorX += k.width + k_KeyGap;
            }
        }

        void BuildLetterRow(float y, string[] keys, float keyWidth)
        {
            var total = keys.Length * keyWidth + (keys.Length - 1) * k_KeyGap;
            var cursorX = -total * 0.5f;
            for (var i = 0; i < keys.Length; i++)
            {
                var lower = keys[i];
                var upper = lower.ToUpperInvariant();
                var centerX = cursorX + keyWidth * 0.5f;
                var btn = M3.Key($"Key_{lower}", m_Panel, lower, M3Button.Variant.Tonal, new Vector2(centerX, y),
                    new Vector2(keyWidth, k_KeyHeight), () => TypeChar(m_Shift ? upper : lower));
                if (lower != upper)
                    m_LetterKeys.Add((btn, lower, upper));
                cursorX += keyWidth + k_KeyGap;
            }
        }

        void BuildShiftRow(float y)
        {
            var letters = new[] { "z", "x", "c", "v", "b", "n", "m" };
            const float shiftW = 104f;
            const float letterW = 72f;
            const float symW = 66f;
            var total = shiftW + letters.Length * letterW + symW * 2f + 9f * k_KeyGap;
            var cursorX = -total * 0.5f;

            m_ShiftButton = M3.Key("Key_Shift", m_Panel, "Maiusc", M3Button.Variant.Outlined,
                new Vector2(cursorX + shiftW * 0.5f, y), new Vector2(shiftW, k_KeyHeight), ToggleShift);
            cursorX += shiftW + k_KeyGap;

            for (var i = 0; i < letters.Length; i++)
            {
                var lower = letters[i];
                var upper = lower.ToUpperInvariant();
                var btn = M3.Key($"Key_{lower}", m_Panel, lower, M3Button.Variant.Tonal,
                    new Vector2(cursorX + letterW * 0.5f, y), new Vector2(letterW, k_KeyHeight),
                    () => TypeChar(m_Shift ? upper : lower));
                m_LetterKeys.Add((btn, lower, upper));
                cursorX += letterW + k_KeyGap;
            }

            M3.Key("Key_Slash", m_Panel, "/", M3Button.Variant.Tonal,
                new Vector2(cursorX + symW * 0.5f, y), new Vector2(symW, k_KeyHeight), () => TypeChar("/"));
            cursorX += symW + k_KeyGap;

            M3.Key("Key_At", m_Panel, "@", M3Button.Variant.Tonal,
                new Vector2(cursorX + symW * 0.5f, y), new Vector2(symW, k_KeyHeight), () => TypeChar("@"));
        }

        void ToggleShift()
        {
            m_Shift = !m_Shift;
            if (m_ShiftButton != null)
                m_ShiftButton.Selected = m_Shift;
            for (var i = 0; i < m_LetterKeys.Count; i++)
            {
                var (btn, lower, upper) = m_LetterKeys[i];
                btn.Text = m_Shift ? upper : lower;
            }
        }

        void TypeChar(string ch)
        {
            if (string.IsNullOrEmpty(ch))
                return;
            m_Buffer += ch;
            RefreshPreview();
            m_TargetWindowProvider?.Invoke()?.TypeText(ch, false);
        }

        void OnVoiceDictationClicked()
        {
            m_DictationPartial = "";
            VoiceDictation.Toggle(this, OnVoiceFinalText, OnVoicePartialText);
            UpdateDictationVisuals(forceReset: false);
            RefreshPreview();
        }

        void OnVoicePartialText(string partial)
        {
            if (!ReferenceEquals(VoiceDictation.ActiveOwner, this))
                return;
            m_DictationPartial = partial ?? "";
            RefreshPreview();
        }

        void OnVoiceFinalText(string spoken)
        {
            m_DictationPartial = "";
            var chunk = VoiceDictation.FormatDictatedAppend(m_Buffer, spoken);
            if (string.IsNullOrEmpty(chunk))
            {
                RefreshPreview();
                return;
            }
            m_Buffer += chunk;
            RefreshPreview();
            m_TargetWindowProvider?.Invoke()?.TypeText(chunk, false);
        }

        void UpdateDictationVisuals(bool forceReset)
        {
            if (m_DictationButton == null)
                return;
            var listeningHere = !forceReset && ReferenceEquals(VoiceDictation.ActiveOwner, this) && VoiceDictation.IsListening;
            if (listeningHere || m_WasDictating || forceReset)
            {
                var (containerColor, haloColor, haloScale) = VoiceDictation.ComputeButtonVisuals(
                    listeningHere, VoiceDictation.VoiceLevel01, Time.unscaledTime);
                if (m_DictationButton.Container != null)
                    m_DictationButton.Container.color = containerColor;
                if (m_DictationButton.IconGraphic != null)
                    m_DictationButton.IconGraphic.color = listeningHere ? Color.white : M3Theme.OnSecondaryContainer;
                if (m_DictationButton.Label != null)
                    m_DictationButton.Label.color = listeningHere ? Color.white : M3Theme.OnSecondaryContainer;
                if (m_DictationHalo != null)
                {
                    m_DictationHalo.color = haloColor;
                    m_DictationHalo.rectTransform.localScale = new Vector3(haloScale, haloScale, 1f);
                }
                if (!listeningHere && !forceReset)
                    m_DictationButton.Apply();
                if (m_WasDictating != listeningHere)
                {
                    m_WasDictating = listeningHere;
                    RefreshPreview();
                }
            }
        }

        void Backspace()
        {
            if (m_Buffer.Length > 0)
                m_Buffer = m_Buffer.Substring(0, m_Buffer.Length - 1);
            RefreshPreview();
            m_TargetWindowProvider?.Invoke()?.TypeText("\b", false);
        }

        void ClearAll()
        {
            m_Buffer = "";
            m_DictationPartial = "";
            RefreshPreview();
            m_TargetWindowProvider?.Invoke()?.ClearInput();
        }

        void Submit()
        {
            m_TargetWindowProvider?.Invoke()?.TypeText("", true);
            m_Buffer = "";
            m_DictationPartial = "";
            RefreshPreview();
            Hide();
        }

        void RefreshPreview()
        {
            if (m_PreviewText == null)
                return;
            var listeningHere = ReferenceEquals(VoiceDictation.ActiveOwner, this) && VoiceDictation.IsListening;
            if (listeningHere)
            {
                var chunk = VoiceDictation.FormatDictatedAppend(m_Buffer, m_DictationPartial);
                var combined = m_Buffer + chunk;
                if (!string.IsNullOrEmpty(combined))
                {
                    m_PreviewText.text = combined + "|";
                    m_PreviewText.color = M3Theme.OnSurface;
                }
                else
                {
                    m_PreviewText.text = string.IsNullOrEmpty(VoiceDictation.LastStatus) ? "In ascolto..." : VoiceDictation.LastStatus;
                    m_PreviewText.color = new Color(0.45f, 0.88f, 1f, 1f);
                }
                return;
            }
            if (string.IsNullOrEmpty(m_Buffer) && !string.IsNullOrEmpty(VoiceDictation.LastStatus) && ReferenceEquals(VoiceDictation.ActiveOwner, null))
            {
                var status = VoiceDictation.LastStatus;
                if (!string.IsNullOrEmpty(status))
                {
                    m_PreviewText.text = status;
                    m_PreviewText.color = M3Theme.OnSurfaceVariant;
                    return;
                }
            }
            var hasText = !string.IsNullOrEmpty(m_Buffer);
            m_PreviewText.text = hasText ? m_Buffer + "|" : k_Placeholder;
            m_PreviewText.color = hasText ? M3Theme.OnSurface : M3Theme.OnSurfaceVariant;
        }
    }
}
