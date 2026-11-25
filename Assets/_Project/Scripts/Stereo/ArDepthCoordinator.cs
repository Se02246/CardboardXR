using System.Collections.Generic;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

namespace CardboardXR.Stereo
{
    // Coordinates AROcclusionManager ownership across independent consumers (HandTracker,
    // SpatialSurfaceLocator) so releasing one consumer never disables environment depth
    // while another still relies on it.
    public static class ArDepthCoordinator
    {
        struct Lease
        {
            public AROcclusionManager Manager;
            public string Owner;
            public bool PreferSmoothing;
        }

        static readonly List<Lease> s_Leases = new();

        public static void SetRequested(AROcclusionManager occlusion, string owner, bool needed,
            bool preferTemporalSmoothing = true)
        {
            PruneNullManagers();
            if (occlusion == null || string.IsNullOrEmpty(owner))
                return;

            var existingIndex = FindLeaseIndex(occlusion, owner);
            if (needed)
            {
                if (existingIndex >= 0)
                {
                    var lease = s_Leases[existingIndex];
                    lease.PreferSmoothing = preferTemporalSmoothing;
                    s_Leases[existingIndex] = lease;
                }
                else
                {
                    s_Leases.Add(new Lease
                    {
                        Manager = occlusion,
                        Owner = owner,
                        PreferSmoothing = preferTemporalSmoothing,
                    });
                }
            }
            else if (existingIndex >= 0)
            {
                s_Leases.RemoveAt(existingIndex);
            }

            ApplyState(occlusion);
        }

        public static void ReleaseAll(AROcclusionManager occlusion)
        {
            PruneNullManagers();
            if (occlusion == null)
                return;
            for (var i = s_Leases.Count - 1; i >= 0; i--)
            {
                if (ReferenceEquals(s_Leases[i].Manager, occlusion))
                    s_Leases.RemoveAt(i);
            }
            ApplyState(occlusion);
        }

        public static int GetRequestCount(AROcclusionManager occlusion)
        {
            PruneNullManagers();
            if (occlusion == null)
                return 0;
            var count = 0;
            for (var i = 0; i < s_Leases.Count; i++)
            {
                if (ReferenceEquals(s_Leases[i].Manager, occlusion))
                    count++;
            }
            return count;
        }

        static void ApplyState(AROcclusionManager occlusion)
        {
            var count = 0;
            var anyWantsRaw = false;
            for (var i = 0; i < s_Leases.Count; i++)
            {
                if (!ReferenceEquals(s_Leases[i].Manager, occlusion))
                    continue;
                count++;
                if (!s_Leases[i].PreferSmoothing)
                    anyWantsRaw = true;
            }

            if (count > 0)
            {
                var smooth = !anyWantsRaw;
                occlusion.requestedEnvironmentDepthMode = EnvironmentDepthMode.Fastest;
                occlusion.environmentDepthTemporalSmoothingRequested = smooth;
                occlusion.requestedOcclusionPreferenceMode = OcclusionPreferenceMode.NoOcclusion;
                if (!occlusion.enabled)
                    occlusion.enabled = true;
                if (occlusion.subsystem != null)
                {
                    occlusion.requestedEnvironmentDepthMode = EnvironmentDepthMode.Fastest;
                    occlusion.environmentDepthTemporalSmoothingRequested = smooth;
                    occlusion.requestedOcclusionPreferenceMode = OcclusionPreferenceMode.NoOcclusion;
                }
            }
            else if (occlusion.enabled)
            {
                occlusion.enabled = false;
            }
        }

        static int FindLeaseIndex(AROcclusionManager occlusion, string owner)
        {
            for (var i = 0; i < s_Leases.Count; i++)
            {
                if (ReferenceEquals(s_Leases[i].Manager, occlusion) && s_Leases[i].Owner == owner)
                    return i;
            }
            return -1;
        }

        static void PruneNullManagers()
        {
            for (var i = s_Leases.Count - 1; i >= 0; i--)
            {
                if (s_Leases[i].Manager == null)
                    s_Leases.RemoveAt(i);
            }
        }
    }
}
