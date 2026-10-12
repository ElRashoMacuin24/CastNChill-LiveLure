using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Il2CppInterop.Runtime;
using UnityEngine;

namespace LiveLure
{
    /// <summary>
    /// Topwater lures, fished like a popper.
    ///
    /// The base game gives this lure no water drag at all and couples it rigidly to the rod, so with a rod you can move
    /// by hand one twitch sent it skating a rod's length at several times reeling speed, up out of the water, and it
    /// kept gliding. A real popper is worked on a slack line with the rod tip: a snap of the tip moves it a few inches,
    /// its cupped face throws a spit of water with a "bloop", and it stops dead and sits.
    ///
    /// So for a topwater lure:
    ///   - only part of the rod tip's movement reaches it (the rest is taken up as a slack line and a bending rod
    ///     would), which is what gives small twitches small pops;
    ///   - the water brakes it hard the moment the line stops pulling;
    ///   - it stays in the surface film however steeply the line pulls, until it is close enough to lift out;
    ///   - each time it is jerked into motion from rest it pops: a splash and a sound sized to how hard it was popped,
    ///     and that pop is what calls fish.
    /// </summary>
    static class Popper
    {
        // The game's FMOD wrapper can start a sound but has no way to make one quieter; the library itself does.
        [DllImport("fmodstudio", EntryPoint = "FMOD_Studio_EventInstance_SetVolume")] static extern int Fmod_SetVolume(IntPtr instance, float volume);
        [DllImport("fmodstudio", EntryPoint = "FMOD_Studio_EventInstance_SetPitch")] static extern int Fmod_SetPitch(IntPtr instance, float pitch);
        [DllImport("fmodstudio", EntryPoint = "FMOD_Studio_EventInstance_Start")] static extern int Fmod_Start(IntPtr instance);
        [DllImport("fmodstudio", EntryPoint = "FMOD_Studio_EventInstance_Release")] static extern int Fmod_Release(IntPtr instance);

        /// <summary>Speed toward the rod (px/s) that starts a pop from rest, the speed it must drop below to be at rest again, and the speed of a full-strength pop.</summary>
        const float PopStart = 9f, PopRest = 4f, PopFull = 60f;
        const float SurfaceY = 0.5f;
        /// <summary>
        /// The game's splash effect does not draw where it is put: measured from screenshots, its water appears 14.5
        /// game pixels to the left of the object's position, at any size, and the game's own lure-landing splash shows
        /// the same shift (there it passes for the lure having moved on). A pop has to be at the lure, so it is put
        /// that much to the right.
        /// </summary>
        const float SplashArtShift = 14.5f;
        /// <summary>How far in front of the lure's middle (toward the rod) its cupped face is, where the water flies from.</summary>
        const float NoseOffset = 4f;
        const int PoolSize = 6;

        // ---- for the log and the self-test ----
        public static int Pops, SplashesShown, SoundsPlayed;
        public static float LastStrength, LastTravel;
        /// <summary>How much further the raised rod has put its tip from the lure than it would be at rest (px).</summary>
        public static float LastLift;
        public static string LastFault = "";

        static readonly System.Random s_random = new System.Random();
        static readonly Dictionary<IntPtr, GameObject[]> s_pools = new Dictionary<IntPtr, GameObject[]>();
        static readonly Dictionary<IntPtr, int> s_next = new Dictionary<IntPtr, int>();
        static GameObject s_holder;
        static Game.FishSizeData s_small, s_medium;
        static bool s_sizesLooked, s_nativeAudio = true;

        static void Fault(string what, Exception e)
        {
            LastFault = what + ": " + e.GetType().Name + " " + e.Message;
            Plugin.Error("Popper " + what, e);
        }

