using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using UnityEngine;
using HookResult = Game.Fish.ChaseState.HookResult;

namespace LiveLure
{
    /// <summary>
    /// Live Lure for Cast n Chill: hold the rod with the mouse, swing it to cast, work the lure the way that lure is
    /// really fished, and jerk the rod to set the hook.
    /// </summary>
    [BepInPlugin(Guid, "Live Lure", Version)]
    public class Plugin : BasePlugin
    {
        public const string Guid = "mike.castnchill.livelure";
        public const string Version = "0.8.0";

        internal static ManualLogSource Logger;
        static readonly Dictionary<string, int> s_errorCounts = new Dictionary<string, int>();

        public override void Load()
        {
            Logger = Log;
            Settings.Bind(Config);

            var harmony = new Harmony(Guid);
            int ok = 0;
            ok += Patch(harmony, typeof(Game.Lure), "FixedUpdate", postfix: nameof(Patches.Lure_FixedUpdate));
            ok += Patch(harmony, typeof(Game.Lure), "AttractFish", prefix: nameof(Patches.AttractFish_Pre), postfix: nameof(Patches.AttractFish_Post));
            ok += Patch(harmony, typeof(Game.LureData), "CanTargetSpawn", prefix: nameof(Patches.CanTargetSpawn_Pre));
            ok += Patch(harmony, typeof(Game.Fish.ChaseState), "TryHooking", postfix: nameof(Patches.TryHooking_Post));
            ok += Patch(harmony, typeof(Game.GUICursor), "UpdateButtons", prefix: nameof(Patches.CursorButtons_Pre));
            ok += Patch(harmony, typeof(Game.Angler.RodAimingState), "Update", prefix: nameof(Patches.Aiming_Pre));
            ok += Patch(harmony, typeof(Game.Rod), "LateUpdate", prefix: nameof(Patches.RodLateUpdate_Pre));
            ok += Patch(harmony, typeof(Game.JigAnim), "get_ShouldActivate", prefix: nameof(Patches.JigShouldActivate_Pre));
            ok += Patch(harmony, typeof(Game.Angler.RodFishingState), "Update", postfix: nameof(Patches.RodFishing_Post));
            ok += Patch(harmony, typeof(Game.Lure), "AddRodRetrieveForce", prefix: nameof(Patches.RetrieveForce_Pre), postfix: nameof(Patches.RetrieveForce_Post));
            ok += Patch(harmony, typeof(Game.Rod), "UpdateLineForce", prefix: nameof(Patches.LineForce_Pre), postfix: nameof(Patches.LineForce_Post));
            ok += Patch(harmony, typeof(Game.LocationObjectSpawn), "FindAttractedFish", prefix: nameof(Patches.FindAttracted_Pre), postfix: nameof(Patches.FindAttracted_Post));
            SelfTest.Init(harmony);

            AddComponent<LiveLureBehaviour>();
            Log.LogMessage(string.Format("Live Lure {0} loaded ({1}/12 patches){2}", Version, ok,
                Settings.SelfTest.Value ? " - SELF TEST MODE, saving disabled" : ""));
        }

        internal static int Patch(Harmony harmony, Type type, string method, string prefix = null, string postfix = null, Type patchClass = null)
        {
            try
            {
                patchClass ??= typeof(Patches);
                MethodInfo original = AccessTools.Method(type, method);
                if (original == null) throw new MissingMethodException(type.FullName, method);
                harmony.Patch(original,
                    prefix != null ? new HarmonyMethod(AccessTools.Method(patchClass, prefix)) : null,
                    postfix != null ? new HarmonyMethod(AccessTools.Method(patchClass, postfix)) : null);
                return 1;
            }
            catch (Exception e)
            {
                Logger.LogError("Could not patch " + type.Name + "." + method + ": " + e);
                return 0;
            }
        }

        /// <summary>Logs the first few failures per call site, so a broken hook cannot flood the log every physics tick.</summary>
        internal static void Error(string where, Exception e)
        {
            s_errorCounts.TryGetValue(where, out int n);
            s_errorCounts[where] = n + 1;
            if (n < 3) Logger.LogError(where + ": " + e);
            else if (n == 3) Logger.LogError(where + ": further errors suppressed");
        }

        /// <summary>A rod-handling event worth having in the tuning log.</summary>
        internal static void Note(string text)
        {
            if (Settings.Telemetry.Value) Logger.LogInfo("[rod] " + text);
        }
    }

    public class LiveLureBehaviour : MonoBehaviour
    {
        public LiveLureBehaviour(IntPtr ptr) : base(ptr) { }

