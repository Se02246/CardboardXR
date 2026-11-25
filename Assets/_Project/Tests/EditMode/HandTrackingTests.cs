using System.Linq;
using System.Reflection;
using CardboardXR.Hands;
using CardboardXR.Interaction;
using CardboardXR.UI;
using NUnit.Framework;
using Unity.Collections;
using UnityEngine;
using UnityEngine.UI;

namespace CardboardXR.Stereo.Tests
{
    public class HandTrackingTests
    {
        [Test]
        public void Anchors_FollowMediaPipesGrids()
        {
            var anchors = PalmDecoder.CreateAnchors();

            Assert.AreEqual(2016, anchors.Length);
            AssertClose(new Vector2(0.5f / 24f, 0.5f / 24f), anchors[0]);
            AssertClose(anchors[0], anchors[1], "two anchors per cell in the fine grid");
            AssertClose(new Vector2(1.5f / 24f, 0.5f / 24f), anchors[2]);
            AssertClose(new Vector2(0.5f / 12f, 0.5f / 12f), anchors[1152], "the coarse grid follows");
            AssertClose(new Vector2(11.5f / 12f, 11.5f / 12f), anchors[2015]);
        }

        [Test]
        public void BestPalm_IsDecodedFromItsAnchor()
        {
            var anchors = PalmDecoder.CreateAnchors();
            var boxes = new float[PalmDecoder.AnchorCount * PalmDecoder.ValuesPerAnchor];
            var scores = Enumerable.Repeat(-10f, PalmDecoder.AnchorCount).ToArray();
            const int best = 1500;
            scores[best] = 3f;
            var o = best * PalmDecoder.ValuesPerAnchor;
            // In input pixels: 192 is the whole input.
            boxes[o] = 9.6f;
            boxes[o + 1] = -19.2f;
            boxes[o + 2] = 38.4f;
            boxes[o + 3] = 19.2f;
            boxes[o + 5] = 19.2f;
            boxes[o + 9] = -19.2f;

            Assert.IsTrue(PalmDecoder.TryDecodeBest(boxes, scores, anchors, 0.5f, out var palm));
            Assert.AreEqual(PalmDecoder.Sigmoid(3f), palm.score, 1e-5f);
            AssertClose(anchors[best] + new Vector2(0.05f, -0.1f), palm.center);
            AssertClose(new Vector2(0.2f, 0.1f), palm.size);
            AssertClose(anchors[best] + new Vector2(0f, 0.1f), palm.wrist);
            AssertClose(anchors[best] + new Vector2(0f, -0.1f), palm.middleKnuckle);

            scores[best] = -1f;
            Assert.IsFalse(PalmDecoder.TryDecodeBest(boxes, scores, anchors, 0.5f, out _));
        }

        [Test]
        public void Rotation_TurnsTheFingersUp()
        {
            Assert.AreEqual(0f, HandRegion.RotationFrom(new Vector2(100f, 200f), new Vector2(100f, 100f)), 1e-5f, "fingers up");
            Assert.AreEqual(Mathf.PI / 2f, HandRegion.RotationFrom(new Vector2(100f, 100f), new Vector2(200f, 100f)), 1e-5f, "fingers right");

            var region = new HandRegion { center = new Vector2(100f, 100f), size = 50f, rotation = Mathf.PI / 2f };
            AssertClose(new Vector2(125f, 100f), region.CropToImage(new Vector2(0.5f, 0f)), "the crop's top is toward the fingers");
            var point = new Vector2(117f, 83f);
            AssertClose(point, region.CropToImage(region.ImageToCrop(point)), "round trip", 1e-3f);
        }

        [Test]
        public void PalmRegion_CoversTheHandAboveThePalm()
        {
            // A 640x360 image fits the 192 input at 0.3 scale, 42 pixels down.
            var letterbox = Letterbox.Fit(640, 360, PalmDecoder.InputSize);
            AssertClose(new Vector2(320f, 180f), letterbox.ToImage(new Vector2(0.5f, 0.5f)));

            var palm = new Palm
            {
                score = 0.9f,
                center = new Vector2(0.5f, 0.5f),
                size = new Vector2(0.1f, 0.1f),
                wrist = new Vector2(0.5f, 0.55f),
                middleKnuckle = new Vector2(0.5f, 0.45f),
            };
            var region = HandRegion.FromPalm(palm, letterbox);

            Assert.AreEqual(0f, region.rotation, 1e-5f);
            // A tenth of the input is 64 image pixels: moved half of that up, 2.6 times as big.
            AssertClose(new Vector2(320f, 148f), region.center, "", 1e-3f);
            Assert.AreEqual(64f * 2.6f, region.size, 1e-3f);
        }

