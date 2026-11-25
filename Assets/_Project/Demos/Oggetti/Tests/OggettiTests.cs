using System;
using System.Collections.Generic;
using System.Text;
using CardboardXR.Stereo;
using NUnit.Framework;
using UnityEngine;

namespace CardboardXR.Demos.Oggetti.Tests
{
    public class OggettiTests
    {
        [Test]
        public void SketchfabClient_BuildsSearchUrlAndParsesCatalogCursorsAndThumbnails()
        {
            Assert.AreEqual(19, SketchfabClient.Categories.Count); // "Tutti" + 18 official categories
            var url = SketchfabClient.BuildSearchUrl("furniture-home", "wooden chair", "-likeCount", "cur123", 12);
            Assert.IsTrue(url.StartsWith("https://api.sketchfab.com/v3/search?type=models&downloadable=true"));
            Assert.IsTrue(url.Contains("categories=furniture-home"));
            Assert.IsTrue(url.Contains("q=wooden%20chair"));
            Assert.IsTrue(url.Contains("cursor=cur123"));

            const string sampleJson = @"{
                ""cursors"": {""next"": ""c2"", ""previous"": ""c0""},
                ""results"": [
                    {
                        ""uid"": ""abc123"",
                        ""name"": ""Modern Chair"",
                        ""likeCount"": 420,
                        ""faceCount"": 12500,
                        ""user"": {""username"": ""jdoe"", ""displayName"": ""John Doe""},
                        ""thumbnails"": {
                            ""images"": [
                                {""width"": 64, ""url"": ""https://img/64.jpg""},
                                {""width"": 720, ""url"": ""https://img/720.jpg""},
                                {""width"": 1920, ""url"": ""https://img/1920.jpg""}
                            ]
                        },
                        ""archives"": {
                            ""glb"": {""size"": 4194304},
                            ""gltf"": {""size"": 5242880}
                        },
                        ""license"": {""label"": ""CC Attribution""}
                    }
                ]
            }";

            var page = SketchfabClient.ParseSearchResponse(sampleJson);
            Assert.AreEqual("c2", page.NextCursor);
            Assert.AreEqual("c0", page.PreviousCursor);
            Assert.AreEqual(1, page.Results.Count);
            Assert.AreEqual("abc123", page.Results[0].Uid);
            Assert.AreEqual("Modern Chair", page.Results[0].Name);
            Assert.AreEqual("John Doe", page.Results[0].AuthorName);
            Assert.AreEqual("https://img/720.jpg", page.Results[0].ThumbnailUrl);
            Assert.IsTrue(page.Results[0].HasGlb);
            Assert.AreEqual(4194304L, page.Results[0].DownloadSizeBytes);
            Assert.AreEqual("4.0 MB", SketchfabClient.FormatBytes(4194304L));
        }

        [Test]
        public void SketchfabClient_ParsesDownloadProfileAndSanitizesToken()
        {
            const string dlGlb = @"{""glb"":{""url"":""https://s3/model.glb"",""size"":1024},""gltf"":{""url"":""https://s3/model.zip"",""size"":2048}}";
            var link = SketchfabClient.ParseDownloadResponse(dlGlb);
            Assert.IsTrue(link.IsValid);
            Assert.IsTrue(link.IsGlb);
            Assert.AreEqual("https://s3/model.glb", link.Url);

            const string dlZip = @"{""gltf"":{""url"":""https://s3/model.zip"",""size"":2048}}";
            var linkZip = SketchfabClient.ParseDownloadResponse(dlZip);
            Assert.IsTrue(linkZip.IsValid);
            Assert.IsFalse(linkZip.IsGlb);
            Assert.AreEqual("https://s3/model.zip", linkZip.Url);

            var profile = SketchfabClient.ParseUserProfile(@"{""username"":""mario"",""displayName"":""Mario Rossi""}");
            Assert.IsTrue(profile.Valid);
            Assert.AreEqual("mario", profile.Username);
            Assert.AreEqual("Mario Rossi", profile.DisplayName);

            Assert.AreEqual("abc123", SketchfabClient.SanitizeToken("  Token abc123 \n"));
            Assert.AreEqual("xyz", SketchfabClient.SanitizeToken("Bearer xyz"));
            Assert.AreEqual("https://sketchfab.com/settings/password", SketchfabClient.TokenSettingsUrl);
        }

        [Test]
        public void GltfLoader_LoadsBinaryGlbWithEmbeddedTextureAndBottomCenterPivot()
        {
            // Create a valid 2x2 PNG in memory for the GLB image bufferView.
            var pngTex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            pngTex.SetPixel(0, 0, Color.cyan);
            pngTex.SetPixel(1, 0, Color.cyan);
            pngTex.SetPixel(0, 1, Color.cyan);
            pngTex.SetPixel(1, 1, Color.cyan);
            pngTex.Apply(false);
            var pngBytes = pngTex.EncodeToPNG();
            UnityEngine.Object.DestroyImmediate(pngTex);

            var bin = new List<byte>();
            AddFloat3(bin, -1f, 2f, 0f);
            AddFloat3(bin, 1f, 2f, 0f);
            AddFloat3(bin, 0f, 4f, 0f);
            AddFloat2(bin, 0f, 0f);
            AddFloat2(bin, 1f, 0f);
            AddFloat2(bin, 0.5f, 1f);
            bin.AddRange(BitConverter.GetBytes((ushort)0));
            bin.AddRange(BitConverter.GetBytes((ushort)1));
            bin.AddRange(BitConverter.GetBytes((ushort)2));
            // Pad to 4-byte boundary before PNG image slice
            while (bin.Count % 4 != 0)
                bin.Add(0);
            var imgOffset = bin.Count;
            bin.AddRange(pngBytes);
            while (bin.Count % 4 != 0)
                bin.Add(0);

            var gltfJson =
                $@"{{
                    ""asset"":{{""version"":""2.0""}},
                    ""scene"":0,
                    ""scenes"":[{{""nodes"":[0]}}],
                    ""nodes"":[{{""mesh"":0,""name"":""Quad""}}],
                    ""materials"":[{{""name"":""Mat"",""pbrMetallicRoughness"":{{""baseColorTexture"":{{""index"":0}}}}}}],
                    ""textures"":[{{""source"":0}}],
                    ""images"":[{{""bufferView"":3,""mimeType"":""image/png""}}],
                    ""meshes"":[{{""primitives"":[{{""attributes"":{{""POSITION"":0,""TEXCOORD_0"":1}},""indices"":2,""material"":0}}]}}],
                    ""accessors"":[
                        {{""bufferView"":0,""componentType"":5126,""count"":3,""type"":""VEC3""}},
                        {{""bufferView"":1,""componentType"":5126,""count"":3,""type"":""VEC2""}},
                        {{""bufferView"":2,""componentType"":5123,""count"":3,""type"":""SCALAR""}}
                    ],
                    ""bufferViews"":[
                        {{""buffer"":0,""byteOffset"":0,""byteLength"":36}},
                        {{""buffer"":0,""byteOffset"":36,""byteLength"":24}},
                        {{""buffer"":0,""byteOffset"":60,""byteLength"":6}},
                        {{""buffer"":0,""byteOffset"":{imgOffset},""byteLength"":{pngBytes.Length}}}
                    ],
                    ""buffers"":[{{""byteLength"":{bin.Count}}}]
                }}";

            var jsonBytes = Encoding.UTF8.GetBytes(gltfJson);
            var paddedJsonLen = (jsonBytes.Length + 3) & ~3;
            var glb = new List<byte>(12 + 8 + paddedJsonLen + 8 + bin.Count);
            // GLB header
            glb.AddRange(BitConverter.GetBytes(0x46546C67u));
            glb.AddRange(BitConverter.GetBytes(2u));
            glb.AddRange(BitConverter.GetBytes((uint)(12 + 8 + paddedJsonLen + 8 + bin.Count)));
            // JSON chunk
            glb.AddRange(BitConverter.GetBytes((uint)paddedJsonLen));
            glb.AddRange(BitConverter.GetBytes(0x4E4F534Au));
            glb.AddRange(jsonBytes);
            for (var i = jsonBytes.Length; i < paddedJsonLen; i++)
                glb.Add((byte)' ');
            // BIN chunk
            glb.AddRange(BitConverter.GetBytes((uint)bin.Count));
            glb.AddRange(BitConverter.GetBytes(0x004E4942u));
            glb.AddRange(bin);

            var model = PolyHavenGltfLoader.LoadFromGlbBytes(glb.ToArray(), null);
            try
            {
                Assert.IsNotNull(model);
                Assert.AreEqual(1, model.Meshes.Count);
                Assert.AreEqual(1, model.Textures.Count);
                Assert.AreEqual(new[] { 0, 2, 1 }, model.Meshes[0].GetTriangles(0));
                Assert.AreEqual(0f, model.LocalBounds.min.y, 1e-4f);
            }
            finally
            {
                model?.DestroyResources();
            }
        }

