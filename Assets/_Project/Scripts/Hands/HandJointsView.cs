using System;
using System.Collections.Generic;
using CardboardXR.Stereo;
using CardboardXR.UI;
using UnityEngine;

namespace CardboardXR.Hands
{
    // Renders a sleek, high-contrast 2D outer contour of the user's tracked hand (with organic
    // wrist/forearm cuff and pinch ring feedback) using a lightweight 4-submesh stencil vector
    // mesh and a 3-instruction shader (CardboardXR/HandSilhouette). Works identically in both
    // 6DoF (HandTracker) and 3DoF/Ultrawide (PinchSensor), strictly over normal windows (queue 3020)
    // and strictly UNDER the palm app window (queue 3050).
    public class HandJointsView : MonoBehaviour
    {
        static readonly int k_Color = Shader.PropertyToID("_Color");
        static readonly int k_Opacity = Shader.PropertyToID("_Opacity");
        static readonly int k_ColorMask = Shader.PropertyToID("_ColorMask");
        static readonly int k_StencilComp = Shader.PropertyToID("_StencilComp");
        static readonly int k_StencilPass = Shader.PropertyToID("_StencilPass");

        const int k_PalmTriCount = 6;
        const int k_BoneCount = 26;
        const int k_JointCircleCount = 22;
        const int k_CircleSegs = 10;
        const int k_PinchSegs = 16;

        const int k_VertsPerLayer = k_PalmTriCount * 3 + k_BoneCount * 4 + k_JointCircleCount * (1 + k_CircleSegs); // 364
        const int k_IndicesPerLayer = k_PalmTriCount * 3 + k_BoneCount * 6 + k_JointCircleCount * k_CircleSegs * 3; // 834
        public const int ContourVertexCount = k_VertsPerLayer * 3 + k_PinchSegs * 2; // 1124
        public const int ContourIndexCountPerLayer = k_IndicesPerLayer;

        static readonly (int a, int b)[] k_Bones =
        {
            // Thumb, Index, Middle, Ring, Pinky
            (1, 2), (2, 3), (3, 4),
            (5, 6), (6, 7), (7, 8),
            (9, 10), (10, 11), (11, 12),
            (13, 14), (14, 15), (15, 16),
            (17, 18), (18, 19), (19, 20),
            // Outer palm border capsules + interior bridges + forearm cuff
            (1, 5), (5, 9), (9, 13), (13, 17), (0, 17), (0, 1),
            (0, 9), (1, 13), (2, 9), (5, 17),
            (0, 21),
        };

        static readonly (int a, int b, int c)[] k_PalmTriangles =
        {
            (0, 1, 5), (0, 5, 9), (0, 9, 13), (0, 13, 17), (0, 1, 2), (1, 2, 5),
        };

        // Sleek adult human anatomical joint radii as fractions of projected palm scale.
        static readonly float[] k_JointRadiusScales =
        {
            0.50f, // 0: Wrist base
            0.175f, 0.132f, 0.105f, 0.084f, // 1..4: Thumb
            0.132f, 0.104f, 0.086f, 0.070f, // 5..8: Index
            0.135f, 0.106f, 0.088f, 0.072f, // 9..12: Middle
            0.126f, 0.098f, 0.082f, 0.067f, // 13..16: Ring
            0.112f, 0.088f, 0.074f, 0.060f, // 17..20: Pinky
            0.58f, // 21: Forearm cuff end
        };

        static readonly (float cos, float sin)[] k_CircleTrig = BuildTrig(k_CircleSegs);
        static readonly (float cos, float sin)[] k_PinchTrig = BuildTrig(k_PinchSegs);

        [SerializeField] HandTracker m_Tracker;
        [SerializeField] HandSubsystemFeed m_Feed;
        [SerializeField] PinchSensor m_Sensor;
        [SerializeField] HeadPoseFusion m_Fusion;
        [SerializeField] PassthroughController m_Passthrough;
        [SerializeField] Material m_Material;

        Mesh m_ContourMesh;
        Transform m_MeshTransform;
        MeshRenderer m_Renderer;
        Material m_MaskMaterial;
        Material m_CoreMaterial;
        Material m_HaloMaterial;
        Material m_PinchMaterial;