        [Test]
        public void LandmarkRegion_BoundsThePalmAndFingerBases()
        {
            var landmarks = Enumerable.Repeat(new Vector2(100f, 150f), 21).ToArray();
            landmarks[0] = new Vector2(100f, 200f);
            landmarks[1] = new Vector2(80f, 190f);
            landmarks[2] = new Vector2(80f, 170f);
            landmarks[3] = new Vector2(80f, 160f);
            landmarks[5] = new Vector2(90f, 110f);
            landmarks[6] = new Vector2(90f, 100f);
            landmarks[9] = new Vector2(100f, 110f);
            landmarks[10] = new Vector2(100f, 100f);
            landmarks[13] = new Vector2(110f, 110f);
            landmarks[14] = new Vector2(110f, 100f);
            landmarks[17] = new Vector2(120f, 120f);
            landmarks[18] = new Vector2(120f, 110f);

            var region = HandRegion.FromLandmarks(landmarks);

            Assert.AreEqual(0f, region.rotation, 1e-5f);
            // Bounds 40 x 100 around (100, 150): moved up a tenth of the height, twice the long side.
            AssertClose(new Vector2(100f, 140f), region.center, "", 1e-3f);
            Assert.AreEqual(200f, region.size, 1e-3f);
        }

        [Test]
        public void RegionSampling_ReadsTheTurnedCrop()
        {
            const int size = 8;
            var pixels = new byte[size * size * 4];
            for (var y = 0; y < size; y++)
            {
                for (var x = 0; x < size; x++)
                {
                    var p = (y * size + x) * 4;
                    pixels[p] = (byte)(x * 30);
                    pixels[p + 1] = (byte)(y * 30);
                    pixels[p + 3] = 255;
                }
            }
            using var image = new NativeArray<byte>(pixels, Allocator.Temp);
            var output = new float[size * size * 3];

            var upright = new HandRegion { center = new Vector2(4f, 4f), size = size, rotation = 0f };
            ImageSampler.SampleRegion(image, size, size, upright, size, output);
            AssertPixel(output, size, 3, 2, 3, 2, "crop pixel (3, 2) is image pixel (3, 2)");

            var turned = upright;
            turned.rotation = Mathf.PI / 2f;
            ImageSampler.SampleRegion(image, size, size, turned, size, output);
            AssertPixel(output, size, 3, 2, 5, 3, "turned a quarter, crop pixel (x, y) is image pixel (7 - y, x)");
        }

        [Test]
        public void LetterboxSampling_PadsAboveAndBelow()
        {
            var pixels = new byte[4 * 2 * 4];
            for (var i = 0; i < 8; i++)
                pixels[i * 4] = 200;
            using var image = new NativeArray<byte>(pixels, Allocator.Temp);
            var output = new float[4 * 4 * 3];

            ImageSampler.SampleLetterbox(image, 4, 2, Letterbox.Fit(4, 2, 4), output);

            Assert.AreEqual(0f, output[0], 1e-5f, "the top row is padding");
            Assert.AreEqual(200f / 255f, output[(1 * 4 + 1) * 3], 1e-4f, "the image's first row");
            Assert.AreEqual(0f, output[(3 * 4 + 1) * 3], 1e-5f, "the bottom row is padding");
        }

        [Test]
        public void HandPosition_IsFitFromItsShapeHoweverItsTurned([Values(0f, 80f)] float turnDegrees)
        {
            // A made-up hand shape, turned about the vertical (80 degrees is nearly edge-on).
            const float focal = 500f;
            var principal = new Vector2(320f, 180f);
            var position = new Vector3(0.05f, -0.03f, 0.4f);
            var turn = Quaternion.Euler(0f, turnDegrees, 0f);
            var metric = new Vector3[21];
            for (var i = 0; i < 21; i++)
                metric[i] = turn * new Vector3((i % 5 - 2) * 0.02f, (i / 5 - 2) * 0.02f, (i % 3 - 1) * 0.01f);
            var truth = metric.Select(m => m + position).ToArray();
            var image = truth.Select(t => new Vector2(t.x, t.y) * focal / t.z + principal).ToArray();

            var fit = HandGeometry.FitPosition(image, metric, HandGeometry.All, new Vector2(focal, focal), principal, out var error);
            Assert.That((fit - position).magnitude, Is.LessThan(1e-4f), $"fit {fit}");
            fit = HandGeometry.FitPosition(image, metric, HandGeometry.Palm, new Vector2(focal, focal), principal, out error);
            Assert.That((fit - position).magnitude, Is.LessThan(1e-4f), $"fit {fit}");
            Assert.That(error, Is.LessThan(0.01f));

            var joints = new Vector3[21];
            HandGeometry.PlaceJoints(image, metric, fit.z, new Vector2(focal, focal), principal, joints);
            for (var i = 0; i < 21; i++)
                Assert.That((joints[i] - truth[i]).magnitude, Is.LessThan(1e-3f), $"joint {i}");
        }

