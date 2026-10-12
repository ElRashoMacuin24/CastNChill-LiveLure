using System;
using Il2CppInterop.Runtime;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace LiveLure
{
    /// <summary>
    /// The overlay. Its centrepiece is a rod handle on a dial: it swings exactly as the real rod does, brightens and
    /// pops when you take hold of it, its reel crank turns as you reel, and it winds back for a cast. A plate beside
    /// it carries a framed meter (the lure's action, or cast power, or line tension in a fight), what the lure wants,
    /// and which mouse button does what.
    ///
    /// With the rod idle only a one-line reminder shows, so the angler and boat are not covered until the rod is
    /// actually picked up.
    /// </summary>
    static class Hud
    {
        const float U = HudArt.Unit;
        const float MeterWidth = 360f, MeterHeight = 24f;
        const float DialX = -250f, ColumnLeft = -134f, PlateRight = 400f, RowY = 44f;
        const float TextWidth = 700f;
        const float RestLean = 40f; // degrees the rod leans toward the water at rest, as in the game

        const float FishLeft = ColumnLeft - 14f, FishBottom = RowY + 66f, FishTop = RowY + 104f;

        static bool s_failed;
        static GameObject s_root, s_meterGo, s_plateGo, s_tipGo, s_promptGo, s_fishGo, s_staminaGo;
        static RectTransform s_panel, s_dialGroup, s_handleRoot, s_crank, s_fill, s_marker, s_promptRect, s_iconRect, s_subRect, s_fishIconRect, s_staminaFill;
        static Image s_fillImage, s_handleImage, s_dialImage, s_crankImage, s_icon, s_fishIcon, s_staminaImage, s_staminaIcon;
        static Sprite s_mouseRight, s_mouseLeft;
        static TextMeshProUGUI s_title, s_hint, s_sub, s_prompt, s_fish, s_drag;
        static string s_titleText, s_hintText, s_subText, s_promptText, s_fishText, s_dragText;
        static float s_shownMeter, s_nextIdleCheck, s_crankAngle, s_pop;
        static bool s_idleHint, s_wasGripped;
        static int s_iconShown = -2;

        static Vector2 V2(float x, float y) { Vector2 v = default; v.x = x; v.y = y; return v; }
        static Vector3 V3(float x, float y, float z) { Vector3 v = default; v.x = x; v.y = y; v.z = z; return v; }
        static Color Rgba(float r, float g, float b, float a) { Color c = default; c.r = r; c.g = g; c.b = b; c.a = a; return c; }

        static string GripName()
        {
            switch ((Settings.GripButton.Value ?? "").Trim().ToLowerInvariant())
            {
                case "middlemouse": return "middle-click";
                case "mouse4": return "mouse button 4";
                case "mouse5": return "mouse button 5";
                default: return "right-click";
            }
        }

        /// <summary>The rod is in hand and idle, so a swing cast is possible: worth telling the player how.</summary>
        static bool CanSwingCast()
        {
            if (Time.unscaledTime < s_nextIdleCheck) return s_idleHint;
            s_nextIdleCheck = Time.unscaledTime + 0.25f;
            s_idleHint = false;
            try
            {
                Game.Angler angler = RodInput.Angler;
                if (angler == null || !Settings.MouseCast.Value || !RodInput.Human(angler)) return false;
                Game.Angler.AnglerState state = angler.State;
                s_idleHint = state != null && state.Pointer == angler.States.RodIdle.Pointer && angler.CanCast;
            }
            catch (Exception) { }
            return s_idleHint;
        }

        static void Show(GameObject go, bool on)
        {
            if (go != null && go.activeSelf != on) go.SetActive(on);
        }

        static float s_aside, s_asideGoal;
        /// <summary>How far sideways the HUD currently sits from its configured place, in canvas units.</summary>
        public static float Aside => s_aside;
        static Camera s_camera;

        /// <summary>Where the player's lure is on the screen, in canvas units from the bottom-left corner.</summary>
        public static bool LureOnScreen(out float x, out float y, out float width)
        {
            x = y = 0f;
            float k = 1080f / Math.Max(1, Screen.height); // the canvas is scaled to a 1080-unit-high screen
            width = Screen.width * k;
            Game.Angler angler = RodInput.Angler;
            Game.Rod rod = angler != null ? angler.rod : null;
            Game.Lure lure = rod != null ? rod.Lure : null;
            if (lure == null) return false;
            if (s_camera == null)
            {
                foreach (Camera cam in Camera.allCameras)
                    if (cam != null && cam.name == "CameraStack") { s_camera = cam; break; }
                if (s_camera == null) s_camera = Camera.main;
                if (s_camera == null) return false;
            }
            Vector3 at = s_camera.WorldToScreenPoint(lure.transform.position);
            x = at.x * k; y = at.y * k;
            return true;
        }

        /// <summary>
        /// How far sideways the HUD should sit so the lure (and the water around it, where a pop or a bite shows) stays
        /// in view. Zero unless the lure is actually behind it. It keeps a position once taken, so it does not creep
        /// about as the lure moves.
        /// </summary>
        static float StepAside(float scale)
        {
            if (!Settings.HudStepAside.Value || !LureOnScreen(out float lx, out float ly, out float width)) return s_asideGoal = 0f;
            const float MarginX = 80f, MarginY = 50f, Extra = 50f;
            float ax = Settings.HudX.Value * width, ay = Settings.HudY.Value * 1080f;
            float left = ax + (DialX - 100f) * scale, right = ax + PlateRight * scale;
            float bottom = ay + (RowY - 110f) * scale, top = ay + (FishTop + 6f) * scale;
            if (ly + MarginY < bottom || ly - MarginY > top) return s_asideGoal = 0f; // the lure is above or below the HUD's band

            bool behindHome = lx + MarginX > left && lx - MarginX < right;
            if (!behindHome) return s_asideGoal = 0f;
            // still clear where it already stands? then stay put
            if (s_asideGoal != 0f && (lx + MarginX <= left + s_asideGoal || lx - MarginX >= right + s_asideGoal)) return s_asideGoal;

            float toLeft = (lx - MarginX - Extra) - right, toRight = (lx + MarginX + Extra) - left;
            bool leftFits = left + toLeft >= 8f, rightFits = right + toRight <= width - 8f;
            if (leftFits && rightFits) s_asideGoal = -toLeft <= toRight ? toLeft : toRight;
            else if (leftFits) s_asideGoal = toLeft;
            else if (rightFits) s_asideGoal = toRight;
            else s_asideGoal = 0f; // no room either side: better where the player expects it than half off the screen
            return s_asideGoal;
        }

        public static void Update()
        {
            if (s_failed) return;
            HudState st = LureController.Hud;
            float now = Time.time, real = Time.unscaledTime, dt = Time.unscaledDeltaTime;
            bool on = Settings.ShowHud.Value && Settings.Enabled.Value && !SelfTest.HideHud;
            bool fishing = on && real - st.Stamp < 0.3f;
            bool fight = on && !fishing && real - st.FightStamp < 0.3f;
            bool casting = on && !fishing && !fight && Casting.Active;
            bool castFlash = on && real < Casting.ShowUntil;
            bool flash = on && now < st.FlashUntil;
            bool idleHint = on && !fishing && !fight && !casting && CanSwingCast();
            if (!(fishing || fight || casting || castFlash || flash || idleHint))
            {
                if (s_root != null && s_root.activeSelf) s_root.SetActive(false);
                return;
            }

            if (s_root == null)
            {
                try { Build(); }
                catch (Exception e)
                {
                    s_failed = true;
                    Plugin.Logger.LogError("HUD could not be created; continuing without it: " + e);
                    return;
                }
            }
            if (!s_root.activeSelf) s_root.SetActive(true);

            s_panel.anchorMin = s_panel.anchorMax = V2(Settings.HudX.Value, Settings.HudY.Value);
            float scale = Settings.HudScale.Value;
            s_panel.localScale = V3(scale, scale, 1f);

            // A lure on the surface sits low on the screen, right where the HUD is. The HUD steps aside rather than hide it.
            float goal = 0f;
            try { goal = (fishing || fight) ? StepAside(scale) : 0f; }
            catch (Exception) { goal = 0f; }
            s_aside += (goal - s_aside) * Math.Min(1f, dt * 7f);
            if (Math.Abs(s_aside) < 0.5f && goal == 0f) s_aside = 0f;
            s_panel.anchoredPosition = V2(s_aside, 0f);

            bool rodInUse = fishing || fight || casting;
            bool gripped = casting || ((fishing || fight) && RodInput.Current == RodInput.Mode.Rod);
            Show(s_plateGo, rodInUse);
            Show(s_tipGo, idleHint);
            Show(s_meterGo, rodInUse);

            // the fish read-out: who is following, what it wants, and whether the lure is doing it (and, in a fight, how it is going)
            bool note = (fishing || fight) && now < st.NoteUntil;
            bool fishLine = s_fish != null && Settings.FishTell.Value && (note || (fishing && real - st.FishStamp < 0.3f));
            Show(s_fishGo, fishLine);

            // ---- the rod handle ----
            if (s_dialGroup != null)
            {
                Show(s_dialGroup.gameObject, rodInUse);
                if (rodInUse)
                {
                    int dir = 1;
                    float reel = 0f;
                    try
                    {
                        Game.Angler angler = RodInput.Angler;
                        if (angler != null)
                        {
                            dir = angler.FishingDir >= 0 ? 1 : -1;
                            Game.Rod rod = angler.rod;
                            if (rod != null && (fishing || fight)) reel = rod.RetrievePower;
                        }
                    }
                    catch (Exception) { }
                    if (fight && Fight.Slipping) reel = -0.7f; // the drag is giving line: the spool runs backwards whatever the handle does

                    // degrees from upright toward the water
                    float lean = casting ? 45f - 100f * Casting.Windup : RestLean - RodPose.Current;
                    s_handleRoot.localEulerAngles = V3(0f, 0f, -lean);

                    s_crankAngle = (s_crankAngle - reel * 720f * dt) % 360f; // winds in clockwise, pays out the other way
                    s_crank.localEulerAngles = V3(0f, 0f, s_crankAngle);

                    if (gripped && !s_wasGripped) s_pop = 1f; // a little jump as the hand closes on it
                    s_pop *= MathF.Exp(-dt * 9f);
                    float size = (gripped ? 1f : 0.92f) * (1f + 0.14f * s_pop);
                    s_dialGroup.localScale = V3(dir * size, size, 1f);

                    // dimmer while it is just lying there, full colour in the hand
                    Color tint = gripped ? Rgba(1f, 1f, 1f, 1f) : Rgba(0.72f, 0.74f, 0.82f, 1f);
                    s_handleImage.color = tint; s_crankImage.color = tint;
                    s_dialImage.color = gripped ? Rgba(1f, 1f, 1f, 1f) : Rgba(0.78f, 0.8f, 0.88f, 1f);
                }
            }
            s_wasGripped = gripped;

            // ---- the meter ----
            if (rodInUse)
            {
                float meter = fishing ? st.Quality : fight ? st.Tension : Casting.Load;
                // ease the action meter so a scored stroke reads as a fill, not a jump; the others track directly
                s_shownMeter = fishing ? s_shownMeter + (meter - s_shownMeter) * Math.Min(1f, dt * 10f) : meter;
                float q = Math.Clamp(s_shownMeter, 0f, 1f);
                s_fill.anchorMax = V2(q, 1f);
                bool straining = fight && Fight.Strain > 0.15f && MathF.Sin(real * 30f) > 0f; // the line is about to go: the bar flashes
                s_fillImage.color = straining ? Rgba(1f, 1f, 1f, 1f)
                                  : fight ? Rgba(0.35f + 0.6f * q, 0.85f - 0.5f * q, 0.3f, 1f)             // green to red as the line loads up
                                  : casting || !st.Gated || st.Quality >= st.MinQuality ? Rgba(0.4f + 0.5f * (1f - q), 0.86f, 0.36f, 1f)
                                  : Rgba(0.85f, 0.5f, 0.32f, 1f);
                // the white tick: what the lure's action must reach while fishing, where the drag starts to give in a fight
                bool dragTick = fight && Settings.FightMechanics.Value;
                float tick = dragTick ? Math.Min(Fight.Drag, 0.985f) : st.MinQuality;
                Show(s_marker.gameObject, dragTick || (fishing && st.Gated));
                s_marker.anchorMin = V2(tick, 0f);
                s_marker.anchorMax = V2(tick, 1f);
            }

            // ---- the fish's remaining fight ----
            bool stamina = fight && Fight.Active && Settings.FightMechanics.Value;
            Show(s_staminaGo, stamina);
            if (stamina && s_staminaFill != null)
            {
                float fightLeft = Math.Clamp(Fight.Stamina, 0f, 1f);
                s_staminaFill.anchorMax = V2(fightLeft, 1f);
                float hit = Math.Clamp(1f - (real - Fight.PumpFlash) / 0.3f, 0f, 1f); // a pump that counted flashes it
                Color strong = Rgba(1f, 0.62f, 0.2f, 1f), weak = Rgba(0.5f, 0.56f, 0.7f, 1f);
                Color c = Rgba(weak.r + (strong.r - weak.r) * fightLeft, weak.g + (strong.g - weak.g) * fightLeft, weak.b + (strong.b - weak.b) * fightLeft, 1f);
                s_staminaImage.color = Rgba(c.r + (1f - c.r) * hit, c.g + (1f - c.g) * hit, c.b + (1f - c.b) * hit, 1f);
                if (s_staminaIcon != null) s_staminaIcon.color = Fight.TooBig ? Rgba(1f, 0.5f, 0.4f, 1f) : c;
            }

            if (s_title == null) return;

            // ---- words ----
            string title = fishing ? st.Title : fight ? "FISH ON" : casting ? "CAST" : "";
            bool fightOn = Settings.FightMechanics.Value;
            string hint = fishing ? (st.Gated ? st.Hint : "")
                        : fight ? (!fightOn ? "line tension  -  ease off before it snaps"
                                   : Fight.TooBig ? "too big for this tackle  -  it will break off"
                                   : "scroll: drag      jerk the rod up: wear it down")
                        : casting ? "pull back, swing toward the water, let go"
                        : "";

            // the drag setting sits beside the meter whenever there is a line out
            string drag = fightOn && (fishing || fight)
                ? "DRAG " + (int)MathF.Round(Fight.Drag * 100f) + "%" + (fight && Fight.Slipping ? "  >>>" : "")
                : "";
            if (s_drag != null)
            {
                if (drag != s_dragText) { s_dragText = drag; s_drag.text = drag; }
                s_drag.color = fight && Fight.Strain > 0.15f ? Rgba(1f, 0.55f, 0.45f, 1f)
                             : real - Fight.DragChanged < 0.8f ? Rgba(1f, 0.9f, 0.3f, 1f)
                             : fight ? Rgba(0.93f, 0.95f, 1f, 1f) : Rgba(0.62f, 0.68f, 0.82f, 1f);
            }
            int icon = -1; // 0 = grip button, 1 = left-click
            string sub = "";
            if (fishing || fight)
            {
                if (gripped) { icon = 1; sub = "reel"; }
                else
                {
                    icon = 0;
                    sub = fishing && RodInput.GripIsRightMouse && Settings.BailTap.Value
                        ? "hold: take the rod      tap: let line out"
                        : "hold: take the rod";
                }
            }
            else if (casting) { icon = 1; sub = "cancel"; }
            else if (idleHint) { icon = 0; sub = "hold, pull back, swing toward the water, let go"; }
            if (icon == 0 && !RodInput.GripIsRightMouse) { icon = -1; sub = GripName() + "  -  " + sub; }
            if (s_icon == null) icon = -1;

            if (fishLine)
            {
                string fishText = note ? st.Note : st.FishText;
                int mood = note ? st.NoteMood : st.FishFit;
                if (fishText != s_fishText) { s_fishText = fishText; s_fish.text = fishText; }
                Color tone = mood > 0 ? Rgba(0.55f, 1f, 0.55f, 1f) : mood < 0 ? Rgba(1f, 0.55f, 0.45f, 1f) : Rgba(1f, 0.9f, 0.45f, 1f);
                s_fish.color = tone;
                if (s_fishIcon != null)
                {
                    s_fishIcon.color = tone;
                    float beat = mood > 0 ? 1f + 0.12f * MathF.Sin(real * 14f) : 1f; // it is about to take: the icon comes alive
                    s_fishIconRect.localScale = V3(beat, beat, 1f);
                }
            }

            string prompt = flash ? st.Flash
                          : fishing && now < st.BiteUntil ? st.BiteText
                          : castFlash ? "CAST  " + (int)Math.Round(Casting.LastPower * 100f) + "%"
                          : fishing && RodInput.BailOpen ? "LINE OUT  -  click to stop"
                          : "";

            if (title != s_titleText) { s_titleText = title; s_title.text = title; }
            if (hint != s_hintText) { s_hintText = hint; s_hint.text = hint; }
            if (sub != s_subText) { s_subText = sub; s_sub.text = sub; }
            if (prompt != s_promptText) { s_promptText = prompt; s_prompt.text = prompt; }
            Show(s_promptGo, prompt.Length > 0);

            if (icon != s_iconShown)
            {
                s_iconShown = icon;
                if (s_icon != null)
                {
                    s_icon.gameObject.SetActive(icon >= 0);
                    if (icon >= 0) s_icon.sprite = icon == 0 ? s_mouseRight : s_mouseLeft;
                }
            }
            // The controls line sits on the plate while the rod is in use, and on its own strip when it is idle.
            float left = idleHint ? -270f : ColumnLeft, row = idleHint ? RowY - 76f : RowY - 62f;
            if (s_iconRect != null) s_iconRect.anchoredPosition = V2(left + 14f, row);
            s_subRect.anchoredPosition = V2(left + (icon >= 0 ? 42f : 0f) + TextWidth / 2f, row);

            bool bad = flash && (st.Flash.StartsWith("MISSED") || st.Flash == "TOO LATE");
            bool bite = !flash && fishing && now < st.BiteUntil;
            s_prompt.color = bad ? Rgba(1f, 0.5f, 0.4f, 1f) : flash ? Rgba(0.55f, 1f, 0.55f, 1f) : Rgba(1f, 0.9f, 0.3f, 1f);
            float throb = bite ? 1f + 0.1f * MathF.Sin(real * 26f) : 1f; // a bite should be impossible to miss
            s_promptRect.localScale = V3(throb, throb, 1f);
        }

        static void Build()
        {
            s_root = new GameObject("LiveLureHud");
            UnityEngine.Object.DontDestroyOnLoad(s_root);

            var canvas = s_root.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 30000;
            var scaler = s_root.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = V2(1920f, 1080f);
            scaler.matchWidthOrHeight = 1f;

            s_panel = Rect("Panel", s_root.transform, 0f, 0f, 0f, 0f);

            // Drawn back to front: plate, strips, dial, meter, words.
            s_plateGo = Plate("Plate", DialX, PlateRight, RowY - 88f, RowY + 62f, 0.66f);
            s_tipGo = Plate("Tip", -290f, 290f, RowY - 100f, RowY - 52f, 0.6f);
            s_fishGo = Plate("Fish", FishLeft, PlateRight, FishBottom, FishTop, 0.66f);
            // the hooked fish's remaining fight: a bar on the title row, to the right of "FISH ON"
            RectTransform strength = Rect("FishStrength", s_panel, 0f, 0f, ColumnLeft + 134f, RowY + 36f);
            s_staminaGo = strength.gameObject;
            RectTransform bar = Rect("Bar", strength, 182f, 14f, 135f, 0f);
            AddImage(bar, Rgba(0.05f, 0.05f, 0.08f, 1f));
            RectTransform barWell = Stretch("Well", bar, 3f);
            AddImage(barWell, Rgba(0.17f, 0.2f, 0.31f, 1f));
            s_staminaFill = Stretch("Fill", barWell, 0f);
            s_staminaImage = AddImage(s_staminaFill, Rgba(1f, 0.62f, 0.2f, 1f));
            s_promptRect = Rect("Prompt", s_panel, 0f, 0f, 0f, RowY + 150f);
            s_promptGo = s_promptRect.gameObject;
            Frame(Rect("Back", s_promptRect, 660f, 72f, 0f, 0f), 0.62f);

            try { BuildRod(); }
            catch (Exception e)
            {
                // The pictures are a nicety; the meter and the words still work without them.
                s_dialGroup = null; s_icon = null; s_iconRect = null; s_fishIcon = null; s_fishIconRect = null; s_staminaIcon = null;
                Plugin.Logger.LogWarning("HUD art could not be created, using the plain HUD: " + e.Message);
            }

            // meter: dark outline, inset well, fill with a lighter strip along the top
            RectTransform meter = Rect("Meter", s_panel, MeterWidth, MeterHeight, ColumnLeft + MeterWidth / 2f, RowY + 2f);
            s_meterGo = meter.gameObject;
            AddImage(meter, Rgba(0.05f, 0.05f, 0.08f, 1f));
            RectTransform well = Stretch("Well", meter, U);
            AddImage(well, Rgba(0.17f, 0.2f, 0.31f, 1f));
            s_fill = Stretch("Fill", well, 0f);
            s_fillImage = AddImage(s_fill, Rgba(0.4f, 0.85f, 0.35f, 1f));
            RectTransform gloss = Stretch("Gloss", s_fill, 0f);
            gloss.anchorMin = V2(0f, 0.62f);
            AddImage(gloss, Rgba(1f, 1f, 1f, 0.25f));
            s_marker = Rect("Threshold", well, U, 0f, 0f, 0f);
            s_marker.sizeDelta = V2(U, 0f);
            AddImage(s_marker, Rgba(1f, 1f, 1f, 0.95f));

            TMP_FontAsset font = FindFont();
            if (font == null)
            {
                Plugin.Logger.LogWarning("No TextMeshPro font found; HUD will show without text.");
                return;
            }
            float textX = ColumnLeft + TextWidth / 2f;
            s_title = AddText("Title", s_panel, font, 28f, textX, RowY + 36f, TextAlignmentOptions.Left, Rgba(1f, 1f, 1f, 1f));
            s_hint = AddText("Hint", s_panel, font, 18f, textX, RowY - 30f, TextAlignmentOptions.Left, Rgba(0.93f, 0.95f, 1f, 1f));
            s_sub = AddText("Sub", s_panel, font, 18f, textX, RowY - 62f, TextAlignmentOptions.Left, Rgba(0.76f, 0.84f, 1f, 1f));
            s_subRect = s_sub.rectTransform;
            s_prompt = AddText("Text", s_promptRect, font, 46f, 0f, 0f, TextAlignmentOptions.Center, Rgba(1f, 0.9f, 0.3f, 1f));
            s_drag = AddText("Drag", s_panel, font, 18f, ColumnLeft + MeterWidth + 16f + TextWidth / 2f, RowY + 2f, TextAlignmentOptions.Left, Rgba(0.62f, 0.68f, 0.82f, 1f));
            // on its strip, to the right of the fish icon (positions are from the strip's centre)
            float stripHalf = (PlateRight - FishLeft) / 2f;
            s_fish = AddText("FishText", s_fishGo.transform, font, 19f, -stripHalf + 76f + TextWidth / 2f, 0f, TextAlignmentOptions.Left, Rgba(1f, 0.9f, 0.45f, 1f));
            if (s_iconRect != null) s_iconRect.SetAsLastSibling(); // the icon belongs on top of the strips it sits on
        }

        static void BuildRod()
        {
            float dip = Settings.DipDegrees.Value, raise = Settings.RaiseDegrees.Value;
            Sprite dial = HudArt.Dial(RestLean + dip, RestLean - raise, RestLean);
            Sprite handle = HudArt.FromGrid(HudArt.Handle);
            Sprite crank = HudArt.FromGrid(HudArt.Crank);
            s_mouseRight = HudArt.FromGrid(HudArt.MouseRight);
            s_mouseLeft = HudArt.FromGrid(HudArt.MouseLeft);

            s_dialGroup = Rect("Rod", s_panel, 0f, 0f, DialX, RowY - 13f);
            s_dialImage = AddSprite(Rect("Dial", s_dialGroup, HudArt.DialSize * U, HudArt.DialSize * U, 0f, 0f), dial);

            s_handleRoot = Rect("HandleRoot", s_dialGroup, 0f, 0f, 0f, 0f);
            float hw = HudArt.Handle[0].Length, hh = HudArt.Handle.Length;
            RectTransform h = Rect("Handle", s_handleRoot, hw * U, hh * U, 0f, 0f);
            h.pivot = V2(HudArt.HandlePivotX / hw, 1f - HudArt.HandlePivotY / hh); // it turns about where the hand holds it
            h.anchoredPosition = V2(0f, 0f);
            s_handleImage = AddSprite(h, handle);

            float cw = HudArt.Crank[0].Length, ch = HudArt.Crank.Length;
            s_crank = Rect("Crank", s_handleRoot, cw * U, ch * U, 0f, 0f);
            s_crank.pivot = V2(HudArt.CrankPivotX / cw, 1f - HudArt.CrankPivotY / ch);
            s_crank.anchoredPosition = V2((HudArt.ReelHubX - HudArt.HandlePivotX) * U, -(HudArt.ReelHubY - HudArt.HandlePivotY) * U);
            s_crankImage = AddSprite(s_crank, crank);

            float mw = HudArt.MouseRight[0].Length, mh = HudArt.MouseRight.Length;
            s_iconRect = Rect("MouseIcon", s_panel, mw * 3f, mh * 3f, 0f, 0f);
            s_icon = AddSprite(s_iconRect, s_mouseRight);

            float fw = HudArt.Fish[0].Length, fh = HudArt.Fish.Length;
            Sprite fish = HudArt.FromGrid(HudArt.Fish);
            s_fishIconRect = Rect("FishIcon", s_fishGo.transform, fw * 3f, fh * 3f, -(PlateRight - FishLeft) / 2f + 14f + fw * 1.5f, 0f);
            s_fishIcon = AddSprite(s_fishIconRect, fish);
            s_staminaIcon = AddSprite(Rect("Icon", s_staminaGo.transform, fw * 2f, fh * 2f, 20f, 0f), fish);
        }

        /// <summary>A dark translucent card with a pixel border and a faint lit top edge.</summary>
        static GameObject Plate(string name, float left, float right, float bottom, float top, float alpha)
        {
            RectTransform rt = Rect(name, s_panel, right - left, top - bottom, (left + right) / 2f, (bottom + top) / 2f);
            Frame(rt, alpha);
            return rt.gameObject;
        }

        static void Frame(RectTransform rt, float alpha)
        {
            AddImage(rt, Rgba(0.03f, 0.03f, 0.06f, Math.Min(1f, alpha + 0.22f)));
            RectTransform face = Stretch("Face", rt, U);
            AddImage(face, Rgba(0.09f, 0.11f, 0.19f, alpha));
            RectTransform lit = Stretch("Lit", face, 0f);
            lit.anchorMin = V2(0f, 1f);
            lit.offsetMin = V2(0f, -U);
            AddImage(lit, Rgba(1f, 1f, 1f, 0.1f));
        }

        static RectTransform Rect(string name, Transform parent, float w, float h, float x, float y)
        {
            var go = new GameObject(name);
            var rt = go.AddComponent<RectTransform>();
            rt.SetParent(parent, false);
            rt.anchorMin = rt.anchorMax = rt.pivot = V2(0.5f, 0.5f);
            rt.sizeDelta = V2(w, h);
            rt.anchoredPosition = V2(x, y);
            return rt;
        }

        static RectTransform Stretch(string name, Transform parent, float inset)
        {
            var go = new GameObject(name);
            var rt = go.AddComponent<RectTransform>();
            rt.SetParent(parent, false);
            rt.anchorMin = V2(0f, 0f);
            rt.anchorMax = V2(1f, 1f);
            rt.offsetMin = V2(inset, inset);
            rt.offsetMax = V2(-inset, -inset);
            return rt;
        }

        static Image AddImage(RectTransform rt, Color color)
        {
            var image = rt.gameObject.AddComponent<Image>();
            image.color = color;
            image.raycastTarget = false;
            return image;
        }

        static Image AddSprite(RectTransform rt, Sprite sprite)
        {
            Image image = AddImage(rt, Rgba(1f, 1f, 1f, 1f));
            image.sprite = sprite;
            return image;
        }

        static TextMeshProUGUI AddText(string name, Transform parent, TMP_FontAsset font, float size, float x, float y, TextAlignmentOptions align, Color color)
        {
            RectTransform rt = Rect(name, parent, TextWidth, 48f, x, y);
            var text = rt.gameObject.AddComponent<TextMeshProUGUI>();
            text.font = font;
            text.fontSize = size;
            text.alignment = align;
            text.color = color;
            text.raycastTarget = false;
            text.text = "";
            return text;
        }

        /// <summary>Borrow whichever font the game's own UI already has loaded, so the HUD matches it.</summary>
        static TMP_FontAsset FindFont()
        {
            var fonts = Resources.FindObjectsOfTypeAll(Il2CppType.Of<TMP_FontAsset>());
            foreach (var o in fonts)
            {
                var f = o.TryCast<TMP_FontAsset>();
                if (f != null) return f;
            }
            return null;
        }
    }
}