        readonly Vector3[] m_WorldJoints = new Vector3[HandGeometry.JointCount];
        readonly Vector3[] m_DisplayWorldJoints = new Vector3[HandGeometry.JointCount];
        readonly Vector2[] m_Predicted2D = new Vector2[HandGeometry.JointCount];
        readonly Vector2[] m_Local2D = new Vector2[22];
        readonly float[] m_Radii22 = new float[22];
        readonly Vector3[] m_Vertices = new Vector3[ContourVertexCount];
        readonly Vector2[] m_Uvs = new Vector2[ContourVertexCount];
        bool m_Shown;
        bool m_HasDisplayJoints;
        float m_SmoothedPalmScale;
        Vector2 m_SmoothedWristDir = Vector2.down;
        bool m_HasSmoothedContour;

        void Start()
        {
            var go = new GameObject("Hand 2D Vector Contour") { layer = StereoRig.FullscreenOverlayLayer };
            go.transform.SetParent(transform, false);
            m_MeshTransform = go.transform;
            var filter = go.AddComponent<MeshFilter>();
            m_Renderer = go.AddComponent<MeshRenderer>();
            m_Renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            m_Renderer.receiveShadows = false;

            m_ContourMesh = CreateContourMesh();
            filter.sharedMesh = m_ContourMesh;

            var shader = m_Material != null && m_Material.shader != null
                ? m_Material.shader
                : Shader.Find("CardboardXR/HandSilhouette");

            if (shader != null)
            {
                m_MaskMaterial = CreateLayerMaterial(shader, "Hand Contour Inner Mask",
                    new Color(1f, 1f, 1f, 1f), colorMask: 0f, stencilComp: 8f, stencilPass: 2f, queue: 3019);
                m_CoreMaterial = CreateLayerMaterial(shader, "Hand Contour White Core",
                    new Color(0.98f, 0.99f, 1.0f, 0.96f), colorMask: 15f, stencilComp: 6f, stencilPass: 2f, queue: 3020);
                m_HaloMaterial = CreateLayerMaterial(shader, "Hand Contour Dark Halo",
                    new Color(0.02f, 0.02f, 0.04f, 0.84f), colorMask: 15f, stencilComp: 6f, stencilPass: 2f, queue: 3021);
                m_PinchMaterial = CreateLayerMaterial(shader, "Hand Contour Pinch Ring",
                    new Color(0.98f, 0.99f, 1.0f, 0.96f), colorMask: 15f, stencilComp: 8f, stencilPass: 0f, queue: 3022);
                m_Renderer.sharedMaterials = new[] { m_MaskMaterial, m_CoreMaterial, m_HaloMaterial, m_PinchMaterial };
            }

            m_Renderer.sortingOrder = UiRenderQueue.k_HandContourSortingOrder;
            m_Renderer.enabled = false;
            m_Shown = false;
        }

        static Material CreateLayerMaterial(Shader shader, string matName, Color color,
            float colorMask, float stencilComp, float stencilPass, int queue)
        {
            var mat = new Material(shader) { name = matName, renderQueue = queue };
            mat.SetColor(k_Color, color);
            mat.SetFloat(k_Opacity, 1f);
            mat.SetFloat(k_ColorMask, colorMask);
            mat.SetFloat(k_StencilComp, stencilComp);
            mat.SetFloat(k_StencilPass, stencilPass);
            return mat;
        }

        void OnDestroy()
        {
            if (m_ContourMesh != null)
                Destroy(m_ContourMesh);
            if (m_MaskMaterial != null)
                Destroy(m_MaskMaterial);
            if (m_CoreMaterial != null)
                Destroy(m_CoreMaterial);
            if (m_HaloMaterial != null)
                Destroy(m_HaloMaterial);
            if (m_PinchMaterial != null)
                Destroy(m_PinchMaterial);
        }

        public static Vector3 SmoothDisplayPoint(Vector3 current, Vector3 target, float dt,
            float deadbandMeters = 0.0012f, float fastMeters = 0.0045f)
        {
            if (dt <= 0f)
                return target;
            var dist = Vector3.Distance(current, target);
            var blend = SmoothStep(deadbandMeters, fastMeters, dist);
            var rate = Mathf.Lerp(18f, 160f, blend);
            return Vector3.Lerp(current, target, 1f - Mathf.Exp(-rate * dt));
        }

