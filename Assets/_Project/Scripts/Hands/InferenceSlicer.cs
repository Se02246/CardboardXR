using System;
using System.Diagnostics;
using Unity.InferenceEngine;
using UnityEngine;

namespace CardboardXR.Hands
{
    // Time-slices Unity Inference Engine CPU worker graphs across frames so neural network execution
    // never blocks the Unity main thread with 15-35 ms single-frame hitches on mobile.
    public static class InferenceSlicer
    {
        public static async Awaitable<bool> RunSlicedAsync(Worker worker, Tensor input, double budgetMsPerFrame,
            Func<bool> isRunning)
        {
            if (worker == null || input == null)
                return false;

            var schedule = worker.ScheduleIterable(input);
            var watch = Stopwatch.StartNew();
            var steps = 0;

            while (schedule.MoveNext())
            {
                if (isRunning != null && !isRunning())
                    return false;
                steps++;
                if (steps >= 4 && watch.Elapsed.TotalMilliseconds >= budgetMsPerFrame)
                {
                    await Awaitable.NextFrameAsync();
                    if (isRunning != null && !isRunning())
                        return false;
                    watch.Restart();
                    steps = 0;
                }
            }

            return isRunning == null || isRunning();
        }
    }
}
