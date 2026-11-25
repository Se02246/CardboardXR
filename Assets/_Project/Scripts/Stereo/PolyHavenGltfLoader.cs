using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Rendering;

namespace CardboardXR.Stereo
{
    public sealed class LoadedGltfModel
    {
        public GameObject Root;
        public Bounds LocalBounds;
        public readonly List<Mesh> Meshes = new();
        public readonly List<Material> Materials = new();
        public readonly List<Texture2D> Textures = new();

        public void DestroyResources()
        {
            if (Root != null)
            {
                DestroySafe(Root);
                Root = null;
            }
            for (var i = 0; i < Meshes.Count; i++)
            {
                if (Meshes[i] != null)
                    DestroySafe(Meshes[i]);
            }
            Meshes.Clear();
            for (var i = 0; i < Materials.Count; i++)
            {
                if (Materials[i] != null)
                    DestroySafe(Materials[i]);
            }
            Materials.Clear();
            for (var i = 0; i < Textures.Count; i++)
            {
                if (Textures[i] != null)
                    DestroySafe(Textures[i]);
            }
            Textures.Clear();
        }

        static void DestroySafe(UnityEngine.Object obj)
        {
            if (obj == null)
                return;
            if (Application.isPlaying)
                UnityEngine.Object.Destroy(obj);
            else
                UnityEngine.Object.DestroyImmediate(obj);
        }
    }

    // Pure-C# glTF 2.0 runtime loader for Poly Haven 3D models (.gltf JSON + .bin + textures).
    // Converts RH Y-up -> Unity LH Y-up and normalizes the root pivot at bottom-center (min.y = 0)
    // so every placed object rests flush on horizontal surfaces and rotates cleanly around vertical up.
    public static class PolyHavenGltfLoader
    {
        const int k_CompByte = 5120;
        const int k_CompUByte = 5121;
        const int k_CompShort = 5122;
        const int k_CompUShort = 5123;
        const int k_CompUInt = 5125;
        const int k_CompFloat = 5126;

        public static LoadedGltfModel LoadFromFile(string gltfPath, Material baseMaterialTemplate)
        {
            if (string.IsNullOrEmpty(gltfPath) || !File.Exists(gltfPath))
                return null;
            var baseDir = Path.GetDirectoryName(gltfPath) ?? "";
            var json = File.ReadAllText(gltfPath);
            return LoadInternal(
                json,
                uri => ResolveBufferFromDisk(uri, baseDir),
                uri => ResolveTextureFromDisk(uri, baseDir),
                baseMaterialTemplate);
        }

        public static LoadedGltfModel LoadFromGlbFile(string glbPath, Material baseMaterialTemplate)
        {
            if (string.IsNullOrEmpty(glbPath) || !File.Exists(glbPath))
                return null;
            return LoadFromGlbBytes(File.ReadAllBytes(glbPath), baseMaterialTemplate);
        }

        public static LoadedGltfModel LoadFromGlbBytes(byte[] glbBytes, Material baseMaterialTemplate)
        {
            if (glbBytes == null || glbBytes.Length < 20)
                return null;
            var magic = BitConverter.ToUInt32(glbBytes, 0);
            var version = BitConverter.ToUInt32(glbBytes, 4);
            if (magic != 0x46546C67u || version != 2u)
                return null;

            string json = null;
            byte[] binChunk = null;
            var offset = 12;
            while (offset + 8 <= glbBytes.Length)
            {
                var chunkLength = BitConverter.ToInt32(glbBytes, offset);
                var chunkType = BitConverter.ToUInt32(glbBytes, offset + 4);
                var dataStart = offset + 8;
                if (chunkLength < 0 || dataStart + chunkLength > glbBytes.Length)
                    break;
                if (chunkType == 0x4E4F534Au && json == null)
                {
                    json = System.Text.Encoding.UTF8.GetString(glbBytes, dataStart, chunkLength).TrimEnd('\0', ' ');
                }
                else if (chunkType == 0x004E4942u && binChunk == null)
                {
                    binChunk = new byte[chunkLength];
                    Buffer.BlockCopy(glbBytes, dataStart, binChunk, 0, chunkLength);
                }
                offset = dataStart + chunkLength;
            }

            if (string.IsNullOrEmpty(json))
                return null;
            return LoadFromMemory(json, binChunk, baseMaterialTemplate);
        }