        [Test]
        public void OneEuroFilter_HoldsStillValuesAndFollowsFastOnes()
        {
            // 15 samples a second, as the hand tracker gives them.
            const double step = 1.0 / 15.0;
            var still = new OneEuroFilter(1f, 4f);
            still.Filter(Vector3.zero, 0.0);
            var afterJump = still.Filter(new Vector3(0.05f, 0f, 0f), step);
            Assert.That(afterJump.x, Is.GreaterThan(0f).And.LessThan(0.03f), "a sudden 5 cm jump is mostly held back");

            // A hand moving steadily at 1 m/s: with beta the filter lags far less than without.
            var fast = new OneEuroFilter(1f, 4f);
            var plain = new OneEuroFilter(1f, 0f);
            Vector3 fastValue = default, plainValue = default;
            for (var i = 0; i <= 15; i++)
            {
                var value = new Vector3((float)(i * step), 0f, 0f);
                fastValue = fast.Filter(value, i * step);
                plainValue = plain.Filter(value, i * step);
            }
            Assert.That(1f - fastValue.x, Is.LessThan((1f - plainValue.x) * 0.5f));

            still.Reset();
            Assert.AreEqual(0.3f, still.Filter(new Vector3(0.3f, 0f, 0f), 10.0).x, 1e-6f, "after a reset the next value is taken as it is");
        }

        [Test]
        public void Pinch_StartsCloserThanItEnds()
        {
            var pinch = new PinchDetector(0.025f, 0.045f);

            Assert.IsFalse(pinch.Update(0.06f));
            Assert.IsFalse(pinch.Update(0.03f), "between the thresholds an open hand stays open");
            Assert.IsTrue(pinch.Update(0.02f));
            Assert.IsTrue(pinch.Update(0.04f), "between the thresholds a pinch holds");
            Assert.IsFalse(pinch.Update(0.05f));

            pinch.Update(0.01f);
            pinch.Reset();
            Assert.IsFalse(pinch.IsPinched);
        }

        [Test]
        public void PalmSize_StaysTheSameAsTheHandTurns()
        {
            var shape = new Vector3[21];
            for (var i = 0; i < 21; i++)
                shape[i] = new Vector3((i % 5 - 2) * 0.02f, (i / 5 - 2) * 0.02f, (i % 3 - 1) * 0.01f);
            var turned = shape.Select(p => Quaternion.Euler(30f, 70f, 10f) * p).ToArray();

            Assert.AreEqual(HandGeometry.PalmSize(shape), HandGeometry.PalmSize(turned), 1e-5f);
        }

        [Test]
        public void ImageAxes_FollowTheDisplayMatrix()
        {
            // Texture UV is screen UV with v flipped: the image's rows run down the screen.
            var flipped = Matrix4x4.identity;
            flipped.m11 = -1f;
            flipped.m21 = 1f;
            HandGeometry.ImageAxesOnScreen(flipped, out var imageX, out var imageY);
            AssertClose(Vector2.right, imageX);
            AssertClose(Vector2.down, imageY);

            // Texture u runs up the screen and v across it.
            var turned = Matrix4x4.identity;
            turned.m00 = 0f;
            turned.m01 = 1f;
            turned.m10 = 1f;
            turned.m11 = 0f;
            HandGeometry.ImageAxesOnScreen(turned, out imageX, out imageY);
            AssertClose(Vector2.up, imageX);
            AssertClose(Vector2.right, imageY);
        }

        [Test]
        public void ImageAxesVariants_AreEightDistinctRightAngledPairs()
        {
            var pairs = Enumerable.Range(1, 8).Select(v =>
            {
                HandGeometry.ImageAxesVariant(v, out var x, out var y);
                Assert.AreEqual(0f, Vector2.Dot(x, y), 1e-5f, $"variant {v}");
                return (x, y);
            }).ToArray();
            Assert.AreEqual(8, pairs.Distinct().Count());
        }

        static void AssertPixel(float[] output, int size, int x, int y, int imageX, int imageY, string message)
        {
            var i = (y * size + x) * 3;
            Assert.AreEqual(imageX * 30 / 255f, output[i], 1e-4f, message);
            Assert.AreEqual(imageY * 30 / 255f, output[i + 1], 1e-4f, message);
        }

        static void AssertClose(Vector2 expected, Vector2 actual, string message = "", float tolerance = 1e-5f) =>
            Assert.That((actual - expected).magnitude, Is.LessThan(tolerance), $"{message} expected {expected}, got {actual}");

