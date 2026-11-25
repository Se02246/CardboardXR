using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

namespace CardboardXR.Stereo
{
    // Places objects on real surfaces in 6DoF, in both VR and passthrough. A tap on a detected plane
    // (see PlacementSurface) anchors a copy of the object prefab there with ARCore. A tap on a placed
    // object removes it (see PlacedObject), and a carried one is anchored again where it's put down
    // (see Move). The anchors live in session space under this XR Origin,
    // which RoomBoundary moves with HeadPoseFusion's session-to-world pose, so the objects stay put in
    // the room. Leaving 6DoF stops ARCore, so the objects are hidden until it's back; if ARCore
    // restarts its session instead of resuming, it drops them. Only the ARAnchorManager destroys an
    // anchor's GameObject: it keeps updating the anchors it knows, so destroying one elsewhere breaks
    // it. Nothing deactivates one either, since a disabled ARAnchor removes itself from ARCore; the
    // placed objects under them are hidden instead. Put it on the XR Origin with the ARAnchorManager.
    public class AnchorPlacer : MonoBehaviour
    {
        const float k_StatusSeconds = 5f;
        // No new object is placed closer than this to an existing one's middle, in meters. On the
        // iPhone, reaching for a cube in passthrough, pinches that fell just short of it clicked the
        // table beside it and put new cubes there, next to the one being reached for. Placing objects
        // touching each other is rarely what anyone means. For a 10 cm cube, this leaves a gap of
        // about 8 cm around it.
        const float k_KeepApart = 0.15f;
        public const float k_SupportPadThickness = 0.16f;
        public static readonly Vector3 k_SupportPadSize = new(1.2f, k_SupportPadThickness, 1.2f);

        [SerializeField] HeadPoseFusion m_Fusion;
        [SerializeField] ARAnchorManager m_Anchors;
        [Tooltip("Placed at each anchor. Its pivot is its center, and it's lifted by half its height to sit on the surface.")]
        [SerializeField] GameObject m_ObjectPrefab;
        [Tooltip("Placing more than this many removes the oldest.")]
        [SerializeField] int m_MaxObjects = 10;

        // Each placed anchor, the object on it, where it was in session space when placed, and an
        // optional invisible physics support collider when placed outside an existing plane collider.
        readonly List<(ARAnchor anchor, GameObject placed, Vector3 placedAt, GameObject supportPad)> m_Placed = new();
        int m_PlacedCount;
        float m_NextStatus;

        // Controls which real surfaces SpatialSurfaceLocator targets (all horizontal vs floor-only).
        public PlacementSurfaceFilter SurfaceFilter { get; set; } = PlacementSurfaceFilter.HorizontalAny;

        // What a click on a surface places, so each demo can choose; null places nothing.
        public GameObject ObjectPrefab
        {
            get => m_ObjectPrefab;
            set => m_ObjectPrefab = value;
        }

        // Each newly placed object, once it's anchored.
        public event System.Action<GameObject> Placed;

        // Shared access to XR Origin's ARCore anchor manager for floating spatial windows (SpatialWindowAnchor).
        public static AnchorPlacer Instance { get; private set; }
        public HeadPoseFusion Fusion => m_Fusion;
        public ARAnchorManager AnchorManager => m_Anchors;
        public Transform Origin => transform;

        public bool HasDynamicObjects
        {
            get
            {
                for (var i = 0; i < m_Placed.Count; i++)
                {
                    var go = m_Placed[i].placed;
                    if (go != null && go.activeInHierarchy &&
                        go.TryGetComponent<Rigidbody>(out var rb) && !rb.isKinematic)
                        return true;
                }
                return false;
            }
        }

        void Awake()
        {
            Instance = this;
            if (GetComponent<SpatialSurfaceLocator>() == null)
            {
                if (GetComponent<ARRaycastManager>() == null)
                    gameObject.AddComponent<ARRaycastManager>().enabled = false;
                gameObject.AddComponent<SpatialSurfaceLocator>();
            }
        }

        void OnDestroy()
        {
            if (Instance == this)
                Instance = null;
        }

        void Update()
        {
            var sixDof = m_Fusion.CurrentMode == HeadPoseFusion.Mode.SixDof;
            if (m_Anchors.enabled != sixDof)
            {
                m_Anchors.enabled = sixDof;
                foreach (var (_, placed, _, supportPad) in m_Placed)
                {
                    if (placed != null)
                        placed.SetActive(sixDof);
                    if (supportPad != null)
                        supportPad.SetActive(sixDof);
                }
            }

            // ARCore can drop an anchor, which destroys its GameObject.
            m_Placed.RemoveAll(entry => entry.anchor == null);
            if (!sixDof || m_Placed.Count == 0 || Time.unscaledTime < m_NextStatus)
                return;
            m_NextStatus = Time.unscaledTime + k_StatusSeconds;
            LogStatus();
        }