        // Order matters: scripted input (if any) is set up, the hands are read, then the cast and the rod act on them.
        void Update()
        {
            try { SelfTest.Update(); } catch (Exception e) { Plugin.Error("SelfTest", e); }
            try { RodInput.Update(); } catch (Exception e) { Plugin.Error("RodInput", e); }
            try { Casting.Update(); } catch (Exception e) { Plugin.Error("Casting", e); }
            try { RodPose.Update(); } catch (Exception e) { Plugin.Error("RodPose", e); }
            try { Fight.Update(); } catch (Exception e) { Plugin.Error("Fight", e); }
            try { Hud.Update(); } catch (Exception e) { Plugin.Error("Hud", e); }
        }
    }

    static class Patches
    {
        const float ForceShare = 0.35f;

        public static void Lure_FixedUpdate(Game.Lure __instance)
        {
            try { LureController.Tick(__instance); }
            catch (Exception e) { Plugin.Error("Lure tick", e); }
        }

        public static void AttractFish_Pre(Game.Lure __instance)
        {
            try { Gate.BlockAttract = SelfTest.BlockAllAttraction || (!Gate.Pulse && LureController.IsGated(__instance)); }
            catch (Exception e) { Gate.BlockAttract = false; Plugin.Error("AttractFish", e); }
        }

        public static void AttractFish_Post()
        {
            Gate.BlockAttract = false;
        }

        // Lure.AttractFish asks this for every fish spawn before attracting from it. Saying no sends the game down
        // its "scare off the wrong fish" branch instead, which is the part of the vanilla tick we want to keep.
        public static bool CanTargetSpawn_Pre(ref bool __result)
        {
            if (!Gate.BlockAttract) return true;
            __result = false;
            return false;
        }

        public static void TryHooking_Post(Game.Fish.ChaseState __instance, ref HookResult __result)
        {
            try
            {
                if (!Settings.Enabled.Value) return;
                Game.Fish fish = __instance.Fish;
                if (fish == null) return;
                Game.Lure lure = fish.Lure;
                if (lure == null) return;
                if (SelfTest.ForceNibble) { SelfTest.ForceNibble = false; __result = HookResult.Nibbled; }
                LureController.OnStrike(fish, lure, ref __result);
            }
            catch (Exception e) { Plugin.Error("TryHooking", e); }
        }

        // The game polls the mouse buttons here and routes clicks by which screen zone the cursor is over. While
        // the rod is gripped those clicks are ours: the grip button must not let line out, and left-click just reels.
        public static bool CursorButtons_Pre(Game.GUICursor __instance)
        {
            try
            {
                Game.Angler angler = __instance.angler;
                if (__instance.anglerIndex == 0) RodInput.Angler = angler;
                Game.Angler mine = RodInput.Angler;
                if (angler == null || mine == null || angler.Pointer != mine.Pointer) return true;
                return !RodInput.OwnsButtons;
            }
            catch (Exception e) { Plugin.Error("Cursor buttons", e); return true; }
        }

        public static bool Aiming_Pre(Game.Angler.RodAimingState __instance)
        {
            try { return Casting.AimingUpdate(__instance); }
            catch (Exception e) { Plugin.Error("Aiming", e); return true; }
        }

        public static void RodLateUpdate_Pre(Game.Rod __instance)
        {
            try { RodPose.Apply(__instance); }
            catch (Exception e) { Plugin.Error("Rod pose", e); }
        }

        // The game pulls the lure toward the rod tip here, but never lets it go faster than the lure's retrieveMaxSpeed,
        // a figure chosen for reeling. A rod ripped upward moves its tip many times faster than that, so for the
        // length of this one call the limit is raised in proportion to how hard the player is yanking, then put back.
        // The pull itself gets a smaller share of the same boost. Measured in game: with none, a quick jerk is over
        // before the lure has covered the distance the rod tip moved; with the full boost the lure overshoots the tip
        // by half again and tops 350 px/s. About a third of it has the lure travel roughly as far as the tip did.
        public static void RetrieveForce_Pre(Game.Lure __instance, ref float force, out float __state)
        {
            __state = -1f;
            try
            {
                float boost = LureController.PullBoost(__instance);
                if (boost <= 1.001f) return;
                Game.LureData data = __instance.Data;
                if (data == null) return;
                __state = LureController.BaseMaxSpeed(data);
                data.retrieveMaxSpeed = __state * boost;
                force *= 1f + ForceShare * (boost - 1f);
            }
            catch (Exception e) { Plugin.Error("Yank boost", e); }
        }