        static (Vector2[] img, Vector3[] metric) MakeRealisticHand(float palmSpanPx = 90f, float centerX = 480f, float centerY = 300f)
        {
            var img = new Vector2[HandGeometry.JointCount];
            var metric = new Vector3[HandGeometry.JointCount];
            // Realistic adult 3D scale (palm ~5.5cm)
            metric[0] = new Vector3(0f, -0.055f, 0f);
            metric[5] = new Vector3(-0.032f, 0.015f, 0f);
            metric[9] = new Vector3(-0.008f, 0.018f, 0f);
            metric[13] = new Vector3(0.014f, 0.015f, 0f);
            metric[17] = new Vector3(0.034f, 0.006f, 0f);

            metric[1] = new Vector3(-0.028f, -0.032f, -0.006f);
            metric[2] = new Vector3(-0.046f, -0.010f, -0.010f);
            metric[3] = new Vector3(-0.060f, 0.014f, -0.012f);
            metric[4] = new Vector3(-0.070f, 0.036f, -0.012f);

            metric[6] = new Vector3(-0.034f, 0.052f, 0f);
            metric[7] = new Vector3(-0.035f, 0.078f, 0f);
            metric[8] = new Vector3(-0.036f, 0.098f, 0f);

            metric[10] = new Vector3(-0.008f, 0.060f, 0f);
            metric[11] = new Vector3(-0.008f, 0.088f, 0f);
            metric[12] = new Vector3(-0.008f, 0.110f, 0f);

            metric[14] = new Vector3(0.015f, 0.054f, 0f);
            metric[15] = new Vector3(0.016f, 0.080f, 0f);
            metric[16] = new Vector3(0.017f, 0.100f, 0f);

            metric[18] = new Vector3(0.036f, 0.038f, 0f);
            metric[19] = new Vector3(0.037f, 0.058f, 0f);
            metric[20] = new Vector3(0.038f, 0.074f, 0f);

            var palmSize = HandGeometry.PalmSize(metric);
            var pxPerMeter = palmSpanPx / palmSize;
            for (var i = 0; i < HandGeometry.JointCount; i++)
                img[i] = new Vector2(centerX + metric[i].x * pxPerMeter, centerY - metric[i].y * pxPerMeter);
            return (img, metric);
        }

        [Test]
        public void NearestUserPalm_PrefersCloserHandAndRejectsTinyOrUpsideDown()
        {
            var anchors = PalmDecoder.CreateAnchors();
            var boxes = new float[PalmDecoder.AnchorCount * PalmDecoder.ValuesPerAnchor];
            var scores = Enumerable.Repeat(-10f, PalmDecoder.AnchorCount).ToArray();

            const int tinyAnchor = 400;
            scores[tinyAnchor] = 4f;
            var to = tinyAnchor * PalmDecoder.ValuesPerAnchor;
            boxes[to + 2] = 0.03f * PalmDecoder.InputSize;
            boxes[to + 3] = 0.03f * PalmDecoder.InputSize;
            Assert.IsFalse(PalmDecoder.TryDecodeNearestUserPalm(boxes, scores, anchors, 0.62f, 0.052f, out _),
                "tiny distant detections must be rejected");

            const int upsideDownAnchor = 150;
            scores[upsideDownAnchor] = 4f;
            var uo = upsideDownAnchor * PalmDecoder.ValuesPerAnchor;
            boxes[uo + 2] = 0.14f * PalmDecoder.InputSize;
            boxes[uo + 3] = 0.14f * PalmDecoder.InputSize;
            boxes[uo + 5] = -10f; // wrist higher on screen than middle knuckle in upper half
            boxes[uo + 9] = 10f;
            Assert.IsFalse(PalmDecoder.TryDecodeNearestUserPalm(boxes, scores, anchors, 0.62f, 0.052f, out _),
                "upside-down hands in upper background must be rejected");

            const int distantValid = 800;
            scores[distantValid] = 4f;
            var dO = distantValid * PalmDecoder.ValuesPerAnchor;
            boxes[dO + 2] = 0.06f * PalmDecoder.InputSize;
            boxes[dO + 3] = 0.06f * PalmDecoder.InputSize;
            boxes[dO + 5] = 10f;
            boxes[dO + 9] = -10f;

            const int closeUser = 1600;
            scores[closeUser] = 1.5f;
            var cO = closeUser * PalmDecoder.ValuesPerAnchor;
            boxes[cO + 2] = 0.24f * PalmDecoder.InputSize;
            boxes[cO + 3] = 0.24f * PalmDecoder.InputSize;
            boxes[cO + 5] = 20f;
            boxes[cO + 9] = -20f;

            Assert.IsTrue(PalmDecoder.TryDecodeNearestUserPalm(boxes, scores, anchors, 0.62f, 0.052f, out var best));
            AssertClose(anchors[closeUser], best.center, "user's close hand wins priority", 1e-3f);
        }

        [Test]
        public void HandPlausibility_AcceptsRealProportionsAndRejectsCollapsedNoise()
        {
            var (img, metric) = MakeRealisticHand();
            Assert.IsTrue(HandGeometry.IsPlausibleHand(img, metric, 960f, 540f));

            var collapsedMetric = (Vector3[])metric.Clone();
            for (var i = 1; i < HandGeometry.JointCount; i++)
            {
                if (i != 5 && i != 9 && i != 13 && i != 17)
                    collapsedMetric[i] = Vector3.zero;
            }
            Assert.IsFalse(HandGeometry.IsPlausibleHand(img, collapsedMetric, 960f, 540f), "collapsed noise fails anatomy check");

            var (tinyImg, _) = MakeRealisticHand(palmSpanPx: 8f);
            Assert.IsFalse(HandGeometry.IsPlausibleHand(tinyImg, metric, 960f, 540f), "tiny clutter box fails 2D span check");
        }