        /// <summary>Called every physics tick for a topwater lure a person is fishing, in place of the plain float.</summary>
        public static void Tick(LureCtl ctl, ref Sample s, float dt)
        {
            Game.Rod rod = ctl.Rod;
            Rigidbody2D body = ctl.Body;
            Vector3 tip = rod.TipPos;
            float toRod = tip.x >= s.px ? 1f : -1f;
            bool atBoat = Math.Abs(tip.x - s.px) < Settings.PopLiftOut.Value;

            // ---- in the surface film, and braked by it ----
            Vector2 v = body.linearVelocity;
            if (Settings.TopwaterFloat.Value)
            {
                float mass = body.mass;
                Vector2 force = default;
                force.y = Math.Clamp(14f * (SurfaceY - s.py) - 5f * v.y, -60f, 90f) * mass;
                if (s.py < 3f) force.x = -Math.Max(0f, Settings.PopBrake.Value) * v.x * mass;
                body.AddForce(force);

                // The line pulls up toward the rod tip as well as in. Out in the water that must not lift a floating
                // lure clear: it ploughs along the surface instead. Only at the boat can the rod pick it up.
                if (!atBoat && s.py > SurfaceY - 1f)
                {
                    float rise = (SurfaceY - s.py) * 12f; // may settle up to the surface from just under it, never climb past it
                    if (v.y > rise)
                    {
                        v.y = rise;
                        body.linearVelocity = v;
                    }
                }
            }

            // ---- only part of what the rod tip does reaches the lure ----
            if (ctl.Mine)
            {
                float give = 0f;
                float applied = RodPose.Applied;
                if (Math.Abs(applied) > 0.05f)
                {
                    // Where the tip would be with the rod at rest. The rod is turned in its parent's space, and that
                    // parent is mirrored when the angler faces the other way, so the turn is undone there, not in the world.
                    Transform rodTf = rod.transform, parent = rodTf.parent;
                    Vector3 local = parent != null ? parent.InverseTransformPoint(tip) : tip;
                    Vector3 pivot = parent != null ? rodTf.localPosition : rodTf.position;
                    float ox = local.x - pivot.x, oy = local.y - pivot.y;
                    float a = -applied * 0.0174533f, cos = MathF.Cos(a), sin = MathF.Sin(a);
                    Vector3 restLocal = default;
                    restLocal.x = pivot.x + ox * cos - oy * sin;
                    restLocal.y = pivot.y + ox * sin + oy * cos;
                    restLocal.z = local.z;
                    Vector3 restTip = parent != null ? parent.TransformPoint(restLocal) : restLocal;
                    float now = MathF.Sqrt((tip.x - s.px) * (tip.x - s.px) + (tip.y - s.py) * (tip.y - s.py));
                    float rest = MathF.Sqrt((restTip.x - s.px) * (restTip.x - s.px) + (restTip.y - s.py) * (restTip.y - s.py));
                    give = (1f - Math.Clamp(Settings.PopTravel.Value, 0.05f, 1f)) * (now - rest);
                    LastLift = now - rest;
                }
                float change = give - ctl.PopGive;
                if (Math.Abs(change) > 0.0001f)
                {
                    rod.LineLength += change;
                    ctl.PopGive = give;
                }
            }

            // ---- the pop ----
            float speed = toRod * v.x;
            float now2 = s.now;
            if (!ctl.PopBusy)
            {
                if (!ctl.PopArmed && speed < PopRest) ctl.PopArmed = true;
                if (ctl.PopArmed && speed >= PopStart && s.inWater)
                {
                    ctl.PopBusy = true; ctl.PopArmed = false; ctl.PopFired = false;
                    ctl.PopT0 = now2; ctl.PopPeak = speed; ctl.PopX0 = s.px;
                }
            }
            else
            {
                if (speed > ctl.PopPeak) ctl.PopPeak = speed;
                // two ticks in, the lure has shown how hard it was popped: that is when the water flies
                if (!ctl.PopFired && now2 - ctl.PopT0 >= 0.04f)
                {
                    ctl.PopFired = true;
                    float strength = Math.Clamp((ctl.PopPeak - PopStart) / (PopFull - PopStart), 0.05f, 1f);
                    s.pop = strength;
                    Pops++;
                    LastStrength = strength;
                    ctl.PopCount++;
                    Splash(ctl, s.px + toRod * NoseOffset, strength);
                }
                if (ctl.PopFired && (speed < PopRest || now2 - ctl.PopT0 > 0.8f))
                {
                    ctl.PopBusy = false;
                    LastTravel = Math.Abs(s.px - ctl.PopX0);
                    Tel.Event(ctl, string.Format("pop: strength {0:0.00} (peak {1:0} px/s), moved {2:0.0} px, rod {3:0} deg", LastStrength, ctl.PopPeak, LastTravel, RodPose.Current));
                }
            }
        }