        void LateUpdate()
        {
            var hasHand = TryGatherWorldJoints(out var pinching, out var pinchClose);
            if (hasHand != m_Shown)
            {
                m_Shown = hasHand;
                if (m_Renderer != null)
                    m_Renderer.enabled = hasHand;
            }
            if (!hasHand || m_ContourMesh == null || m_MeshTransform == null)
            {
                m_HasDisplayJoints = false;
                m_HasSmoothedContour = false;
                return;
            }

            var dt = Time.unscaledDeltaTime;
            if (!m_HasDisplayJoints)
            {
                for (var i = 0; i < HandGeometry.JointCount; i++)
                    m_DisplayWorldJoints[i] = m_WorldJoints[i];
                m_HasDisplayJoints = true;
            }
            else
            {
                for (var i = 0; i < HandGeometry.JointCount; i++)
                    m_DisplayWorldJoints[i] = SmoothDisplayPoint(m_DisplayWorldJoints[i], m_WorldJoints[i], dt);
            }

            var eyeTransform = m_Fusion != null ? m_Fusion.transform : (Camera.main != null ? Camera.main.transform : null);
            var eyePos = eyeTransform != null ? eyeTransform.position : Vector3.zero;
            var eyeUp = eyeTransform != null ? eyeTransform.up : Vector3.up;

            var handCenter = (m_DisplayWorldJoints[0] + m_DisplayWorldJoints[5] + m_DisplayWorldJoints[9] + m_DisplayWorldJoints[13] + m_DisplayWorldJoints[17]) * 0.2f;
            var toHand = handCenter - eyePos;
            var handDist = Mathf.Max(0.18f, toHand.magnitude);
            var forward = toHand / handDist;
            var right = Vector3.Cross(eyeUp, forward);
            if (right.sqrMagnitude < 1e-6f)
                right = Vector3.right;
            else
                right.Normalize();
            var up = Vector3.Cross(forward, right).normalized;
            var planeRot = Quaternion.LookRotation(forward, up);

            // Perspective-project all 21 smoothed landmarks onto the viewer-facing plane at handDist.
            for (var i = 0; i < HandGeometry.JointCount; i++)
            {
                var rel = m_DisplayWorldJoints[i] - eyePos;
                var z = Mathf.Max(0.08f, Vector3.Dot(rel, forward));
                var scale = handDist / z;
                m_Local2D[i] = new Vector2(Vector3.Dot(rel, right) * scale, Vector3.Dot(rel, up) * scale);
            }

            // Compute stable projected palm scale (clamped to anatomical adult bounds).
            var palmSpanSum = 0f;
            var palmPairs = 0;
            for (var a = 0; a < HandGeometry.Palm.Length; a++)
            {
                for (var b = a + 1; b < HandGeometry.Palm.Length; b++)
                {
                    palmSpanSum += Vector2.Distance(m_Local2D[HandGeometry.Palm[a]], m_Local2D[HandGeometry.Palm[b]]);
                    palmPairs++;
                }
            }
            var rawPalmScale = Mathf.Clamp(palmSpanSum / Mathf.Max(1, palmPairs), 0.042f, 0.115f);

            // Anatomical wrist/forearm cuff extending outward from landmark 0 away from the knuckles.
            var midKnuckles = (m_Local2D[5] + m_Local2D[9] + m_Local2D[13] + m_Local2D[17]) * 0.25f;
            var rawWristDir = m_Local2D[0] - midKnuckles;
            if (rawWristDir.sqrMagnitude < 1e-6f)
                rawWristDir = Vector2.down;
            else
                rawWristDir.Normalize();

            if (!m_HasSmoothedContour)
            {
                m_SmoothedPalmScale = rawPalmScale;
                m_SmoothedWristDir = rawWristDir;
                m_HasSmoothedContour = true;
            }
            else
            {
                m_SmoothedPalmScale = Mathf.Lerp(m_SmoothedPalmScale, rawPalmScale, 1f - Mathf.Exp(-30f * dt));
                m_SmoothedWristDir = Vector2.Lerp(m_SmoothedWristDir, rawWristDir, 1f - Mathf.Exp(-35f * dt));
                if (m_SmoothedWristDir.sqrMagnitude < 1e-6f)
                    m_SmoothedWristDir = Vector2.down;
                else
                    m_SmoothedWristDir.Normalize();
            }

            var palmScale = m_SmoothedPalmScale;
            var wristDir = m_SmoothedWristDir;
            var wristLen = palmScale * 1.25f;
            m_Local2D[21] = m_Local2D[0] + wristDir * wristLen;

            for (var i = 0; i < 22; i++)
                m_Radii22[i] = palmScale * k_JointRadiusScales[i];

            var distFactor = Mathf.Clamp(handDist / 0.42f, 0.75f, 1.55f);
            var strokeW = 0.0019f * distFactor;
            var haloW = 0.0024f * distFactor;
            var pinchMid = (m_Local2D[HandGeometry.ThumbTip] + m_Local2D[HandGeometry.IndexTip]) * 0.5f;

            FillContourGeometry(m_Local2D, m_Radii22, wristDir, wristLen, strokeW, haloW,
                pinchMid, pinchClose, pinching, palmScale, m_Vertices, m_Uvs);

            m_MeshTransform.SetPositionAndRotation(eyePos + forward * handDist, planeRot);
            m_MeshTransform.localScale = Vector3.one;

            const UnityEngine.Rendering.MeshUpdateFlags flags =
                UnityEngine.Rendering.MeshUpdateFlags.DontValidateIndices |
                UnityEngine.Rendering.MeshUpdateFlags.DontRecalculateBounds;
            m_ContourMesh.SetVertices(m_Vertices, 0, ContourVertexCount, flags);
            m_ContourMesh.SetUVs(0, m_Uvs, 0, ContourVertexCount, flags);

            var opacity = (m_Passthrough != null && m_Passthrough.IsOn && !EnvironmentManager.IsIsolated) ? 0.92f : 1.0f;
            if (m_CoreMaterial != null)
                m_CoreMaterial.SetFloat(k_Opacity, opacity);
            if (m_HaloMaterial != null)
                m_HaloMaterial.SetFloat(k_Opacity, opacity);
            if (m_PinchMaterial != null)
            {
                var ringVis = pinchClose > 0.45f
                    ? SmoothStep(0.45f, 0.78f, pinchClose) * (0.55f + 0.45f * (pinching ? 1f : 0f))
                    : 0f;
                m_PinchMaterial.SetFloat(k_Opacity, opacity * ringVis);
            }
        }