        // Anchors a new object on the plane at a world point. The anchor isn't attached to the plane:
        // AR Foundation parents a plane-attached anchor to the plane's GameObject, and destroys it
        // with the plane when ARCore merges or drops the plane.
        public void Place(ARPlane plane, Vector3 point) =>
            Place(point, plane != null ? plane.normal : Vector3.up, plane, SpatialHitQuality.PlanePolygon);

        public async void Place(Vector3 point, Vector3 normal, ARPlane plane = null,
            SpatialHitQuality quality = SpatialHitQuality.PlanePolygon)
        {
            if (m_ObjectPrefab == null || !m_Anchors.enabled || m_Anchors.subsystem == null)
                return;
            if (TryFindNearby(point, out var nearby, out var distance))
            {
                Debug.Log($"[Anchor] Not placing {distance * 100f:F0} cm from {nearby.name}: a pinch there is " +
                          "more likely a grab that missed");
                return;
            }

            var head = m_Fusion.transform.position;
            var pose = PlacementPose(point, normal, head);
            var placedAt = InSession(pose.position);
            var result = await m_Anchors.TryAddAnchorAsync(pose);
            if (this == null)
                return;
            if (!result.status.IsSuccess() || result.value == null)
            {
                Debug.LogWarning($"[Anchor] ARCore didn't add an anchor ({quality}): {result.status}");
                return;
            }

            var anchor = result.value;
            // The prefab may have been cleared while ARCore added the anchor.
            if (m_ObjectPrefab == null)
            {
                m_Anchors.TryRemoveAnchor(anchor);
                return;
            }
            var placed = Instantiate(m_ObjectPrefab, anchor.transform);
            placed.name = $"Placed {++m_PlacedCount}";
            placed.transform.SetLocalPositionAndRotation(Vector3.up * (placed.transform.localScale.y * 0.5f + 0.002f), Quaternion.identity);

            GameObject supportPad = null;
            if (placed.TryGetComponent<Rigidbody>(out var body) && !body.isKinematic)
            {
                body.linearVelocity = Vector3.zero;
                body.angularVelocity = Vector3.zero;
                supportPad = new GameObject("Surface Support");
                // Ignore Raycast (layer 2): supports falling/resting cubes without blocking gaze or hand rays.
                supportPad.layer = 2;
                supportPad.transform.SetParent(anchor.transform, false);
                supportPad.transform.SetLocalPositionAndRotation(new Vector3(0f, -k_SupportPadThickness * 0.5f, 0f), Quaternion.identity);
                var box = supportPad.AddComponent<BoxCollider>();
                box.size = k_SupportPadSize;
            }

            m_Placed.Add((anchor, placed, placedAt, supportPad));
            var surfaceLabel = plane != null ? $"{plane.alignment} plane" : quality.ToString();
            Debug.Log($"[Anchor] {placed.name} via {quality} ({surfaceLabel}) {Vector3.Distance(head, point):F2} m away; " +
                      $"{m_Placed.Count} placed");
            Placed?.Invoke(placed);
            if (m_Placed.Count > m_MaxObjects)
                Remove(m_Placed[0].anchor);
        }

        // The placed object whose middle is nearest a world point, if one is within k_KeepApart.
        bool TryFindNearby(Vector3 point, out GameObject nearby, out float distance)
        {
            nearby = null;
            distance = float.PositiveInfinity;
            foreach (var (_, placed, _, _) in m_Placed)
            {
                if (placed == null)
                    continue;
                var d = Vector3.Distance(point, placed.transform.position);
                if (d < distance)
                {
                    distance = d;
                    nearby = placed;
                }
            }
            return distance < k_KeepApart;
        }

        // Anchors a placed object again at a new world pose, after it's been carried there: a new
        // anchor at the pose takes the object, keeping where it is, and then the old anchor goes.
        public async void Move(GameObject placed, Pose pose)
        {
            if (!m_Anchors.enabled || m_Anchors.subsystem == null || m_Placed.FindIndex(entry => entry.placed == placed) < 0)
                return;

            var result = await m_Anchors.TryAddAnchorAsync(pose);
            // It may have been removed while ARCore added the anchor.
            var index = m_Placed.FindIndex(entry => entry.placed == placed);
            if (this == null || placed == null || index < 0)
                return;
            if (!result.status.IsSuccess() || result.value == null)
            {
                Debug.LogWarning($"[Anchor] ARCore didn't anchor {placed.name} where it was put down: {result.status}");
                return;
            }

            var oldAnchor = m_Placed[index].anchor;
            var supportPad = m_Placed[index].supportPad;
            placed.transform.SetParent(result.value.transform, worldPositionStays: true);
            if (placed.TryGetComponent<Rigidbody>(out var body) && !body.isKinematic)
                supportPad = TransferOrCreateSupportPad(result.value.transform, placed, supportPad, pose.position.y);
            m_Placed[index] = (result.value, placed, InSession(pose.position), supportPad);
            if (oldAnchor != null && !m_Anchors.TryRemoveAnchor(oldAnchor))
                Debug.LogWarning($"[Anchor] ARCore didn't remove {placed.name}'s old anchor");
            Debug.Log($"[Anchor] Anchored {placed.name} again where it was put down");
        }

