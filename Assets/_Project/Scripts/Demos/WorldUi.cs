using CardboardXR.Interaction;
using CardboardXR.Stereo;
using CardboardXR.UI;
using UnityEngine;

namespace CardboardXR.Demos
{
    // Helpers for demos that build their panels at runtime with the Material kit (CardboardXR.UI.M3):
    // a panel the gaze pointer works (look, then pinch or tap the screen), laid out in dp, and where
    // to put it.
    public static class WorldUi
    {
        // A frosted-glass spatial panel the gaze/watch pointer works, laid out in dp at its distance,
        // with a delicate visionOS glass edge rim.
        public static RectTransform Panel(string name, Transform parent, Vector2 sizeDp, float distance, Color? color = null,
            float radius = M3Theme.k_ShapePanel, float scaleFactor = 1f)
        {
            var panel = M3.Canvas(name, parent, sizeDp, distance, color ?? M3Theme.SurfaceContainer, radius, scaleFactor);
            panel.gameObject.AddComponent<GazeCanvas>();
            return panel;
        }

        // A borderless visionOS spatial window with no opaque outer card (used when a RoundedScreen fills the window).
        public static RectTransform BorderlessWindow(string name, Transform parent, Vector2 sizeDp, float distance,
            float scaleFactor = 1f)
        {
            var panel = M3.Canvas(name, parent, sizeDp, distance, surface: null, scaleFactor: scaleFactor);
            panel.gameObject.AddComponent<GazeCanvas>();
            return panel;
        }

        // Puts a panel ahead of the head at a distance, turned yawDegrees from where it faces and
        // pitchDegrees above (+) or below (-) the eyes, strictly level (0 roll) with ergonomic tilt.
        public static void PlaceInFront(RectTransform panel, Transform head, float distance, float pitchDegrees, float yawDegrees = 0f)
        {
            var flat = Vector3.ProjectOnPlane(head.forward, Vector3.up);
            var yaw = flat.sqrMagnitude > 1e-4f ? Quaternion.LookRotation(flat.normalized) : Quaternion.identity;
            var position = head.position + yaw * Quaternion.Euler(-pitchDegrees, yawDegrees, 0f) * Vector3.forward * distance;
            panel.SetPositionAndRotation(position, M3Theme.FacingRotation(position, head.position));
            SpatialWindowAnchor.PinAtCurrentPose(panel);
        }

        public static void BringToFront(RectTransform panel, Transform head, float distance, float pitchDegrees = 0f, float yawDegrees = 0f)
        {
            if (panel == null)
                return;
            if (SpatialWindowAnchor.IsStowed(panel))
                SpatialWindowAnchor.SetFloatingStowed(false);
            if (!M3Fade.IsShown(panel.gameObject))
                M3Fade.Show(panel.gameObject);
            if (head != null)
                PlaceInFront(panel, head, distance, pitchDegrees, yawDegrees);
        }

        // An empty area to group elements, such as one page of a panel.
        public static RectTransform CreateGroup(string name, RectTransform parent) => M3.Group(name, parent);
    }
}