        // Builds static topology once and populates vertices/UVs into pre-allocated arrays (zero GC).
        public static Mesh CreateContourMesh()
        {
            var mesh = new Mesh { name = "Hand Stencil Vector Contour" };
            mesh.MarkDynamic();
            mesh.SetVertices(new Vector3[ContourVertexCount]);
            mesh.SetUVs(0, new Vector2[ContourVertexCount]);
            mesh.subMeshCount = 4;

            for (var layer = 0; layer < 3; layer++)
                mesh.SetTriangles(BuildLayerIndices(layer * k_VertsPerLayer), layer, false);
            mesh.SetTriangles(BuildPinchRingIndices(k_VertsPerLayer * 3), 3, false);
            mesh.bounds = new Bounds(Vector3.zero, Vector3.one * 1.5f);
            return mesh;
        }

        public static void FillContourGeometry(IReadOnlyList<Vector2> joints22, IReadOnlyList<float> radii22,
            Vector2 wristDir, float wristLen, float strokeW, float haloW, Vector2 pinchMid,
            float pinchClose, bool pinching, float palmScale, Vector3[] vertices, Vector2[] uvs)
        {
            if (joints22 == null || radii22 == null || vertices == null || uvs == null)
                return;

            var j0 = joints22[0];
            var invWristLen = 1f / Mathf.Max(wristLen, 0.01f);

            float EvalFade(Vector2 p)
            {
                var proj = Vector2.Dot(p - j0, wristDir) * invWristLen;
                return 1f - SmoothStep(0.45f, 0.94f, proj);
            }

            var v = 0;
            for (var layer = 0; layer < 3; layer++)
            {
                var expand = layer == 0 ? 0f : (layer == 1 ? strokeW : strokeW + haloW);

                // 6 watertight solid palm triangles
                for (var t = 0; t < k_PalmTriCount; t++)
                {
                    var (ia, ib, ic) = k_PalmTriangles[t];
                    WriteVertex(vertices, uvs, v++, joints22[ia], EvalFade(joints22[ia]));
                    WriteVertex(vertices, uvs, v++, joints22[ib], EvalFade(joints22[ib]));
                    WriteVertex(vertices, uvs, v++, joints22[ic], EvalFade(joints22[ic]));
                }

                // 26 exact-tangent tapered bone quads (fingers + palm perimeter + interior bridges + wrist cuff)
                for (var b = 0; b < k_Bones.Length; b++)
                {
                    var (ia, ib) = k_Bones[b];
                    var a = joints22[ia];
                    var c = joints22[ib];
                    var ra = Mathf.Max(0.0005f, radii22[ia] + expand);
                    var rb = Mathf.Max(0.0005f, radii22[ib] + expand);
                    var ab = c - a;
                    var d = ab.magnitude;
                    if (d <= Mathf.Abs(ra - rb) + 1e-5f)
                    {
                        var fa = EvalFade(a);
                        WriteVertex(vertices, uvs, v++, a, fa);
                        WriteVertex(vertices, uvs, v++, a, fa);
                        WriteVertex(vertices, uvs, v++, a, fa);
                        WriteVertex(vertices, uvs, v++, a, fa);
                        continue;
                    }
                    var dir = ab / d;
                    var perp = new Vector2(-dir.y, dir.x);
                    var sinBeta = Mathf.Clamp((ra - rb) / d, -0.999f, 0.999f);
                    var cosBeta = Mathf.Sqrt(1f - sinBeta * sinBeta);
                    var nL = perp * cosBeta + dir * sinBeta;
                    var nR = -perp * cosBeta + dir * sinBeta;

                    var p0 = a + nL * ra;
                    var p1 = a + nR * ra;
                    var p2 = c + nR * rb;
                    var p3 = c + nL * rb;
                    WriteVertex(vertices, uvs, v++, p0, EvalFade(p0));
                    WriteVertex(vertices, uvs, v++, p1, EvalFade(p1));
                    WriteVertex(vertices, uvs, v++, p2, EvalFade(p2));
                    WriteVertex(vertices, uvs, v++, p3, EvalFade(p3));
                }

                // 22 10-segment joint circles rounding every knuckle, fingertip, wrist base, and cuff
                for (var j = 0; j < k_JointCircleCount; j++)
                {
                    var center = joints22[j];
                    var r = Mathf.Max(0.0005f, radii22[j] + expand);
                    WriteVertex(vertices, uvs, v++, center, EvalFade(center));
                    for (var s = 0; s < k_CircleSegs; s++)
                    {
                        var (cos, sin) = k_CircleTrig[s];
                        var pt = center + new Vector2(cos * r, sin * r);
                        WriteVertex(vertices, uvs, v++, pt, EvalFade(pt));
                    }
                }
            }

            // Submesh 3: lightweight surgical pinch micro-ring between thumb & index fingertips
            if (pinchClose > 0.45f)
            {
                var safePalm = Mathf.Max(palmScale, 0.05f);
                var radius = Mathf.Lerp(safePalm * 0.082f, safePalm * 0.036f, Mathf.Clamp01(pinchClose));
                var ringHalf = Mathf.Max(0.0004f, strokeW * 0.45f);
                var rIn = Mathf.Max(0.0002f, radius - ringHalf);
                var rOut = radius + ringHalf;
                var ringFade = pinching ? 1f : 0.92f;
                for (var s = 0; s < k_PinchSegs; s++)
                {
                    var (cos, sin) = k_PinchTrig[s];
                    var radial = new Vector2(cos, sin);
                    WriteVertex(vertices, uvs, v++, pinchMid + radial * rIn, ringFade);
                    WriteVertex(vertices, uvs, v++, pinchMid + radial * rOut, ringFade);
                }
            }
            else
            {
                for (var s = 0; s < k_PinchSegs * 2; s++)
                    WriteVertex(vertices, uvs, v++, pinchMid, 0f);
            }
        }