        /// <summary>The spit of water and the bloop, both sized to the pop.</summary>
        public static void Splash(LureCtl ctl, float x, float strength)
        {
            Game.Lure lure = ctl.Lure;
            try
            {
                Game.ParticleEffect look = Effect(ctl.Data, Settings.PopSplash.Value);
                if (look != null)
                {
                    float size = Math.Max(0.05f, Settings.PopSplashSize.Value) * (0.45f + 0.55f * strength);
                    Show(look, x, lure.transform.position.z, size);
                }
            }
            catch (Exception e) { Fault("splash", e); }

            try
            {
                Game.ParticleEffect voice = Effect(ctl.Data, Settings.PopSound.Value);
                if (voice != null)
                {
                    float volume = Math.Max(0f, Settings.PopVolume.Value) * (0.3f + 0.7f * strength);
                    float pitch = 1.28f - 0.3f * strength + 0.1f * ((float)s_random.NextDouble() - 0.5f); // small pops are higher, and no two are quite alike
                    Play(lure, voice.audioEvent, volume, pitch);
                }
            }
            catch (Exception e) { Fault("sound", e); }

            if (strength > 0.75f)
            {
                // a hard chug drives air under as well
                try
                {
                    Vector3 at = default;
                    at.x = x; at.y = -1f; at.z = lure.transform.position.z;
                    Game.BubbleSpawner bubbles = Game.BubbleSpawner.Instance;
                    if (bubbles != null) bubbles.Burst(at);
                }
                catch (Exception e) { Fault("bubbles", e); }
            }
        }

        /// <summary>For the self-test: show one named effect at a given size, with its sound, and describe it.</summary>
        public static string Preview(LureCtl ctl, string which, float size, float volume, float dx = 0f)
        {
            Game.ParticleEffect effect = Effect(ctl.Data, which);
            if (effect == null) return which + ": not found";
            Game.Lure lure = ctl.Lure;
            Vector3 p = lure.transform.position;
            Show(effect, p.x + dx, p.z, size);
            if (volume > 0f) Play(lure, effect.audioEvent, volume, 1f);
            var systems = effect.particleSystems;
            return string.Format("{0}: \"{1}\", {2} particle system(s), snaps to surface={3}, has a sound={4}",
                which, effect.name, systems != null ? systems.Length : 0, effect.snapToSurface, !effect.audioEvent.IsNull);
        }

        /// <summary>One of the game's own splash effects, by the name used in the config.</summary>
        public static Game.ParticleEffect Effect(Game.LureData data, string which)
        {
            switch ((which ?? "").Trim().ToLowerInvariant())
            {
                case "lure": return data != null ? data.splashEffect : null;
                case "fish-s": FindSizes(); return s_small != null ? s_small.splashEffect : null;
                case "fish-m": FindSizes(); return s_medium != null ? s_medium.splashEffect : null;
                default: return null;
            }
        }

        static void FindSizes()
        {
            if (s_sizesLooked) return;
            s_sizesLooked = true;
            foreach (var o in Resources.FindObjectsOfTypeAll(Il2CppType.Of<Game.FishSizeData>()))
            {
                var size = o.TryCast<Game.FishSizeData>();
                if (size == null || size.splashEffect == null) continue;
                string id = size.id;
                if (id == "s" && s_small == null) s_small = size;
                else if (id == "m" && s_medium == null) s_medium = size;
            }
        }

        /// <summary>
        /// Shows a splash at a point on the surface, at a chosen size. The game's effect objects play their own sound
        /// at full volume and return themselves to the game's pool, so these are private copies with that part taken
        /// off, kept in a small ring and replayed.
        /// </summary>
        static void Show(Game.ParticleEffect source, float x, float z, float size)
        {
            IntPtr key = source.Pointer;
            if (!s_pools.TryGetValue(key, out GameObject[] ring))
            {
                ring = new GameObject[PoolSize];
                s_pools[key] = ring;
                s_next[key] = 0;
            }
            int slot = s_next[key];
            s_next[key] = (slot + 1) % PoolSize;

            GameObject go = ring[slot];
            if (go == null)
            {
                go = Copy(source);
                ring[slot] = go;
                if (go == null) return;
            }

            go.SetActive(false);
            Transform tf = go.transform;
            Vector3 at = default;
            at.x = x + SplashArtShift; at.y = 0f; at.z = z; // the water's surface is y = 0, as the game's own splashes assume
            tf.position = at;
            Vector3 scale = default;
            scale.x = size; scale.y = size; scale.z = 1f;
            tf.localScale = scale;
            go.SetActive(true);
            SplashesShown++;
            LastShown = go;
        }