        [Test]
        public void OwnHandFilter_RejectsDistantOrUpperUpsideDownHands()
        {
            var (img, _) = MakeRealisticHand(palmSpanPx: 85f, centerX: 480f, centerY: 320f);
            Assert.IsTrue(HandGeometry.IsLikelyOwnHand(img, 0.45f, 540f, acquiring: true));
            Assert.IsFalse(HandGeometry.IsLikelyOwnHand(img, 0.80f, 540f, acquiring: true), "beyond acquiring reach");

            var (upperImg, _) = MakeRealisticHand(palmSpanPx: 65f, centerX: 480f, centerY: 110f);
            Assert.IsFalse(HandGeometry.IsLikelyOwnHand(upperImg, 0.60f, 540f, acquiring: true), "distant background person in upper view");

            var flipped = (Vector2[])img.Clone();
            (flipped[0], flipped[9]) = (new Vector2(480f, 180f), new Vector2(480f, 260f));
            Assert.IsFalse(HandGeometry.IsLikelyOwnHand(flipped, 0.55f, 540f, acquiring: true), "upside-down distant hand");
        }

        [Test]
        public void PinchMetric_RejectsDepthSeparatedOverlap()
        {
            var (img, metric) = MakeRealisticHand();
            // Fingertips appear right on top of each other in 2D, but are 5 cm apart in 3D depth.
            img[HandGeometry.IndexTip] = img[HandGeometry.ThumbTip] + new Vector2(2f, 0f);
            metric[HandGeometry.IndexTip] = metric[HandGeometry.ThumbTip] + new Vector3(0f, 0f, 0.050f);
            var depthSeparated = PinchSensor.ComputePinchMetric(img, metric);
            Assert.GreaterOrEqual(depthSeparated, PinchSensor.k_CloseRatio, "depth-separated fingers must not count as pinched");

            // Truly touching in both 2D and 3D.
            metric[HandGeometry.IndexTip] = metric[HandGeometry.ThumbTip] + new Vector3(0.004f, 0f, 0.002f);
            var touching = PinchSensor.ComputePinchMetric(img, metric);
            Assert.Less(touching, PinchSensor.k_CloseRatio);
        }

        sealed class TestVisualScrollTarget : MonoBehaviour, IVisualScrollTarget
        {
            public RectTransform Rect;
            public int Begins;
            public int Moves;
            public int Ends;
            public Vector2 LastLocal;
            public Vector2 LastDelta;
            public bool LastCancelled;

            public RectTransform ScrollRectTransform => Rect;
            public bool CanVisualScroll => Rect != null;
            public void OnVisualTouchBegin(Vector2 localPoint)
            {
                Begins++;
                LastLocal = localPoint;
            }
            public void OnVisualTouchMove(Vector2 localPoint, Vector2 localDelta)
            {
                Moves++;
                LastLocal = localPoint;
                LastDelta = localDelta;
            }
            public void OnVisualTouchEnd(Vector2 localPoint, bool cancelled)
            {
                Ends++;
                LastLocal = localPoint;
                LastCancelled = cancelled;
            }
        }

        [Test]
        public void VisualPinchScroller_GazeOnSurfaceStartsDragThatFollowsTheHandAnywhere()
        {
            VisualPinchScroller.Reset();
            var headGo = new GameObject("Head");
            var canvasGo = new GameObject("Scroll Canvas", typeof(RectTransform), typeof(Canvas), typeof(Image), typeof(GazeCanvas), typeof(TestVisualScrollTarget));
            var outsideGo = new GameObject("Outside Target");
            try
            {
                var gaze = headGo.AddComponent<GazePointer>();
                var canvas = canvasGo.GetComponent<Canvas>();
                canvas.renderMode = RenderMode.WorldSpace;
                var rt = (RectTransform)canvasGo.transform;
                rt.SetPositionAndRotation(new Vector3(0f, 0f, 2f), Quaternion.identity);
                rt.sizeDelta = new Vector2(400f, 300f);
                rt.localScale = Vector3.one * 0.002f; // 0.8 m wide x 0.6 m tall
                var img = canvasGo.GetComponent<Image>();
                img.raycastTarget = true;
                var scroll = canvasGo.GetComponent<TestVisualScrollTarget>();
                scroll.Rect = rt;
                GraphicRegistry.RegisterRaycastGraphicForCanvas(canvas, img);
                Canvas.ForceUpdateCanvases();

                var lowPinchRay = new Ray(Vector3.zero, new Vector3(0f, -0.5f, 1f).normalized);
                VisualPinchScroller.SyncPinchRay(lowPinchRay);
                Assert.IsFalse(VisualPinchScroller.TryBegin(PinchSource.Camera, gaze, outsideGo));
                Assert.IsTrue(VisualPinchScroller.TryBegin(PinchSource.Camera, gaze, img.gameObject));
                Assert.IsTrue(VisualPinchScroller.IsActive);

                var movedUpRay = new Ray(Vector3.zero, new Vector3(0f, -0.4f, 1f).normalized);
                VisualPinchScroller.UpdateHand(movedUpRay, true, true, 0.03f);
                Assert.IsTrue(VisualPinchScroller.IsDragging);
                Assert.AreEqual(1, scroll.Begins);
                Assert.AreEqual(1, scroll.Moves);
                Assert.Greater(scroll.LastDelta.y, 60f);
                Assert.Less(Mathf.Abs(scroll.LastDelta.x), 1f);

                var farRightRay = new Ray(Vector3.zero, new Vector3(0.9f, -0.4f, 1f).normalized);
                VisualPinchScroller.UpdateHand(farRightRay, true, true, 0.03f);
                Assert.IsTrue(VisualPinchScroller.IsDragging);
                Assert.AreEqual(2, scroll.Moves);

                VisualPinchScroller.UpdateHand(farRightRay, true, false, 0.03f);
                Assert.IsFalse(VisualPinchScroller.IsActive);
                Assert.AreEqual(1, scroll.Ends);
                Assert.IsFalse(scroll.LastCancelled);
            }
            finally
            {
                VisualPinchScroller.Reset();
                Object.DestroyImmediate(outsideGo);
                Object.DestroyImmediate(canvasGo);
                Object.DestroyImmediate(headGo);
            }
        }

