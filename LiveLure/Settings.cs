using BepInEx.Configuration;

namespace LiveLure
{
    static class Settings
    {
        public static ConfigEntry<bool> Enabled, RequireTechnique, ShowHud;
        public static ConfigEntry<string> ToggleKey, SensitivityDownKey, SensitivityUpKey;

        public static ConfigEntry<string> GripButton;
        public static ConfigEntry<bool> InvertY, RodWhileHooked, BailTap, SlackPickup, TopwaterFloat;
        public static ConfigEntry<float> Sensitivity, SweepFraction, RaiseDegrees, DipDegrees, HorizontalWeight, Smoothing, ReturnSpeed, YankPower;

        public static ConfigEntry<bool> MouseCast;
        public static ConfigEntry<float> CastFullSpeed, CastWindup;

        public static ConfigEntry<float> MaxPulseRate, MinQuality, RangeBonus, SecondaryCap, TemptChance;

        public static ConfigEntry<bool> Hookset, HooksetManualOnly;
        public static ConfigEntry<float> HooksetWindow, HooksetJerkDegrees, HooksetBestChance, HooksetSpookChance;

        public static ConfigEntry<bool> FishBehaviour, ReactionStrikes, Spooking, Splashdown, FishTell;
        public static ConfigEntry<float> ReactionChance, RipSpeed, ReactionRange, SpookChance;

        public static ConfigEntry<bool> PopperHandling;
        public static ConfigEntry<string> PopSplash, PopSound;
        public static ConfigEntry<float> PopTravel, PopBrake, PopSnap, PopVolume, PopSplashSize, PopLiftOut, PopCall;

        public static ConfigEntry<bool> FightMechanics, WheelDrag, LandingGrip;
        public static ConfigEntry<string> DragLooserKey, DragTighterKey;
        public static ConfigEntry<float> Drag, DragStep, PumpGrip, PressureTire, SlackTire, PumpTire, PumpRecover, TurnChance, JerkStrain, BreakTimeScale;

        public static ConfigEntry<float> HudX, HudY, HudScale;
        public static ConfigEntry<bool> HudStepAside;

        public static ConfigEntry<bool> Telemetry, SelfTest;
        public static ConfigEntry<string> SelfTestLure;