        public static LoadedGltfModel LoadFromMemory(string gltfJson, byte[] binData,
            Material baseMaterialTemplate, Func<string, Texture2D> textureResolver = null)
        {
            return LoadInternal(
                gltfJson,
                uri => DecodeDataUri(uri) ?? binData,
                textureResolver,
                baseMaterialTemplate);
        }

        static byte[] ResolveBufferFromDisk(string uri, string baseDir)
        {
            var inline = DecodeDataUri(uri);
            if (inline != null)
                return inline;
            if (string.IsNullOrEmpty(uri))
                return null;
            var rel = uri.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
            var full = Path.Combine(baseDir, rel);
            return File.Exists(full) ? File.ReadAllBytes(full) : null;
        }

        static Texture2D ResolveTextureFromDisk(string uri, string baseDir)
        {
            var inline = DecodeDataUri(uri);
            byte[] bytes = inline;
            if (bytes == null && !string.IsNullOrEmpty(uri))
            {
                var rel = uri.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
                var full = Path.Combine(baseDir, rel);
                if (File.Exists(full))
                    bytes = File.ReadAllBytes(full);
            }
            if (bytes == null || bytes.Length == 0)
                return null;
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, mipChain: true)
            {
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Repeat,
            };
            if (ImageConversion.LoadImage(tex, bytes, markNonReadable: true))
                return tex;
            UnityEngine.Object.Destroy(tex);
            return null;
        }

