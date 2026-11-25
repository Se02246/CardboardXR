using System.Collections.Generic;
using System.Text;
using CardboardXR.Demos;
using CardboardXR.Hands;
using NUnit.Framework;
using UnityEngine;

namespace CardboardXR.Stereo.Tests
{
    public class PolyHavenEnvironmentTests
    {
        const string k_StorePrefKey = "CardboardXR.PolyHavenEnvironments";

        bool m_HadStorePref;
        string m_SavedStorePref;

        [SetUp]
        public void SetUp()
        {
            m_HadStorePref = PlayerPrefs.HasKey(k_StorePrefKey);
            m_SavedStorePref = m_HadStorePref ? PlayerPrefs.GetString(k_StorePrefKey, "") : "";
            EnvironmentLibraryStore.ResetToDefaults();
        }

        [TearDown]
        public void TearDown()
        {
            if (m_HadStorePref)
                PlayerPrefs.SetString(k_StorePrefKey, m_SavedStorePref);
            else
                PlayerPrefs.DeleteKey(k_StorePrefKey);
            EnvironmentLibraryStore.ResetToDefaults();
        }

        [Test]
        public void UserAgentHeader_IsSetOnEveryRequest()
        {
            Assert.AreEqual("CardboardXR/1.0", PolyHavenClient.UserAgent);
            using var req = PolyHavenService.CreateGetRequest(PolyHavenClient.AssetsEndpoint);
            Assert.AreEqual(PolyHavenClient.UserAgent, req.GetRequestHeader("User-Agent"));
        }

        [Test]
        public void AttributionText_IsPoweredByPolyHaven()
        {
            Assert.AreEqual("Powered by Poly Haven", PolyHavenClient.AttributionText);
        }

        [Test]
        public void MiniJson_ParsesPolyHavenCatalogAndFilesPayload()
        {
            const string json = @"{
                ""spiaggia_di_mondello"": {
                    ""name"": ""Spiaggia di Mondello"",
                    ""category"": ""Coast \u0026 Water/Beaches/Sandy Beaches"",
                    ""tags"": [""beach"", ""sea"", ""italy""],
                    ""download_count"": 42150,
                    ""featured"": true
                },
                ""extra"": null
            }";

            var root = PolyHavenJson.Parse(json) as Dictionary<string, object>;
            Assert.IsNotNull(root);
            Assert.IsNull(root["extra"]);

            var item = PolyHavenJson.GetDict(root, "spiaggia_di_mondello");
            Assert.IsNotNull(item);
            Assert.AreEqual("Spiaggia di Mondello", PolyHavenJson.GetString(item, "name"));
            Assert.AreEqual("Coast & Water/Beaches/Sandy Beaches", PolyHavenJson.GetString(item, "category"));
            Assert.AreEqual(42150, PolyHavenJson.GetLong(item, "download_count"));
            CollectionAssert.AreEqual(new[] { "beach", "sea", "italy" }, PolyHavenJson.GetStringList(item, "tags"));

            Assert.IsNull(PolyHavenJson.Parse("{broken"));
        }

