﻿using System.Collections;
using Chroma.Colorizer;
using Chroma.Colorizer.Monobehaviours;
using HarmonyLib;
using JetBrains.Annotations;
using UnityEngine;

namespace Chroma.HarmonyPatches.Colorizer.Initialize
{
    [HarmonyPatch(typeof(ParticleSystemEventEffect))]
    [HarmonyPatch("Start")]
    internal static class ParticleSystemEventEffectStart
    {
        [UsedImplicitly]
        private static void Postfix(ParticleSystemEventEffect __instance, BeatmapEventType ____colorEvent)
        {
            // If duplicated, clean up before duping
            ChromaParticleEventController oldController = __instance.GetComponent<ChromaParticleEventController>();
            if (oldController != null)
            {
                Object.Destroy(oldController);
            }

            __instance.StartCoroutine(WaitThenStart(__instance, ____colorEvent));
        }

        private static IEnumerator WaitThenStart(ParticleSystemEventEffect instance, BeatmapEventType eventType)
        {
            yield return new WaitForEndOfFrame();
            instance.gameObject.AddComponent<ChromaParticleEventController>().Init(instance, eventType);
        }
    }

    [HarmonyPatch(typeof(ParticleSystemEventEffect))]
    [HarmonyPatch("HandleBeatmapObjectCallbackControllerBeatmapEventDidTrigger")]
    internal static class ParticleSystemEventEffectSetLastEvent
    {
        [UsedImplicitly]
        private static void Prefix(BeatmapEventData beatmapEventData, BeatmapEventType ____colorEvent)
        {
            if (beatmapEventData.type == ____colorEvent)
            {
                ____colorEvent.GetParticleColorizers().ForEach(n => n.PreviousValue = beatmapEventData.value);
            }
        }
    }
}