        // The game models the reel's drag: once line tension passes a threshold, part of the stretch slips off the
        // spool every tick. That is right for a running fish, but a bare lure cannot pull drag, and with a movable
        // rod it meant a hard yank spiked the tension, the reel gave line, and the lure barely moved. So while the
        // player holds the rod and nothing is hooked, any line the drag let out during this update is taken back.
        public static void LineForce_Pre(Game.Rod __instance, out float __state)
        {
            __state = -1f;
            try { Fight.BeforeLineForce(__instance); } // with a fish on, the drag is the player's (see Fight.cs)
            catch (Exception e) { Plugin.Error("Drag", e); }
            try
            {
                if (!Settings.Enabled.Value || !RodInput.HoldingRod) return;
                Game.Angler mine = RodInput.Angler;
                if (mine == null) return;
                Game.Rod rod = mine.rod;
                if (rod == null || rod.Pointer != __instance.Pointer) return;
                Game.Lure lure = __instance.Lure;
                if (lure == null || lure.State != Game.Lure.LureState.Fishing) return;
                __state = __instance.LineLength;
            }
            catch (Exception e) { Plugin.Error("Drag hold", e); }
        }

        public static void LineForce_Post(Game.Rod __instance, float __state)
        {
            try { Fight.AfterLineForce(__instance); }
            catch (Exception e) { Plugin.Error("Drag restore", e); }
            if (__state < 0f) return;
            try
            {
                // With nothing hooked, the reel idle or winding and the bail shut, drag slip is the only thing that lengthens the line.
                if (__instance.LineLength <= __state || __instance.Spooling || __instance.RetrievePower < 0f) return;
                Game.Lure lure = __instance.Lure;
                if (lure != null && lure.State == Game.Lure.LureState.Fishing) __instance.LineLength = __state;
            }
            catch (Exception e) { Plugin.Error("Drag hold restore", e); }
        }

        // Lure.AttractFish asks each fish spawn for a fish within sight of the lure. Sight is the species' vision times
        // a figure on the lure, so for the length of this one call that figure is scaled by how this kind of fish takes
        // the way the lure is moving: lively brings aggressive fish from further, calm brings the shy ones, and noise
        // carries to fish with good hearing.
        public static void FindAttracted_Pre(Game.LocationObjectSpawn __instance, Game.LureData lureData, out float __state)
        {
            __state = -1f;
            try
            {
                LureCtl ctl = Gate.Ctl;
                if (ctl == null || !Gate.Pulse || lureData == null) return;
                float notice = FishSense.Notice(ctl, __instance);
                if (MathF.Abs(notice - 1f) < 0.01f) return;
                __state = lureData.visualMultiplier;
                lureData.visualMultiplier = __state * notice;
            }
            catch (Exception e) { Plugin.Error("Fish notice", e); }
        }

        public static void FindAttracted_Post(Game.LureData lureData, float __state)
        {
            if (__state < 0f) return;
            try { lureData.visualMultiplier = __state; }
            catch (Exception e) { Plugin.Error("Fish notice restore", e); }
        }

        public static void RetrieveForce_Post(Game.Lure __instance, float __state)
        {
            if (__state < 0f) return;
            try { __instance.Data.retrieveMaxSpeed = __state; }
            catch (Exception e) { Plugin.Error("Yank boost restore", e); }
        }

        // The base game reels at full speed whenever the lure is out of the water, to fetch back one left dangling.
        // With the rod in hand that is the player lifting the lure clear on purpose, so put the reel back to what
        // they are actually asking for. Once the rod is let go and settled, the base behaviour returns.
        public static void RodFishing_Post(Game.Angler.RodFishingState __instance)
        {
            try
            {
                if (!Settings.Enabled.Value || !RodInput.HoldingRod) return;
                Game.Angler angler = __instance.Angler, mine = RodInput.Angler;
                if (angler == null || mine == null || angler.Pointer != mine.Pointer) return;
                Game.Rod rod = angler.rod;
                Game.Lure lure = rod != null ? rod.Lure : null;
                if (lure == null || lure.State != Game.Lure.LureState.Fishing || !lure.IsOutOfWater) return;
                rod.SetRetrievePower(angler.InteractInput);
            }
            catch (Exception e) { Plugin.Error("Rod fishing", e); }
        }

        // The base game plays a canned jig animation on every reel click. With the rod in hand the player does the
        // jigging, so the canned one would only fight the mouse.
        public static bool JigShouldActivate_Pre(Game.JigAnim __instance, ref bool __result)
        {
            try
            {
                if (RodInput.Current != RodInput.Mode.Rod) return true;
                Game.Angler mine = RodInput.Angler, angler = __instance.Angler;
                if (mine == null || angler == null || angler.Pointer != mine.Pointer) return true;
                __result = false;
                return false;
            }
            catch (Exception e) { Plugin.Error("Jig anim", e); return true; }
        }
    }
}