        [Test]
        public void OggettiLayout_RotatesStrictlyAroundVerticalAxisAndClampsScale()
        {
            var rot = OggettiLayout.VerticalAxisRotation(135f);
            var euler = rot.eulerAngles;
            Assert.AreEqual(0f, Mathf.DeltaAngle(0f, euler.x), 1e-3f);
            Assert.AreEqual(135f, euler.y, 1e-3f);
            Assert.AreEqual(0f, Mathf.DeltaAngle(0f, euler.z), 1e-3f);

            Assert.AreEqual(OggettiLayout.MinScale, OggettiLayout.ClampScale(0.01f), 1e-4f);
            Assert.AreEqual(OggettiLayout.MaxScale, OggettiLayout.ClampScale(99f), 1e-4f);
            Assert.IsTrue(DemoManager.IsWindowedApp(new DemoCatalog.Entry { Id = "oggetti" }));

            // Centimeter-scale Sketchfab models (e.g. 100 units tall) normalize cleanly to StandardHeightMeters.
            var hugeBounds = new Bounds(new Vector3(0f, 50f, 0f), new Vector3(50f, 100f, 50f));
            var hugeScale = OggettiLayout.DefaultNormalizedScale(hugeBounds);
            Assert.AreEqual(OggettiLayout.StandardHeightMeters, hugeBounds.size.y * hugeScale, 1e-3f);

            // Wide flat models respect MaxSpawnDimensionMeters on their largest axis.
            var wideBounds = new Bounds(new Vector3(0f, 0.4f, 0f), new Vector3(4f, 0.8f, 2f));
            var wideScale = OggettiLayout.DefaultNormalizedScale(wideBounds);
            Assert.AreEqual(OggettiLayout.MaxSpawnDimensionMeters, wideBounds.size.x * wideScale, 1e-3f);
            Assert.LessOrEqual(wideBounds.size.y * wideScale, OggettiLayout.StandardHeightMeters + 1e-3f);
        }

        static void AddFloat2(List<byte> buf, float x, float y)
        {
            buf.AddRange(BitConverter.GetBytes(x));
            buf.AddRange(BitConverter.GetBytes(y));
        }

        static void AddFloat3(List<byte> buf, float x, float y, float z)
        {
            buf.AddRange(BitConverter.GetBytes(x));
            buf.AddRange(BitConverter.GetBytes(y));
            buf.AddRange(BitConverter.GetBytes(z));
        }
    }
}