        [Test]
        public void Catalog_CoversAllTwelveCategoriesAndFiltersCorrectly()
        {
            Assert.AreEqual(12, PolyHavenClient.Categories.Count);

            const string catalogJson = @"{
                ""empty_warehouse_01"": { ""name"": ""Empty Warehouse 01"", ""category"": ""Abandoned & Ruins/Industrial"", ""tags"": [""ruins""], ""download_count"": 900 },
                ""venice_sunset"": { ""name"": ""Venice Sunset"", ""category"": ""Coast & Water/Harbours & Piers"", ""tags"": [""sunset""], ""download_count"": 5000 },
                ""goegap"": { ""name"": ""Goegap"", ""category"": ""Desert & Arid/Karoo"", ""tags"": [""desert""], ""download_count"": 300 },
                ""lilienstein"": { ""name"": ""Lilienstein"", ""category"": ""Fields & Countryside/Fields"", ""tags"": [""grass""], ""download_count"": 700 },
                ""meadow_2"": { ""name"": ""Meadow 2"", ""category"": ""Forest & Woodland/Clearings"", ""tags"": [""trees""], ""download_count"": 800 },
                ""freight_station"": { ""name"": ""Freight Station"", ""category"": ""Industrial/Rail"", ""tags"": [""train""], ""download_count"": 200 },
                ""fireplace"": { ""name"": ""Fireplace"", ""category"": ""Interiors/Living Rooms"", ""tags"": [""cozy""], ""download_count"": 3200 },
                ""alps_field"": { ""name"": ""Alps Field"", ""category"": ""Mountains & Hills/Alpine"", ""tags"": [""snow""], ""download_count"": 1500 },
                ""autumn_park"": { ""name"": ""Autumn Park"", ""category"": ""Parks & Gardens/Parks"", ""tags"": [""leaves""], ""download_count"": 2100 },
                ""kloppenheim_06_puresky"": { ""name"": ""Kloppenheim 06 (Pure Sky)"", ""category"": ""Pure Skies"", ""tags"": [""sky""], ""download_count"": 4100 },
                ""canary_wharf"": { ""name"": ""Canary Wharf"", ""category"": ""Streets & Town/Urban"", ""tags"": [""city""], ""download_count"": 1100 },
                ""studio_small_09"": { ""name"": ""Studio Small 09"", ""category"": ""Studio/Photo Studios"", ""tags"": [""softbox""], ""download_count"": 2800 }
            }";

            var catalog = PolyHavenClient.ParseAssetsCatalog(catalogJson);
            Assert.AreEqual(12, catalog.Count);
            Assert.AreEqual("venice_sunset", catalog[0].Id);

            var summaries = PolyHavenClient.BuildCategorySummaries(catalog);
            Assert.AreEqual(12, summaries.Count);
            for (var i = 0; i < summaries.Count; i++)
            {
                Assert.AreEqual(1, summaries[i].Count, $"Expected count 1 for {summaries[i].ApiName}");
                Assert.IsNotEmpty(summaries[i].RepresentativeAssetId);
                Assert.IsTrue(summaries[i].RepresentativeThumbUrl.Contains("format=jpg"));
            }