        [Test]
        public void PredictOffset_FollowsVelocityAndClampsMaxDistance()
        {
            Assert.AreEqual(Vector2.zero, PinchSensor.PredictOffset(Vector2.zero, 0.04f, 100f));
            Assert.AreEqual(Vector2.zero, PinchSensor.PredictOffset(new Vector2(300f, 0f), 0f, 100f));

            var offset = PinchSensor.PredictOffset(new Vector2(400f, -200f), 0.04f, 100f);
            AssertClose(new Vector2(13.6f, -6.8f), offset, "forward velocity prediction", 1e-3f);

            var clamped = PinchSensor.PredictOffset(new Vector2(5000f, 0f), 0.2f, 60f);
            Assert.LessOrEqual(clamped.magnitude, 60.001f);
            Assert.Greater(clamped.x, 59f);
        }

        [Test]
        public void PinchSensor_ShouldReleasePinch_FastOnClearSeparationAndDebouncesBorderline()
        {
            var openStreak = 0;
            var borderline = (PinchSensor.k_OpenRatio + PinchSensor.k_FastOpenRatio) * 0.5f;

            // Single borderline sample is debounced, and drops back into pinch range reset the streak.
            Assert.IsFalse(PinchSensor.ShouldReleasePinch(borderline, ref openStreak));
            Assert.AreEqual(1, openStreak);
            Assert.IsFalse(PinchSensor.ShouldReleasePinch(PinchSensor.k_CloseRatio + 0.05f, ref openStreak));
            Assert.AreEqual(0, openStreak);

            // Two consecutive borderline samples release.
            Assert.IsFalse(PinchSensor.ShouldReleasePinch(borderline, ref openStreak));
            Assert.IsTrue(PinchSensor.ShouldReleasePinch(borderline, ref openStreak));
            Assert.AreEqual(0, openStreak);

            // Clear separation releases immediately in 1 frame with zero lag.
            Assert.IsTrue(PinchSensor.ShouldReleasePinch(PinchSensor.k_FastOpenRatio, ref openStreak));
            Assert.AreEqual(0, openStreak);
        }

        [Test]
        public void VisualPinchScroller_SinglePinchOverScreenClicksAtGazePointerAndSkipsActionButtons()
        {
            VisualPinchScroller.Reset();
            var headGo = new GameObject("Head");
            var canvasGo = new GameObject("Scroll Canvas", typeof(RectTransform), typeof(Canvas), typeof(Image), typeof(GazeCanvas), typeof(PointerEventRecorder), typeof(TestVisualScrollTarget));
            var btnGo = new GameObject("Action Button", typeof(RectTransform), typeof(Button), typeof(M3Button));
            var outsideGo = new GameObject("Outside Target");
            try
            {
                var gaze = headGo.AddComponent<GazePointer>();
                var canvas = canvasGo.GetComponent<Canvas>();
                canvas.renderMode = RenderMode.WorldSpace;
                var rt = (RectTransform)canvasGo.transform;
                rt.SetPositionAndRotation(new Vector3(0f, 0f, 2f), Quaternion.identity);
                rt.sizeDelta = new Vector2(400f, 300f);
                rt.localScale = Vector3.one * 0.002f;
                var img = canvasGo.GetComponent<Image>();
                img.raycastTarget = true;
                var recorder = canvasGo.GetComponent<PointerEventRecorder>();
                var scroll = canvasGo.GetComponent<TestVisualScrollTarget>();
                scroll.Rect = rt;

                btnGo.transform.SetParent(canvasGo.transform, false);
                btnGo.GetComponent<M3Button>().Initialize(M3Button.Variant.Tonal, btnGo.GetComponent<Button>(), img, null, null, null, null, Color.white);

                GraphicRegistry.RegisterRaycastGraphicForCanvas(canvas, img);
                Canvas.ForceUpdateCanvases();

                gaze.UpdateGaze();
                Assert.AreSame(img.gameObject, gaze.Target);

                var centerRay = new Ray(Vector3.zero, Vector3.forward);
                VisualPinchScroller.SyncPinchRay(centerRay);

                Assert.IsFalse(VisualPinchScroller.TryBegin(PinchSource.Camera, gaze, btnGo, gazeIsButton: true),
                    "action buttons inside scrollable panels must click directly instead of scrolling");
                Assert.IsFalse(VisualPinchScroller.TryBegin(PinchSource.Camera, gaze, outsideGo),
                    "pinch over a scrollable surface must not hijack a target on another object");

                Assert.IsTrue(VisualPinchScroller.TryBegin(PinchSource.Camera, gaze, img.gameObject));
                VisualPinchScroller.UpdateHand(centerRay, true, false, 0.02f);
                Assert.IsFalse(VisualPinchScroller.IsActive);
                Assert.AreEqual(0, scroll.Begins);
                Assert.IsTrue(recorder.events.Contains("click"),
                    "quick single pinch over a scrollable surface must click where the pointer aims");
            }
            finally
            {
                VisualPinchScroller.Reset();
                Object.DestroyImmediate(outsideGo);
                Object.DestroyImmediate(btnGo);
                Object.DestroyImmediate(canvasGo);
                Object.DestroyImmediate(headGo);
            }
        }