        // Transfers (or creates) a horizontal level Surface Support pad under a dynamic placed object on
        // its new anchor BEFORE removing the old anchor so the collider is never destroyed underneath it.
        public static GameObject TransferOrCreateSupportPad(Transform newAnchor, GameObject placed,
            GameObject existingPad, float fallbackBottomY)
        {
            if (newAnchor == null)
                return existingPad;

            Physics.SyncTransforms();
            var bottomY = fallbackBottomY;
            if (placed != null && placed.TryGetComponent<Collider>(out var col) && col.enabled)
                bottomY = col.bounds.min.y;

            var pad = existingPad;
            if (pad == null)
            {
                pad = new GameObject("Surface Support");
                pad.layer = 2;
                var box = pad.AddComponent<BoxCollider>();
                box.size = k_SupportPadSize;
            }

            var refPos = placed != null ? placed.transform.position : newAnchor.position;
            pad.transform.SetParent(null, worldPositionStays: true);
            pad.transform.SetPositionAndRotation(
                new Vector3(refPos.x, bottomY - k_SupportPadThickness * 0.5f, refPos.z),
                Quaternion.identity);
            pad.transform.SetParent(newAnchor, worldPositionStays: true);
            pad.SetActive(true);
            return pad;
        }

        public void Remove(ARAnchor anchor)
        {
            var index = m_Placed.FindIndex(entry => entry.anchor == anchor);
            if (index < 0)
                return;

            var placed = m_Placed[index].placed;
            var supportPad = m_Placed[index].supportPad;
            m_Placed.RemoveAt(index);
            // Hidden now; the manager destroys the anchor, and the object with it, once ARCore reports
            // the anchor removed.
            if (placed != null)
                placed.SetActive(false);
            if (supportPad != null)
                supportPad.SetActive(false);
            if (m_Anchors.enabled && m_Anchors.TryRemoveAnchor(anchor))
                Debug.Log($"[Anchor] Removed one; {m_Placed.Count} placed");
            else
                Debug.LogWarning($"[Anchor] ARCore didn't remove an anchor, so it stays hidden; {m_Placed.Count} placed");
        }

        // Removes every placed object, when leaving a demo.
        public void RemoveAll()
        {
            if (m_Placed.Count == 0)
                return;
            Debug.Log($"[Anchor] Removing all {m_Placed.Count}");
            for (var i = m_Placed.Count - 1; i >= 0; i--)
                Remove(m_Placed[i].anchor);
        }

        // How many anchors ARCore is tracking, and how far it has moved them in session space since
        // they were placed, which shows how much it corrects its map.
        void LogStatus()
        {
            var tracking = 0;
            var moved = 0f;
            // Whether each object's collider is where the object is, for the gaze to hit it.
            var colliderOffset = 0f;
            var withoutCollider = 0;
            foreach (var (anchor, placed, placedAt, _) in m_Placed)
            {
                if (anchor.trackingState == TrackingState.Tracking)
                    tracking++;
                moved = Mathf.Max(moved, Vector3.Distance(InSession(anchor.transform.position), placedAt));
                if (placed != null && placed.activeInHierarchy && placed.TryGetComponent<Collider>(out var collider) && collider.enabled)
                    colliderOffset = Mathf.Max(colliderOffset, Vector3.Distance(collider.bounds.center, placed.transform.position));
                else
                    withoutCollider++;
            }
            Debug.Log($"[Anchor] {m_Placed.Count} placed, {tracking} tracking; ARCore moved them up to {moved * 100f:F1} cm since placing; " +
                      $"colliders up to {colliderOffset * 100f:F1} cm off, {withoutCollider} without one");
        }

        // A world point in ARCore's session space. The trackables parent sits on this XR Origin with no
        // offset. An anchor's own local position won't do, since an anchor attached to a plane is
        // parented to the plane.
        Vector3 InSession(Vector3 world) => transform.InverseTransformPoint(world);

        // A pose at the point with up along the surface normal, facing the head across the surface.
        public static Pose PlacementPose(Vector3 point, Vector3 normal, Vector3 head)
        {
            var up = normal.normalized;
            var facing = Vector3.ProjectOnPlane(head - point, up);
            // Straight above or below the head, any direction along the surface will do.
            if (facing.sqrMagnitude < 1e-6f)
                facing = Vector3.ProjectOnPlane(Vector3.forward, up);
            if (facing.sqrMagnitude < 1e-6f)
                facing = Vector3.ProjectOnPlane(Vector3.right, up);
            return new Pose(point, Quaternion.LookRotation(facing.normalized, up));
        }
    }
}