        static void WriteVertex(Vector3[] vertices, Vector2[] uvs, int index, Vector2 pos, float fade)
        {
            vertices[index] = new Vector3(pos.x, pos.y, 0f);
            uvs[index] = new Vector2(fade, 0f);
        }

        static int[] BuildLayerIndices(int baseVertex)
        {
            var indices = new int[k_IndicesPerLayer];
            var idx = 0;
            var v = baseVertex;

            for (var t = 0; t < k_PalmTriCount; t++)
            {
                indices[idx++] = v++;
                indices[idx++] = v++;
                indices[idx++] = v++;
            }

            for (var b = 0; b < k_Bones.Length; b++)
            {
                var v0 = v;
                indices[idx++] = v0;
                indices[idx++] = v0 + 1;
                indices[idx++] = v0 + 2;
                indices[idx++] = v0;
                indices[idx++] = v0 + 2;
                indices[idx++] = v0 + 3;
                v += 4;
            }

            for (var j = 0; j < k_JointCircleCount; j++)
            {
                var center = v++;
                for (var s = 0; s < k_CircleSegs; s++)
                {
                    var curr = v + s;
                    var next = v + ((s + 1) % k_CircleSegs);
                    indices[idx++] = center;
                    indices[idx++] = curr;
                    indices[idx++] = next;
                }
                v += k_CircleSegs;
            }

            return indices;
        }