        static byte[] DecodeDataUri(string uri)
        {
            if (string.IsNullOrEmpty(uri) || !uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                return null;
            var comma = uri.IndexOf(',');
            if (comma < 0)
                return null;
            var meta = uri.Substring(0, comma);
            if (meta.IndexOf(";base64", StringComparison.OrdinalIgnoreCase) < 0)
                return null;
            try
            {
                return Convert.FromBase64String(uri.Substring(comma + 1));
            }
            catch (Exception)
            {
                return null;
            }
        }

        static LoadedGltfModel LoadInternal(
            string gltfJson,
            Func<string, byte[]> bufferResolver,
            Func<string, Texture2D> textureResolver,
            Material baseMaterialTemplate)
        {
            if (PolyHavenJson.Parse(gltfJson) is not Dictionary<string, object> rootDict)
                return null;

            var model = new LoadedGltfModel();
            var rootGo = new GameObject("PolyHavenModel");
            var innerRoot = new GameObject("Geometry");
            innerRoot.transform.SetParent(rootGo.transform, false);
            model.Root = rootGo;

            var buffersList = PolyHavenJson.GetList(rootDict, "buffers");
            var bufferBytes = new List<byte[]>(buffersList?.Count ?? 0);
            if (buffersList != null)
            {
                for (var i = 0; i < buffersList.Count; i++)
                {
                    var bDict = buffersList[i] as Dictionary<string, object>;
                    var uri = bDict != null ? PolyHavenJson.GetString(bDict, "uri") : "";
                    bufferBytes.Add(bufferResolver?.Invoke(uri));
                }
            }

            var bufferViews = PolyHavenJson.GetList(rootDict, "bufferViews");
            var accessors = PolyHavenJson.GetList(rootDict, "accessors");
            var imagesList = PolyHavenJson.GetList(rootDict, "images");
            var texturesList = PolyHavenJson.GetList(rootDict, "textures");
            var materialsList = PolyHavenJson.GetList(rootDict, "materials");
            var meshesList = PolyHavenJson.GetList(rootDict, "meshes");
            var nodesList = PolyHavenJson.GetList(rootDict, "nodes");
            var scenesList = PolyHavenJson.GetList(rootDict, "scenes");

            // Load images referenced by baseColor / normal / arm
            var loadedImages = new Dictionary<int, Texture2D>();
            var defaultMat = CreateDefaultMaterial(baseMaterialTemplate);
            model.Materials.Add(defaultMat);

            var builtMaterials = new List<Material>(materialsList?.Count ?? 0);
            if (materialsList != null)
            {
                for (var i = 0; i < materialsList.Count; i++)
                {
                    var matDict = materialsList[i] as Dictionary<string, object>;
                    var mat = BuildMaterial(matDict, defaultMat, baseMaterialTemplate, texturesList, imagesList,
                        bufferBytes, bufferViews, loadedImages, textureResolver, model.Textures);
                    builtMaterials.Add(mat);
                    model.Materials.Add(mat);
                }
            }

            // Build Unity Meshes per glTF mesh (preserving submeshes per primitive)
            var builtMeshes = new List<(Mesh mesh, Material[] mats)>(meshesList?.Count ?? 0);
            if (meshesList != null)
            {
                for (var m = 0; m < meshesList.Count; m++)
                {
                    var meshDict = meshesList[m] as Dictionary<string, object>;
                    var built = BuildMesh(meshDict, bufferBytes, bufferViews, accessors, builtMaterials, defaultMat);
                    if (built.mesh != null)
                        model.Meshes.Add(built.mesh);
                    builtMeshes.Add(built);
                }
            }

            // Instantiate node tree
            if (nodesList != null && nodesList.Count > 0)
            {
                var sceneIndex = (int)PolyHavenJson.GetLong(rootDict, "scene", 0);
                List<object> rootNodeIndices = null;
                if (scenesList != null && sceneIndex >= 0 && sceneIndex < scenesList.Count)
                    rootNodeIndices = PolyHavenJson.GetList(scenesList[sceneIndex], "nodes");

                if (rootNodeIndices != null && rootNodeIndices.Count > 0)
                {
                    for (var i = 0; i < rootNodeIndices.Count; i++)
                    {
                        var nodeIdx = ToInt(rootNodeIndices[i], -1);
                        if (nodeIdx >= 0)
                            BuildNodeRecursive(nodeIdx, innerRoot.transform, nodesList, builtMeshes, 0);
                    }
                }
                else
                {
                    for (var i = 0; i < nodesList.Count; i++)
                        BuildNodeRecursive(i, innerRoot.transform, nodesList, builtMeshes, 0);
                }
            }

            // Normalize pivot so bottom-center of the combined bounds sits cleanly at local (0, 0, 0).
            var renderers = innerRoot.GetComponentsInChildren<MeshRenderer>(true);
            if (renderers.Length > 0)
            {
                var combined = renderers[0].bounds;
                for (var i = 1; i < renderers.Length; i++)
                    combined.Encapsulate(renderers[i].bounds);

                var pivotShift = new Vector3(-combined.center.x, -combined.min.y, -combined.center.z);
                innerRoot.transform.localPosition = pivotShift;
                combined.center += pivotShift;
                model.LocalBounds = combined;
            }
            else
            {
                model.LocalBounds = new Bounds(new Vector3(0f, 0.25f, 0f), new Vector3(0.5f, 0.5f, 0.5f));
            }

            return model;
        }

        static Material CreateDefaultMaterial(Material template)
        {
            if (template != null)
                return new Material(template) { name = "PolyHavenDefault", color = Color.white };
            var shader = Shader.Find("Universal Render Pipeline/Simple Lit")
                         ?? Shader.Find("Universal Render Pipeline/Lit")
                         ?? Shader.Find("Universal Render Pipeline/Unlit")
                         ?? Shader.Find("Standard");
            return shader != null
                ? new Material(shader) { name = "PolyHavenDefault", color = Color.white }
                : new Material(Shader.Find("Hidden/InternalErrorShader"));
        }

        static Material BuildMaterial(
            Dictionary<string, object> matDict,
            Material fallback,
            Material template,
            List<object> texturesList,
            List<object> imagesList,
            List<byte[]> bufferBytes,
            List<object> bufferViews,
            Dictionary<int, Texture2D> loadedImages,
            Func<string, Texture2D> textureResolver,
            List<Texture2D> ownedTextures)
        {
            var mat = CreateDefaultMaterial(template ?? fallback);
            if (matDict == null)
                return mat;

            mat.name = PolyHavenJson.GetString(matDict, "name", "PolyHavenMat");
            Texture2D appliedBaseTex = null;
            var pbr = PolyHavenJson.GetDict(matDict, "pbrMetallicRoughness");
            if (pbr != null)
            {
                var factor = PolyHavenJson.GetList(pbr, "baseColorFactor");
                if (factor != null && factor.Count >= 4)
                {
                    var col = new Color(
                        ToFloat(factor[0], 1f),
                        ToFloat(factor[1], 1f),
                        ToFloat(factor[2], 1f),
                        ToFloat(factor[3], 1f));
                    mat.color = col;
                    if (mat.HasProperty("_BaseColor"))
                        mat.SetColor("_BaseColor", col);
                }

                var baseTexInfo = PolyHavenJson.GetDict(pbr, "baseColorTexture");
                if (baseTexInfo != null)
                {
                    var texIdx = (int)PolyHavenJson.GetLong(baseTexInfo, "index", -1);
                    appliedBaseTex = LoadTextureByIndex(texIdx, texturesList, imagesList,
                        bufferBytes, bufferViews, loadedImages, textureResolver, ownedTextures);
                }
            }

            if (appliedBaseTex == null)
            {
                var ext = PolyHavenJson.GetDict(matDict, "extensions");
                var specGloss = PolyHavenJson.GetDict(ext, "KHR_materials_pbrSpecularGlossiness");
                if (specGloss != null)
                {
                    var diffFactor = PolyHavenJson.GetList(specGloss, "diffuseFactor");
                    if (diffFactor != null && diffFactor.Count >= 4)
                    {
                        var col = new Color(
                            ToFloat(diffFactor[0], 1f),
                            ToFloat(diffFactor[1], 1f),
                            ToFloat(diffFactor[2], 1f),
                            ToFloat(diffFactor[3], 1f));
                        mat.color = col;
                        if (mat.HasProperty("_BaseColor"))
                            mat.SetColor("_BaseColor", col);
                    }
                    var diffTexInfo = PolyHavenJson.GetDict(specGloss, "diffuseTexture");
                    if (diffTexInfo != null)
                    {
                        var texIdx = (int)PolyHavenJson.GetLong(diffTexInfo, "index", -1);
                        appliedBaseTex = LoadTextureByIndex(texIdx, texturesList, imagesList,
                            bufferBytes, bufferViews, loadedImages, textureResolver, ownedTextures);
                    }
                }
            }

            if (appliedBaseTex != null)
            {
                mat.mainTexture = appliedBaseTex;
                if (mat.HasProperty("_BaseMap"))
                    mat.SetTexture("_BaseMap", appliedBaseTex);
            }

            if (matDict.TryGetValue("doubleSided", out var dsObj) && dsObj is bool doubleSided)
            {
                if (mat.HasProperty("_Cull"))
                    mat.SetFloat("_Cull", doubleSided ? 0f : 2f);
            }

            var alphaMode = PolyHavenJson.GetString(matDict, "alphaMode", "OPAQUE");
            if (alphaMode == "MASK" && mat.HasProperty("_AlphaClip"))
            {
                mat.SetFloat("_AlphaClip", 1f);
                var cutoff = (float)PolyHavenJson.GetDouble(matDict, "alphaCutoff", 0.5);
                if (mat.HasProperty("_Cutoff"))
                    mat.SetFloat("_Cutoff", cutoff);
                mat.EnableKeyword("_ALPHATEST_ON");
            }

            return mat;
        }

        static Texture2D LoadTextureByIndex(
            int texIndex,
            List<object> texturesList,
            List<object> imagesList,
            List<byte[]> bufferBytes,
            List<object> bufferViews,
            Dictionary<int, Texture2D> loadedImages,
            Func<string, Texture2D> textureResolver,
            List<Texture2D> ownedTextures)
        {
            if (texturesList == null || imagesList == null || texIndex < 0 || texIndex >= texturesList.Count)
                return null;
            var sourceIdx = (int)PolyHavenJson.GetLong(texturesList[texIndex], "source", -1);
            if (sourceIdx < 0 || sourceIdx >= imagesList.Count)
                return null;
            if (loadedImages.TryGetValue(sourceIdx, out var existing))
                return existing;

            var imgDict = imagesList[sourceIdx] as Dictionary<string, object>;
            var uri = PolyHavenJson.GetString(imgDict, "uri");
            Texture2D tex = null;
            if (!string.IsNullOrEmpty(uri))
            {
                tex = textureResolver?.Invoke(uri);
            }
            else if (imgDict != null && bufferBytes != null && bufferViews != null)
            {
                var bvIdx = (int)PolyHavenJson.GetLong(imgDict, "bufferView", -1);
                if (bvIdx >= 0 && bvIdx < bufferViews.Count && bufferViews[bvIdx] is Dictionary<string, object> bv)
                {
                    var bufIdx = (int)PolyHavenJson.GetLong(bv, "buffer", 0);
                    var offset = (int)PolyHavenJson.GetLong(bv, "byteOffset", 0);
                    var length = (int)PolyHavenJson.GetLong(bv, "byteLength", 0);
                    if (bufIdx >= 0 && bufIdx < bufferBytes.Count && bufferBytes[bufIdx] != null &&
                        offset >= 0 && length > 0 && offset + length <= bufferBytes[bufIdx].Length)
                    {
                        var slice = new byte[length];
                        Buffer.BlockCopy(bufferBytes[bufIdx], offset, slice, 0, length);
                        tex = new Texture2D(2, 2, TextureFormat.RGBA32, mipChain: true)
                        {
                            filterMode = FilterMode.Bilinear,
                            wrapMode = TextureWrapMode.Repeat,
                        };
                        if (!ImageConversion.LoadImage(tex, slice, markNonReadable: true))
                        {
                            if (Application.isPlaying)
                                UnityEngine.Object.Destroy(tex);
                            else
                                UnityEngine.Object.DestroyImmediate(tex);
                            tex = null;
                        }
                    }
                }
            }

            loadedImages[sourceIdx] = tex;
            if (tex != null)
                ownedTextures.Add(tex);
            return tex;
        }

        static (Mesh mesh, Material[] mats) BuildMesh(
            Dictionary<string, object> meshDict,
            List<byte[]> bufferBytes,
            List<object> bufferViews,
            List<object> accessors,
            List<Material> materials,
            Material defaultMat)
        {
            var primitives = PolyHavenJson.GetList(meshDict, "primitives");
            if (primitives == null || primitives.Count == 0)
                return (null, Array.Empty<Material>());

            var allVerts = new List<Vector3>();
            var allNorms = new List<Vector3>();
            var allUvs = new List<Vector2>();
            var submeshIndices = new List<int[]>();
            var submeshMats = new List<Material>();
            var missingNormals = false;

            for (var p = 0; p < primitives.Count; p++)
            {
                if (primitives[p] is not Dictionary<string, object> prim)
                    continue;
                var mode = (int)PolyHavenJson.GetLong(prim, "mode", 4);
                if (mode != 4)
                    continue;

                var attrs = PolyHavenJson.GetDict(prim, "attributes");
                if (attrs == null || !attrs.TryGetValue("POSITION", out var posAccObj))
                    continue;

                var posAcc = ToInt(posAccObj, -1);
                var positions = ReadVec3Accessor(posAcc, bufferBytes, bufferViews, accessors);
                if (positions == null || positions.Length == 0)
                    continue;

                var baseVertex = allVerts.Count;
                for (var i = 0; i < positions.Length; i++)
                {
                    var v = positions[i];
                    allVerts.Add(new Vector3(-v.x, v.y, v.z));
                }

                if (attrs.TryGetValue("NORMAL", out var normAccObj))
                {
                    var normals = ReadVec3Accessor(ToInt(normAccObj, -1), bufferBytes, bufferViews, accessors);
                    if (normals != null && normals.Length == positions.Length)
                    {
                        for (var i = 0; i < normals.Length; i++)
                        {
                            var n = normals[i];
                            allNorms.Add(new Vector3(-n.x, n.y, n.z).normalized);
                        }
                    }
                    else
                    {
                        missingNormals = true;
                        for (var i = 0; i < positions.Length; i++)
                            allNorms.Add(Vector3.up);
                    }
                }
                else
                {
                    missingNormals = true;
                    for (var i = 0; i < positions.Length; i++)
                        allNorms.Add(Vector3.up);
                }

                if (attrs.TryGetValue("TEXCOORD_0", out var uvAccObj))
                {
                    var uvs = ReadVec2Accessor(ToInt(uvAccObj, -1), bufferBytes, bufferViews, accessors);
                    if (uvs != null && uvs.Length == positions.Length)
                    {
                        for (var i = 0; i < uvs.Length; i++)
                            allUvs.Add(new Vector2(uvs[i].x, 1f - uvs[i].y));
                    }
                    else
                    {
                        for (var i = 0; i < positions.Length; i++)
                            allUvs.Add(Vector2.zero);
                    }
                }
                else
                {
                    for (var i = 0; i < positions.Length; i++)
                        allUvs.Add(Vector2.zero);
                }

                int[] tris;
                if (prim.TryGetValue("indices", out var idxAccObj))
                {
                    tris = ReadIndexAccessor(ToInt(idxAccObj, -1), bufferBytes, bufferViews, accessors);
                }
                else
                {
                    tris = new int[positions.Length];
                    for (var i = 0; i < positions.Length; i++)
                        tris[i] = i;
                }

                if (tris == null || tris.Length < 3)
                    continue;

                var triCount = tris.Length / 3;
                var converted = new int[triCount * 3];
                for (var t = 0; t < triCount; t++)
                {
                    var b = t * 3;
                    converted[b] = baseVertex + tris[b];
                    converted[b + 1] = baseVertex + tris[b + 2];
                    converted[b + 2] = baseVertex + tris[b + 1];
                }
                submeshIndices.Add(converted);

                var matIdx = (int)PolyHavenJson.GetLong(prim, "material", -1);
                var mat = matIdx >= 0 && matIdx < materials.Count ? materials[matIdx] : defaultMat;
                submeshMats.Add(mat);
            }

            if (allVerts.Count == 0 || submeshIndices.Count == 0)
                return (null, Array.Empty<Material>());

            var mesh = new Mesh
            {
                name = PolyHavenJson.GetString(meshDict, "name", "PolyHavenMesh"),
                indexFormat = allVerts.Count > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16,
            };
            mesh.SetVertices(allVerts);
            mesh.SetNormals(allNorms);
            mesh.SetUVs(0, allUvs);
            mesh.subMeshCount = submeshIndices.Count;
            for (var s = 0; s < submeshIndices.Count; s++)
                mesh.SetTriangles(submeshIndices[s], s);

            if (missingNormals)
                mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            return (mesh, submeshMats.ToArray());
        }

        static void BuildNodeRecursive(
            int nodeIndex,
            Transform parent,
            List<object> nodesList,
            List<(Mesh mesh, Material[] mats)> builtMeshes,
            int depth)
        {
            if (depth > 32 || nodeIndex < 0 || nodeIndex >= nodesList.Count)
                return;
            if (nodesList[nodeIndex] is not Dictionary<string, object> nodeDict)
                return;

            var go = new GameObject(PolyHavenJson.GetString(nodeDict, "name", $"Node_{nodeIndex}"));
            go.transform.SetParent(parent, false);
            ApplyNodeTransform(nodeDict, go.transform);

            var meshIdx = (int)PolyHavenJson.GetLong(nodeDict, "mesh", -1);
            if (meshIdx >= 0 && meshIdx < builtMeshes.Count && builtMeshes[meshIdx].mesh != null)
            {
                go.AddComponent<MeshFilter>().sharedMesh = builtMeshes[meshIdx].mesh;
                var mr = go.AddComponent<MeshRenderer>();
                mr.sharedMaterials = builtMeshes[meshIdx].mats;
                mr.shadowCastingMode = ShadowCastingMode.Off;
                mr.receiveShadows = false;
            }

            var children = PolyHavenJson.GetList(nodeDict, "children");
            if (children != null)
            {
                for (var i = 0; i < children.Count; i++)
                {
                    var childIdx = ToInt(children[i], -1);
                    if (childIdx >= 0)
                        BuildNodeRecursive(childIdx, go.transform, nodesList, builtMeshes, depth + 1);
                }
            }
        }

        static void ApplyNodeTransform(Dictionary<string, object> nodeDict, Transform t)
        {
            var matList = PolyHavenJson.GetList(nodeDict, "matrix");
            if (matList != null && matList.Count >= 16)
            {
                var gltfM = new Matrix4x4();
                for (var col = 0; col < 4; col++)
                {
                    for (var row = 0; row < 4; row++)
                        gltfM[row, col] = ToFloat(matList[col * 4 + row], row == col ? 1f : 0f);
                }
                // Conjugate column-major glTF matrix by S = diag(-1, 1, 1, 1) to map RH Y-up -> Unity LH Y-up.
                var mirror = Matrix4x4.Scale(new Vector3(-1f, 1f, 1f));
                var unityM = mirror * gltfM * mirror;
                t.localPosition = unityM.GetPosition();
                t.localRotation = unityM.rotation;
                t.localScale = unityM.lossyScale;
                return;
            }

            var trList = PolyHavenJson.GetList(nodeDict, "translation");
            if (trList != null && trList.Count >= 3)
                t.localPosition = new Vector3(-ToFloat(trList[0], 0f), ToFloat(trList[1], 0f), ToFloat(trList[2], 0f));

            var rotList = PolyHavenJson.GetList(nodeDict, "rotation");
            if (rotList != null && rotList.Count >= 4)
            {
                t.localRotation = new Quaternion(
                    ToFloat(rotList[0], 0f),
                    -ToFloat(rotList[1], 0f),
                    -ToFloat(rotList[2], 0f),
                    ToFloat(rotList[3], 1f));
            }

            var scList = PolyHavenJson.GetList(nodeDict, "scale");
            if (scList != null && scList.Count >= 3)
                t.localScale = new Vector3(ToFloat(scList[0], 1f), ToFloat(scList[1], 1f), ToFloat(scList[2], 1f));
        }

        static bool TryGetAccessorSpan(
            int accessorIndex,
            List<byte[]> buffers,
            List<object> bufferViews,
            List<object> accessors,
            out byte[] data,
            out int startOffset,
            out int stride,
            out int count,
            out int componentType)
        {
            data = null;
            startOffset = 0;
            stride = 0;
            count = 0;
            componentType = 0;

            if (accessors == null || bufferViews == null || accessorIndex < 0 || accessorIndex >= accessors.Count)
                return false;
            var acc = accessors[accessorIndex] as Dictionary<string, object>;
            if (acc == null)
                return false;

            var bvIdx = (int)PolyHavenJson.GetLong(acc, "bufferView", -1);
            if (bvIdx < 0 || bvIdx >= bufferViews.Count)
                return false;
            var bv = bufferViews[bvIdx] as Dictionary<string, object>;
            if (bv == null)
                return false;

            var bufIdx = (int)PolyHavenJson.GetLong(bv, "buffer", 0);
            if (bufIdx < 0 || bufIdx >= buffers.Count || buffers[bufIdx] == null)
                return false;

            data = buffers[bufIdx];
            var bvOffset = (int)PolyHavenJson.GetLong(bv, "byteOffset", 0);
            var accOffset = (int)PolyHavenJson.GetLong(acc, "byteOffset", 0);
            startOffset = bvOffset + accOffset;
            count = (int)PolyHavenJson.GetLong(acc, "count", 0);
            componentType = (int)PolyHavenJson.GetLong(acc, "componentType", 0);
            var typeStr = PolyHavenJson.GetString(acc, "type", "SCALAR");
            var elemSize = ComponentSize(componentType) * ComponentCount(typeStr);
            if (elemSize <= 0 || count <= 0)
                return false;
            var bvStride = (int)PolyHavenJson.GetLong(bv, "byteStride", 0);
            stride = bvStride > 0 ? bvStride : elemSize;
            return startOffset >= 0 && startOffset + (count - 1) * stride + elemSize <= data.Length;
        }

        static Vector3[] ReadVec3Accessor(int accessorIndex, List<byte[]> buffers, List<object> bufferViews, List<object> accessors)
        {
            if (!TryGetAccessorSpan(accessorIndex, buffers, bufferViews, accessors,
                out var data, out var offset, out var stride, out var count, out var compType))
                return null;
            if (compType != k_CompFloat)
                return null;
            var arr = new Vector3[count];
            for (var i = 0; i < count; i++)
            {
                var p = offset + i * stride;
                arr[i] = new Vector3(
                    BitConverter.ToSingle(data, p),
                    BitConverter.ToSingle(data, p + 4),
                    BitConverter.ToSingle(data, p + 8));
            }
            return arr;
        }

        static Vector2[] ReadVec2Accessor(int accessorIndex, List<byte[]> buffers, List<object> bufferViews, List<object> accessors)
        {
            if (!TryGetAccessorSpan(accessorIndex, buffers, bufferViews, accessors,
                out var data, out var offset, out var stride, out var count, out var compType))
                return null;
            if (compType != k_CompFloat)
                return null;
            var arr = new Vector2[count];
            for (var i = 0; i < count; i++)
            {
                var p = offset + i * stride;
                arr[i] = new Vector2(
                    BitConverter.ToSingle(data, p),
                    BitConverter.ToSingle(data, p + 4));
            }
            return arr;
        }

        static int[] ReadIndexAccessor(int accessorIndex, List<byte[]> buffers, List<object> bufferViews, List<object> accessors)
        {
            if (!TryGetAccessorSpan(accessorIndex, buffers, bufferViews, accessors,
                out var data, out var offset, out var stride, out var count, out var compType))
                return null;
            var arr = new int[count];
            for (var i = 0; i < count; i++)
            {
                var p = offset + i * stride;
                arr[i] = compType switch
                {
                    k_CompUShort => BitConverter.ToUInt16(data, p),
                    k_CompUInt => (int)BitConverter.ToUInt32(data, p),
                    k_CompUByte => data[p],
                    k_CompShort => BitConverter.ToInt16(data, p),
                    _ => 0,
                };
            }
            return arr;
        }

        static int ComponentSize(int compType) => compType switch
        {
            k_CompByte or k_CompUByte => 1,
            k_CompShort or k_CompUShort => 2,
            k_CompUInt or k_CompFloat => 4,
            _ => 0,
        };

        static int ComponentCount(string typeStr) => typeStr switch
        {
            "SCALAR" => 1,
            "VEC2" => 2,
            "VEC3" => 3,
            "VEC4" => 4,
            "MAT4" => 16,
            _ => 0,
        };

        static int ToInt(object obj, int fallback) => obj switch
        {
            long l => (int)l,
            int i => i,
            double d => (int)d,
            _ => fallback,
        };

        static float ToFloat(object obj, float fallback) => obj switch
        {
            double d => (float)d,
            float f => f,
            long l => l,
            int i => i,
            _ => fallback,
        };
    }
}