            var interiors = PolyHavenClient.FilterByCategory(catalog, "Interiors");
            Assert.AreEqual(1, interiors.Count);
            Assert.AreEqual("fireplace", interiors[0].Id);
        }

        [Test]
        public void SearchCatalog_MatchesNameIdTagsAndCategoryCaseInsensitive()
        {
            const string catalogJson = @"{
                ""venice_sunset"": { ""name"": ""Venice Sunset"", ""category"": ""Coast & Water/Harbours"", ""tags"": [""water"", ""evening""], ""download_count"": 5000 },
                ""spiaggia_di_mondello"": { ""name"": ""Spiaggia di Mondello"", ""category"": ""Coast & Water/Beaches"", ""tags"": [""sicily"", ""sea""], ""download_count"": 4200 },
                ""fireplace"": { ""name"": ""Fireplace"", ""category"": ""Interiors/Living Rooms"", ""tags"": [""indoor"", ""warm""], ""download_count"": 3200 }
            }";

            var catalog = PolyHavenClient.ParseAssetsCatalog(catalogJson);

            var byName = PolyHavenClient.SearchCatalog(catalog, "venice");
            Assert.AreEqual(1, byName.Count);
            Assert.AreEqual("venice_sunset", byName[0].Id);

            var byTag = PolyHavenClient.SearchCatalog(catalog, "sicily");
            Assert.AreEqual(1, byTag.Count);
            Assert.AreEqual("spiaggia_di_mondello", byTag[0].Id);

            var byItalianCat = PolyHavenClient.SearchCatalog(catalog, "interni");
            Assert.AreEqual(1, byItalianCat.Count);
            Assert.AreEqual("fireplace", byItalianCat[0].Id);

            var scoped = PolyHavenClient.SearchCatalog(catalog, "water", "Interiors");
            Assert.AreEqual(0, scoped.Count);
        }

        [Test]
        public void SelectBestDownload_PrefersModerateTonemappedAndFallsBackToHdrForHugeFiles()
        {
            const string smallTonemappedJson = @"{
                ""tonemapped"": { ""url"": ""https://dl.polyhaven.org/tm.jpg"", ""size"": 4500000 },
                ""hdri"": { ""4k"": { ""hdr"": { ""url"": ""https://dl.polyhaven.org/4k.hdr"", ""size"": 18000000 } } }
            }";
            Assert.IsTrue(PolyHavenClient.TrySelectBestDownload(smallTonemappedJson, out var c1));
            Assert.IsFalse(c1.IsHdr);
            Assert.AreEqual("https://dl.polyhaven.org/tm.jpg", c1.Url);

            const string hugeTonemappedWith4kHdrJson = @"{
                ""tonemapped"": { ""url"": ""https://dl.polyhaven.org/tm75.jpg"", ""size"": 75000000 },
                ""hdri"": {
                    ""4k"": { ""hdr"": { ""url"": ""https://dl.polyhaven.org/4k.hdr"", ""size"": 18000000 } },
                    ""2k"": { ""hdr"": { ""url"": ""https://dl.polyhaven.org/2k.hdr"", ""size"": 6000000 } }
                }
            }";
            Assert.IsTrue(PolyHavenClient.TrySelectBestDownload(hugeTonemappedWith4kHdrJson, out var c2));
            Assert.IsTrue(c2.IsHdr);
            Assert.AreEqual("https://dl.polyhaven.org/4k.hdr", c2.Url);

            const string huge4kFallback2kJson = @"{
                ""tonemapped"": { ""url"": ""https://dl.polyhaven.org/tm75.jpg"", ""size"": 75000000 },
                ""hdri"": {
                    ""4k"": { ""hdr"": { ""url"": ""https://dl.polyhaven.org/4k_big.hdr"", ""size"": 28000000 } },
                    ""2k"": { ""hdr"": { ""url"": ""https://dl.polyhaven.org/2k.hdr"", ""size"": 6200000 } }
                }
            }";
            Assert.IsTrue(PolyHavenClient.TrySelectBestDownload(huge4kFallback2kJson, out var c3));
            Assert.IsTrue(c3.IsHdr);
            Assert.AreEqual("https://dl.polyhaven.org/2k.hdr", c3.Url);
        }

        [Test]
        public void RadianceHdrDecoder_DecodesUncompressedAndRleBuffers()
        {
            // Build synthetic 8x2 uncompressed Radiance HDR buffer
            var header = Encoding.ASCII.GetBytes("#?RADIANCE\nFORMAT=32-bit_rle_rgbe\n\n-Y 2 +X 8\n");
            var rawPixels = new byte[8 * 2 * 4];
            for (var i = 0; i < 16; i++)
            {
                rawPixels[i * 4 + 0] = 180;
                rawPixels[i * 4 + 1] = 140;
                rawPixels[i * 4 + 2] = 90;
                rawPixels[i * 4 + 3] = 128;
            }
            var rawHdr = new byte[header.Length + rawPixels.Length];
            System.Buffer.BlockCopy(header, 0, rawHdr, 0, header.Length);
            System.Buffer.BlockCopy(rawPixels, 0, rawHdr, header.Length, rawPixels.Length);

            Assert.IsTrue(RadianceHdrDecoder.TryDecodeToRgb24(rawHdr, out var w1, out var h1, out var rgb1, out var err1), err1);
            Assert.AreEqual(8, w1);
            Assert.AreEqual(2, h1);
            Assert.AreEqual(8 * 2 * 3, rgb1.Length);
            Assert.Greater(rgb1[0], 0);

            // Build synthetic 8x2 new-style RLE Radiance HDR buffer
            var rleList = new List<byte>(header);
            for (var y = 0; y < 2; y++)
            {
                rleList.Add(2);
                rleList.Add(2);
                rleList.Add(0);
                rleList.Add(8);
                byte[] channels = { 180, 140, 90, 128 };
                for (var ch = 0; ch < 4; ch++)
                {
                    rleList.Add(128 + 8); // run of 8
                    rleList.Add(channels[ch]);
                }
            }

            Assert.IsTrue(RadianceHdrDecoder.TryDecodeToRgb24(rleList.ToArray(), out var w2, out var h2, out var rgb2, out var err2), err2);
            Assert.AreEqual(8, w2);
            Assert.AreEqual(2, h2);
            CollectionAssert.AreEqual(rgb1, rgb2);
        }

        [Test]
        public void EnvironmentLibraryStore_AddRemoveAndRoundtripPersistence()
        {
            var initialCount = EnvironmentLibraryStore.Count;
            Assert.IsTrue(EnvironmentLibraryStore.AddOrUpdate("canary_wharf", "Canary Wharf", "Streets & Town"));
            Assert.AreEqual(initialCount + 1, EnvironmentLibraryStore.Count);
            Assert.IsTrue(EnvironmentLibraryStore.Contains("canary_wharf"));

            // Procedural cinema remains last
            var last = EnvironmentLibraryStore.Get(EnvironmentLibraryStore.Count - 1);
            Assert.IsTrue(last.IsProceduralCinema);
            Assert.IsFalse(EnvironmentLibraryStore.Remove(EnvironmentLibraryStore.ProceduralCinemaId, deleteCachedFiles: false));

            var serialized = EnvironmentLibraryStore.Serialize(EnvironmentLibraryStore.Entries);
            var parsed = EnvironmentLibraryStore.Deserialize(serialized);
            Assert.AreEqual(EnvironmentLibraryStore.Count - 1, parsed.Count);
            Assert.IsTrue(parsed.Exists(e => e.Id == "canary_wharf" && e.Title == "Canary Wharf"));

            Assert.IsTrue(EnvironmentLibraryStore.Remove("canary_wharf", deleteCachedFiles: false));
            Assert.AreEqual(initialCount, EnvironmentLibraryStore.Count);
        }

        [Test]
        public void CoverUrl_UsesPrimaryPanoramaAndCenterSquareUvCrop()
        {
            var url = PolyHavenClient.BuildThumbnailJpgUrl("spiaggia_di_mondello");
            Assert.AreEqual("https://cdn.polyhaven.com/asset_img/primary/spiaggia_di_mondello.png?width=512&format=jpg", url);

            var uv = AppLibrary.ComputeSquareCoverUvRect(512, 256);
            Assert.AreEqual(0.25f, uv.x, 1e-5f);
            Assert.AreEqual(0f, uv.y, 1e-5f);
            Assert.AreEqual(0.5f, uv.width, 1e-5f);
            Assert.AreEqual(1f, uv.height, 1e-5f);
        }

        [Test]
        public void LookUpBattery_FormatsPercentageAndStatusColor()
        {
            Assert.AreEqual("", SettingsSummoner.FormatBatteryText(-1f, BatteryStatus.Unknown));
            Assert.AreEqual("74%", SettingsSummoner.FormatBatteryText(0.74f, BatteryStatus.Discharging));
            Assert.AreEqual("100%", SettingsSummoner.FormatBatteryText(1f, BatteryStatus.Full));

            var chargingCol = SettingsSummoner.GetBatteryColor(0.50f, BatteryStatus.Charging);
            Assert.Greater(chargingCol.g, chargingCol.r);
            Assert.AreEqual(CardboardXR.UI.M3Theme.Error, SettingsSummoner.GetBatteryColor(0.15f, BatteryStatus.Discharging));
        }
    }
}