        static int[] BuildPinchRingIndices(int baseVertex)
        {
            var indices = new int[k_PinchSegs * 6];
            var idx = 0;
            for (var s = 0; s < k_PinchSegs; s++)
            {
                var i0 = baseVertex + s * 2;
                var o0 = i0 + 1;
                var nextS = (s + 1) % k_PinchSegs;
                var i1 = baseVertex + nextS * 2;
                var o1 = i1 + 1;

                indices[idx++] = i0;
                indices[idx++] = o0;
                indices[idx++] = o1;
                indices[idx++] = i0;
                indices[idx++] = o1;
                indices[idx++] = i1;
            }
            return indices;
        }

        static (float cos, float sin)[] BuildTrig(int segments)
        {
            var arr = new (float cos, float sin)[segments];
            var step = (Mathf.PI * 2f) / segments;
            for (var i = 0; i < segments; i++)
            {
                var a = i * step;
                arr[i] = (Mathf.Cos(a), Mathf.Sin(a));
            }
            return arr;
        }

        static float SmoothStep(float edge0, float edge1, float x)
        {
            var t = Mathf.Clamp01((x - edge0) / Mathf.Max(edge1 - edge0, 1e-5f));
            return t * t * (3f - 2f * t);
        }

        bool TryGatherWorldJoints(out bool pinching, out float pinchClose)
        {
            pinching = false;
            pinchClose = 0f;

            if (m_Tracker != null && m_Tracker.isActiveAndEnabled && m_Tracker.IsTracking)
            {
                var joints = m_Tracker.Joints;
                for (var i = 0; i < HandGeometry.JointCount; i++)
                    m_WorldJoints[i] = joints[i];
                pinching = (m_Feed != null && m_Feed.IsPinching) || VisualPinchScroller.IsPinchHeld;
                pinchClose = Mathf.InverseLerp(0.080f, 0.042f, m_Tracker.PinchDistance);
                return true;
            }

            if (m_Sensor != null && m_Sensor.isActiveAndEnabled && m_Sensor.SeesHand && m_Fusion != null)
            {
                var imgSize = m_Sensor.ImageSize;
                if (imgSize.x <= 1f || imgSize.y <= 1f || !m_Sensor.FillPredictedImageLandmarks(m_Predicted2D))
                    return false;

                var head = m_Fusion.transform;
                var dist = Mathf.Clamp(m_Sensor.EstimatedHandDistance, 0.24f, 0.78f);
                var hFov = m_Sensor.HorizontalFovDeg;
                var vFov = m_Sensor.VerticalFovDeg;
                for (var i = 0; i < HandGeometry.JointCount; i++)
                {
                    var frac = new Vector2(m_Predicted2D[i].x / imgSize.x, m_Predicted2D[i].y / imgSize.y);
                    var ray = PalmGesture.Ray(frac, hFov, vFov);
                    m_WorldJoints[i] = head.position + head.rotation * ray * dist;
                }
                pinching = m_Sensor.IsPinched || VisualPinchScroller.IsPinchHeld;
                pinchClose = Mathf.InverseLerp(1.0f, PinchSensor.k_CloseRatio, m_Sensor.CurrentPinchMetric);
                return true;
            }

            return false;
        }
    }
}