        public static void Bind(ConfigFile c)
        {
            Enabled = c.Bind("General", "Enabled", true, "Master switch. Can also be toggled in game with ToggleKey.");
            RequireTechnique = c.Bind("General", "RequireTechnique", true,
                "Fish are only attracted while the lure is worked the way that lure is fished in real life. " +
                "Off = vanilla attraction; rod control, casting and hooksets still apply.");
            ShowHud = c.Bind("General", "ShowHud", true, "Show the technique name, action meter, rod gauge and prompts.");
            ToggleKey = c.Bind("General", "ToggleKey", "F8", "Keyboard key that toggles the mod on/off (UnityEngine.InputSystem.Key name). Empty = none.");

            GripButton = c.Bind("Rod", "GripButton", "RightMouse",
                "Hold this to take hold of the rod; the mouse then moves the rod instead of the cursor. " +
                "RightMouse, MiddleMouse, Mouse4 or Mouse5.");
            Sensitivity = c.Bind("Rod", "MouseSensitivity", 1f,
                "Overall mouse sensitivity for the rod and for casting. Lower = more hand movement for the same rod movement. " +
                "Adjustable in game with the keys below.");
            SensitivityDownKey = c.Bind("Rod", "SensitivityDownKey", "F9", "Key that lowers MouseSensitivity. Empty = none.");
            SensitivityUpKey = c.Bind("Rod", "SensitivityUpKey", "F10", "Key that raises MouseSensitivity. Empty = none.");
            SweepFraction = c.Bind("Rod", "SweepScreenFraction", 0.9f,
                "Cursor travel for the rod's whole range at sensitivity 1, as a fraction of the screen height.");
            RaiseDegrees = c.Bind("Rod", "RaiseDegrees", 70f, "How far the rod can be raised from its resting angle.");
            YankPower = c.Bind("Rod", "YankPower", 3.5f,
                "How fast a hard yank of the rod can rip the lure through the water, as a multiple of the speed the base game " +
                "allows (which is set for gentle reeling). A slow lift is unaffected. 1 = base game.");
            DipDegrees = c.Bind("Rod", "DipDegrees", 20f, "How far the rod can be lowered below its resting angle.");
            HorizontalWeight = c.Bind("Rod", "HorizontalWeight", 0.5f,
                "How much sideways mouse movement back from the water counts as raising the rod (0 = vertical only).");
            Smoothing = c.Bind("Rod", "Smoothing", 0.035f, "Seconds of lag between the mouse and the rod. A little gives the rod weight; 0 = instant.");
            ReturnSpeed = c.Bind("Rod", "ReturnSpeed", 160f, "Degrees per second the rod settles back to rest after you let go.");
            InvertY = c.Bind("Rod", "InvertY", false, "Mouse down raises the rod instead of mouse up.");
            RodWhileHooked = c.Bind("Rod", "RodControlWhileHooked", true, "Keep rod control while fighting a hooked fish, so you can pump the rod.");
            BailTap = c.Bind("Rod", "BailTap", true,
                "A quick tap of the grip button opens the bail and lets line run out; any click closes it. " +
                "This replaces the base game's hold-right-click. Only applies when GripButton is RightMouse.");

            SlackPickup = c.Bind("Rod", "SlackPickup", true,
                "The reel winds on slack line that the lure is not using (as you would when lowering the rod), so the next " +
                "rod movement reaches the lure. Line a sinking lure is still falling on is left alone.");
            TopwaterFloat = c.Bind("Rod", "TopwaterFloats", true,
                "Topwater lures float to the surface and stay there. In the base game they hang wherever they stop.");

            MouseCast = c.Bind("Cast", "SwingToCast", true,
                "With the rod idle, hold the grip button, swing the mouse toward the water and release to cast. " +
                "The base game's hold-left-click cast still works.");
            CastFullSpeed = c.Bind("Cast", "FullSwingSpeed", 20f,
                "Swing speed that gets everything out of a loaded rod, in screen heights per second at sensitivity 1. " +
                "Power is mainly how far the rod was drawn back; a slower swing uses a little over half of it.");
            CastWindup = c.Bind("Cast", "WindupScreenFraction", 0.9f, "Cursor travel that draws the rod fully back, at sensitivity 1.");

            MaxPulseRate = c.Bind("Attraction", "MaxPulseRate", 3f,
                "Attraction checks per second at a full action meter. Vanilla runs 1 to 2 per second no matter what you do.");
            MinQuality = c.Bind("Attraction", "MinQuality", 0.3f, "Action meter level below which no fish are attracted.");
            RangeBonus = c.Bind("Attraction", "RangeBonus", 0.8f,
                "Extra distance fish notice a well-worked lure from. 0.8 = up to 80% further at a full action meter.");
            TemptChance = c.Bind("Attraction", "TemptChance", 0.6f,
                "Chance, at a full action meter, that a fish which does not normally go for this kind of lure bites anyway " +
                "instead of turning away. Scales down with the meter. 0 = base game: such fish nearly always lose interest.");
            SecondaryCap = c.Bind("Attraction", "SecondaryTechniqueCap", 0.8f,
                "How full the action meter can get from a technique the lure supports but is not known for (e.g. jigging a spinnerbait).");

            Hookset = c.Bind("Hookset", "Enabled", true, "A bite must be answered with a sharp upward jerk of the rod to set the hook.");
            HooksetManualOnly = c.Bind("Hookset", "ManualOnly", true,
                "Fish never hook themselves: every bite needs your hookset. Off = some bites hook on their own as in the base game.");
            HooksetWindow = c.Bind("Hookset", "Window", 0.9f, "Seconds after a bite in which a jerk can still set the hook.");
            HooksetJerkDegrees = c.Bind("Hookset", "JerkDegrees", 18f, "How far the rod must be raised within a fifth of a second to count as a hookset.");
            HooksetBestChance = c.Bind("Hookset", "BestChance", 0.92f, "Chance to hook with a fast, strong jerk. Slower and weaker jerks do worse.");
            HooksetSpookChance = c.Bind("Hookset", "SpookChance", 0.6f, "Chance a missed hookset scares the fish away instead of leaving it interested.");

            FishBehaviour = c.Bind("Fish", "SpeciesBehaviour", true,
                "Fish respond to how the lure moves, each kind in its own way: ambushers hit a lure that stalls, chasers one that " +
                "speeds up, shy fish one that lies still, steady feeders one kept in rhythm, wary followers a change of pace. " +
                "Off = any bite from a fish that likes the lure just needs your hookset.");
            ReactionStrikes = c.Bind("Fish", "ReactionStrikes", true,
                "A lure ripped past a predator can make it charge and hit on instinct, whatever kind of lure it is.");
            ReactionChance = c.Bind("Fish", "ReactionChance", 1f,
                "Multiplier on each species' own chance of a reaction strike (pike and bass are the keenest, shy fish never do it).");
            RipSpeed = c.Bind("Fish", "RipSpeed", 100f,
                "How fast the lure must be moving, in game pixels per second, to count as ripped. Reeling tops out near 50 and a " +
                "gentle lift of the rod near 65; a hard yank reaches 120 to 180 depending on the lure.");
            ReactionRange = c.Bind("Fish", "ReactionRange", 60f,
                "How close, in game pixels, a ripped lure must pass for a fish with average eyesight to react. The rod is 50 long.");
            Spooking = c.Bind("Fish", "Spooking", true,
                "Ripping a lure near shy fish scares them off, and can lose you one that was following.");
            SpookChance = c.Bind("Fish", "SpookChance", 1f, "Multiplier on each species' own chance of being scared by a rip.");
            Splashdown = c.Bind("Fish", "Splashdown", true,
                "A lure landing close to fish gets noticed: it can scare the shy ones and bring a predator over for a look.");
            FishTell = c.Bind("Fish", "ShowFishRead", true,
                "Show a line on the HUD when a fish is following: what kind it is, what it wants to see, and whether the lure is doing it.");

            FightMechanics = c.Bind("Fight", "Enabled", true,
                "The fight is yours to win: an adjustable drag that gives line instead of letting it snap, a fish that tires " +
                "faster the harder you load the line, and rod pumps that wear it down and can turn it. Off = base game fight.");
            Drag = c.Bind("Fight", "Drag", 0.5f,
                "Drag setting: how much of the line's strength it takes to pull line off the reel (0.2 loose .. 1 locked). " +
                "Loose is safe but the fish takes line and tires slowly; tight wears it down fast but the line can part. " +
                "Changed in game with the mouse wheel or the keys below, and remembered.");
            DragStep = c.Bind("Fight", "DragStep", 0.05f, "How much one wheel notch or key press changes the drag.");
            WheelDrag = c.Bind("Fight", "MouseWheelSetsDrag", true, "The mouse wheel adjusts the drag whenever the rod is out: up tightens, down loosens.");
            DragLooserKey = c.Bind("Fight", "DragLooserKey", "Z", "Key that loosens the drag. Empty = none.");
            DragTighterKey = c.Bind("Fight", "DragTighterKey", "X", "Key that tightens the drag. Empty = none.");
            PumpGrip = c.Bind("Fight", "PumpGrip", 0.5f,
                "How firmly the spool is held while you heave the rod: 0 = the drag slips as usual and a pump mostly pulls line, " +
                "1 = locked for the length of the heave.");
            PressureTire = c.Bind("Fight", "PressureTire", 1.6f,
                "How much faster a fish tires under full line tension, added to SlackTire. With both at their defaults a fish " +
                "tires at 0.7x the base game's rate on a slack line and 2.3x with the line at its limit.");
            SlackTire = c.Bind("Fight", "SlackTire", 0.7f, "How fast a fish tires with no tension on the line, relative to the base game.");
            PumpTire = c.Bind("Fight", "PumpTire", 0.06f,
                "What one good heave of the rod (a big lift, on a loaded line, not too soon after the last) takes out of a fish, " +
                "as a share of a 40-second fight. A fish with more fight in it loses more seconds to each pump but a smaller " +
                "share, so it needs more of them. 0 = pumping does nothing.");
            PumpRecover = c.Bind("Fight", "PumpRecover", 0.9f, "Seconds between pumps for each to count in full. Faster pumping counts for less.");
            TurnChance = c.Bind("Fight", "TurnChance", 0.6f,
                "Best chance that a pump turns a fish that is running away so it swims back toward you. Heavier fish for the rod, " +
                "weaker pumps and a slipping drag all lower it.");
            JerkStrain = c.Bind("Fight", "JerkStrain", 0.6f,
                "Seconds of line-at-its-limit that heaving the rod adds when the line is already at its limit. " +
                "The line parts after 4 such seconds in total.");
            BreakTimeScale = c.Bind("Fight", "BreakTimeScale", 1f,
                "How long the line survives at its limit, as a multiple of the base game's 4 seconds.");
            LandingGrip = c.Bind("Fight", "LandingGrip", true,
                "The drag firms up by itself once the fish is worn out and as it comes close, so a light drag setting does " +
                "not stop you bringing a beaten fish to hand.");

            PopperHandling = c.Bind("Topwater", "Popper", true,
                "Topwater lures behave like a real popper: they sit in the surface film, a twitch of the rod pops them a short " +
                "way with a splash and a sound, and they stop dead instead of gliding. Off = the lure follows the rod like any other.");
            PopTravel = c.Bind("Topwater", "PopTravel", 0.4f,
                "How much of the rod tip's movement reaches a topwater lure. Lower = shorter pops for the same twitch, more finesse; " +
                "1 = the lure moves as far as the rod tip does.");
            PopBrake = c.Bind("Topwater", "PopBrake", 7f,
                "How hard the water stops a topwater lure once the line goes slack, per second. The base game gives it none, " +
                "so one yank sent it gliding all the way in. Higher = stops sooner.");
            PopSnap = c.Bind("Topwater", "PopSnap", 0.6f,
                "How much faster than reeling speed a sharp twitch can move a topwater lure. 0 = never faster than reeling.");
            PopLiftOut = c.Bind("Topwater", "LiftOutDistance", 45f,
                "Within this many game pixels of the rod a raised rod lifts a topwater lure clear of the water; further out it stays on the surface.");
            PopSplash = c.Bind("Topwater", "PopSplash", "fish-s",
                "Which of the game's splash effects a pop throws up: fish-s (a small fish's), fish-m, lure (the lure's own landing splash) or none.");
            PopSplashSize = c.Bind("Topwater", "PopSplashSize", 1f, "Size of the pop splash. A gentle pop is drawn at 45% of this, a hard one at 100%.");
            PopSound = c.Bind("Topwater", "PopSound", "lure",
                "Which of the game's sounds a pop makes: lure (the lure's own landing plop), fish-s, fish-m or none. Gentle pops are quieter and pitched up.");
            PopVolume = c.Bind("Topwater", "PopVolume", 1f, "Loudness of the pop sound for a hard pop. A gentle pop plays at about a third of this.");
            PopCall = c.Bind("Topwater", "PopCall", 0.6f,
                "How much of an attraction check each well-timed pop is worth on top of the usual steady rate. 1 = every pop is a call to the fish.");

            HudX = c.Bind("Hud", "X", 0.5f, "Horizontal position of the HUD (0 = left, 1 = right).");
            HudY = c.Bind("Hud", "Y", 0.09f, "Vertical position of the HUD (0 = bottom, 1 = top).");
            HudScale = c.Bind("Hud", "Scale", 1f, "HUD size multiplier.");
            HudStepAside = c.Bind("Hud", "StepAside", true,
                "When your lure is on the screen behind the HUD (a float or a topwater lure rides low on the screen, where the HUD is), " +
                "the HUD slides sideways so you can see it.");

            Telemetry = c.Bind("Debug", "Telemetry", false,
                "Log lure motion and every cast, pop, strike, hookset and fight event to BepInEx\\LogOutput.log. " +
                "Turn this on before reporting a problem and send that file.");
            SelfTest = c.Bind("Debug", "SelfTest", false, "Developer self-test: disables saving and drives the game automatically. Leave off.");
            SelfTestLure = c.Bind("Debug", "SelfTestLure", "", "Developer self-test: lure id to put through a logged workout instead of the standard steps.");
        }
    }
}