        [Test]
        public void RenderLayerHierarchy_ReticleStrictlyAbovePalmOverlayAndHandContour()
        {
            Assert.Greater(UiRenderQueue.k_HandContourSortingOrder, 0);
            Assert.Less(UiRenderQueue.k_HandContourSortingOrder, UiRenderQueue.k_PalmOverlaySortingOrder);
            Assert.Less(UiRenderQueue.k_PalmOverlaySortingOrder, UiRenderQueue.k_HandRaySortingOrder);
            Assert.Less(UiRenderQueue.k_HandRaySortingOrder, UiRenderQueue.k_ReticleSortingOrder);
            Assert.Less(UiRenderQueue.k_ReticleSortingOrder, UiRenderQueue.k_ScreenFadeSortingOrder);

            var go = new GameObject("Test Reticle", typeof(MeshFilter), typeof(MeshRenderer), typeof(GazeReticle));
            try
            {
                var reticle = go.GetComponent<GazeReticle>();
                typeof(GazeReticle).GetMethod("Awake", BindingFlags.Instance | BindingFlags.NonPublic)?.Invoke(reticle, null);
                Assert.AreEqual(UiRenderQueue.k_ReticleSortingOrder, go.GetComponent<MeshRenderer>().sortingOrder);
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void HandContourVectorMesh_Creates4SubmeshesExpandsOutwardFadesCuffAndTogglesPinchRing()
        {
            var mesh = HandJointsView.CreateContourMesh();
            try
            {
                Assert.AreEqual(4, mesh.subMeshCount);
                Assert.AreEqual(HandJointsView.ContourVertexCount, mesh.vertexCount);

                var joints = new Vector2[22];
                joints[0] = new Vector2(0f, -0.05f);
                joints[1] = new Vector2(-0.025f, -0.02f);
                joints[2] = new Vector2(-0.040f, 0.01f);
                joints[3] = new Vector2(-0.050f, 0.035f);
                joints[4] = new Vector2(-0.058f, 0.058f);
                joints[5] = new Vector2(-0.022f, 0.03f);
                joints[6] = new Vector2(-0.024f, 0.068f);
                joints[7] = new Vector2(-0.025f, 0.095f);
                joints[8] = new Vector2(-0.025f, 0.118f);
                joints[9] = new Vector2(0f, 0.034f);
                joints[10] = new Vector2(0f, 0.075f);
                joints[11] = new Vector2(0f, 0.105f);
                joints[12] = new Vector2(0f, 0.130f);
                joints[13] = new Vector2(0.020f, 0.030f);
                joints[14] = new Vector2(0.022f, 0.068f);
                joints[15] = new Vector2(0.023f, 0.095f);
                joints[16] = new Vector2(0.024f, 0.115f);
                joints[17] = new Vector2(0.038f, 0.022f);
                joints[18] = new Vector2(0.042f, 0.052f);
                joints[19] = new Vector2(0.044f, 0.072f);
                joints[20] = new Vector2(0.046f, 0.090f);
                joints[21] = new Vector2(0f, -0.145f);

                var radii = new float[22];
                for (var i = 0; i < 22; i++)
                    radii[i] = i == 0 || i == 21 ? 0.035f : 0.008f;

                var verts = new Vector3[HandJointsView.ContourVertexCount];
                var uvs = new Vector2[HandJointsView.ContourVertexCount];
                const float strokeW = 0.002f;
                const float haloW = 0.003f;

                HandJointsView.FillContourGeometry(joints, radii, Vector2.down, 0.095f,
                    strokeW, haloW, joints[8], pinchClose: 0.2f, pinching: false, palmScale: 0.07f, verts, uvs);

                var vertsPerLayer = (HandJointsView.ContourVertexCount - 32) / 3;
                var minY0 = float.PositiveInfinity;
                var maxY0 = float.NegativeInfinity;
                var minY1 = float.PositiveInfinity;
                var maxY1 = float.NegativeInfinity;
                var minY2 = float.PositiveInfinity;
                var maxY2 = float.NegativeInfinity;
                var maxFingerFade = 0f;
                var minCuffFade = 1f;

                for (var i = 0; i < HandJointsView.ContourVertexCount; i++)
                {
                    Assert.IsFalse(float.IsNaN(verts[i].x) || float.IsNaN(verts[i].y));
                    if (i < vertsPerLayer)
                    {
                        minY0 = Mathf.Min(minY0, verts[i].y);
                        maxY0 = Mathf.Max(maxY0, verts[i].y);
                        if (verts[i].y > 0.10f) maxFingerFade = Mathf.Max(maxFingerFade, uvs[i].x);
                        if (verts[i].y < -0.13f) minCuffFade = Mathf.Min(minCuffFade, uvs[i].x);
                    }
                    else if (i < vertsPerLayer * 2)
                    {
                        minY1 = Mathf.Min(minY1, verts[i].y);
                        maxY1 = Mathf.Max(maxY1, verts[i].y);
                    }
                    else if (i < vertsPerLayer * 3)
                    {
                        minY2 = Mathf.Min(minY2, verts[i].y);
                        maxY2 = Mathf.Max(maxY2, verts[i].y);
                    }
                    else
                    {
                        Assert.AreEqual(0f, uvs[i].x, 1e-4f, "Pinch ring should be inactive when pinchClose <= 0.45");
                    }
                }

                Assert.Greater(maxY1, maxY0 + strokeW * 0.8f);
                Assert.Greater(maxY2, maxY1 + haloW * 0.8f);
                Assert.Less(minY1, minY0 - strokeW * 0.8f);
                Assert.Less(minY2, minY1 - haloW * 0.8f);
                Assert.AreEqual(1f, maxFingerFade, 1e-3f);
                Assert.AreEqual(0f, minCuffFade, 1e-3f);

                HandJointsView.FillContourGeometry(joints, radii, Vector2.down, 0.095f,
                    strokeW, haloW, joints[8], pinchClose: 0.9f, pinching: true, palmScale: 0.07f, verts, uvs);
                for (var i = vertsPerLayer * 3; i < HandJointsView.ContourVertexCount; i++)
                    Assert.AreEqual(1f, uvs[i].x, 1e-4f, "Pinch ring should illuminate when pinching");
            }
            finally
            {
                Object.DestroyImmediate(mesh);
            }
        }

        [Test]
        public void PinchSensor_ComputeDownsampleStride_KeepsUltrawideFramesFullSize()
        {
            Assert.AreEqual(1, PinchSensor.ComputeDownsampleStride(320));
            Assert.AreEqual(1, PinchSensor.ComputeDownsampleStride(480));
            Assert.AreEqual(1, PinchSensor.ComputeDownsampleStride(960));
            Assert.AreEqual(1, PinchSensor.ComputeDownsampleStride(1280));
            Assert.AreEqual(2, PinchSensor.ComputeDownsampleStride(1920));
            Assert.AreEqual(2, PinchSensor.ComputeDownsampleStride(2560));
        }

        [Test]
        public void HandStabilization_DeadbandsStillHandVelocityAndSmoothsDisplayJitter()
        {
            // PinchSensor stationary deadband zeros out sub-threshold noise while keeping fast motion prediction.
            Assert.AreEqual(Vector2.zero, PinchSensor.PredictOffset(new Vector2(10f, -8f), 0.04f, 64f));
            var movingOffset = PinchSensor.PredictOffset(new Vector2(140f, 0f), 0.04f, 64f);
            Assert.Greater(movingOffset.x, 1f);

            // HandTracker stationary deadband suppresses 3D drift while preserving intentional movement.
            Assert.AreEqual(Vector3.zero, HandTracker.DampStationaryVelocity(new Vector3(0.02f, -0.01f, 0.01f)));
            var movingVel = HandTracker.DampStationaryVelocity(new Vector3(0.30f, 0f, 0f));
            Assert.Greater(movingVel.x, 0.25f);

            // HandJointsView display smoother damps micro-jitter (1.5 mm) while tracking 10 mm moves cleanly.
            var origin = Vector3.zero;
            var jitterStep = HandJointsView.SmoothDisplayPoint(origin, new Vector3(0.0015f, 0f, 0f), 1f / 60f);
            Assert.Less(jitterStep.x / 0.0015f, 0.35f);

            var moveStep = HandJointsView.SmoothDisplayPoint(origin, new Vector3(0.010f, 0f, 0f), 1f / 60f);
            Assert.Greater(moveStep.x / 0.010f, 0.85f);
        }
    }
}