        /// <summary>The splash object most recently shown, for the self-test to inspect.</summary>
        public static GameObject LastShown;

        /// <summary>For the self-test: is the last splash actually alive and drawing, and where?</summary>
        public static string Inspect(Game.Lure lure)
        {
            GameObject go = LastShown;
            if (go == null) return "no splash object";
            var sb = new System.Text.StringBuilder();
            Vector3 p = go.transform.position, sc = go.transform.lossyScale, lp = lure.transform.position;
            sb.AppendFormat("active={0} at ({1:0.0},{2:0.0},{3:0.0}) scale {4:0.00}, lure at ({5:0.0},{6:0.0},{7:0.0}), layer {8} (lure layer {9})",
                go.activeInHierarchy, p.x, p.y, p.z, sc.x, lp.x, lp.y, lp.z, go.layer, lure.gameObject.layer);
            foreach (ParticleSystem ps in go.GetComponentsInChildren<ParticleSystem>(true))
            {
                string order = "?";
                try
                {
                    var r = ps.GetComponent<ParticleSystemRenderer>();
                    if (r != null) order = r.sortingLayerName + "/" + r.sortingOrder + (r.enabled ? "" : " (renderer off)");
                }
                catch (Exception e) { order = e.GetType().Name; }
                sb.AppendFormat("; system \"{0}\": playing={1} particles={2} sorting {3}", ps.name, ps.isPlaying, ps.particleCount, order);
            }
            try
            {
                foreach (Camera cam in Camera.allCameras)
                {
                    Vector3 sp = cam.WorldToScreenPoint(lp);
                    sb.AppendFormat("; camera \"{0}\" puts the lure at screen ({1:0},{2:0}) of {3}x{4}", cam.name, sp.x, sp.y, cam.pixelWidth, cam.pixelHeight);
                }
            }
            catch (Exception e) { sb.Append("; cameras: " + e.GetType().Name); }
            return sb.ToString();
        }

        static GameObject Copy(Game.ParticleEffect source)
        {
            if (s_holder == null)
            {
                s_holder = new GameObject("LiveLurePopSplashes");
                s_holder.SetActive(false); // anything made under it is born switched off, so the game's effect script never runs on a copy
                UnityEngine.Object.DontDestroyOnLoad(s_holder);
            }
            UnityEngine.Object made = UnityEngine.Object.Instantiate(source.gameObject, s_holder.transform);
            GameObject go = made != null ? made.TryCast<GameObject>() : null;
            if (go == null) return null;
            Game.ParticleEffect script = go.GetComponent<Game.ParticleEffect>();
            if (script != null) UnityEngine.Object.Destroy(script);
            go.SetActive(false);
            go.transform.SetParent(null, false);
            UnityEngine.Object.DontDestroyOnLoad(go);
            return go;
        }

        /// <summary>Plays one of the game's sounds at the lure, at a chosen loudness and pitch.</summary>
        static void Play(Game.Lure lure, FMODUnity.EventReference sound, float volume, float pitch)
        {
            if (sound.IsNull || volume <= 0.001f) return;
            FMOD.Studio.EventInstance instance = Game.AudioExtensions.CreateInstance(sound, lure.gameObject, false, false);
            IntPtr handle = instance.handle;
            if (handle == IntPtr.Zero) return;
            if (s_nativeAudio)
            {
                try
                {
                    Fmod_SetVolume(handle, volume);
                    Fmod_SetPitch(handle, pitch);
                    Fmod_Start(handle);
                    Fmod_Release(handle);
                    SoundsPlayed++;
                    return;
                }
                catch (Exception e)
                {
                    s_nativeAudio = false; // library not reachable by name: fall back to the game's wrapper from now on
                    Fault("native audio", e);
                }
            }
            instance.setVolume(volume);
            instance.setPitch(pitch);
            instance.start();
            instance.release();
            SoundsPlayed++;
        }
    }
}
