LIVE LURE 0.8.0  -  a mod for Cast n Chill
==========================================

Hold the rod with your mouse. Swing it to cast, work the lure the way that lure is really fished, jerk the rod to
set the hook, and fight the fish with an adjustable drag. Fish respond to how the lure moves, each kind in its own
way.

This is a fan-made mod. It is not made by, or affiliated with, the game's developers. It contains none of the
game's files: you need your own copy of Cast n Chill.


WHAT YOU NEED
-------------
* Cast n Chill on Steam, on Windows. Built and tested on game version 1.3.0.
* BepInEx 6 "bleeding edge" for Unity IL2CPP games, Windows x64. This mod was made with build 788
  (BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.788). Get it from the BepInEx project's own build site:
      https://builds.bepinex.dev/projects/bepinex_be
  Pick "BepInEx Unity (IL2CPP) for Windows (x64) games". The ordinary BepInEx 5 download will not work.


INSTALL
-------
1. In Steam, right-click Cast n Chill > Manage > Browse local files. The folder that opens (it has castnchill.exe
   in it) is the game folder.
2. Unzip BepInEx into the game folder, so that winhttp.dll ends up next to castnchill.exe.
3. Start the game once and let it reach the menu, then close it. This first start takes a minute or two while
   BepInEx sets itself up, and it downloads a few support files, so it needs an internet connection that one time.
   A black text window opens alongside the game: that is BepInEx, and it is normal.
4. Unzip LiveLure-0.8.0.zip into the game folder. You should now have
       BepInEx\plugins\LiveLure\LiveLure.dll
5. Start the game. The black window should show the line
       Live Lure 0.8.0 loaded (12/12 patches)


HOW TO PLAY
-----------
Hold right-click            Take hold of the rod. The mouse now moves the rod: up raises it, down lowers it.
Left-click while holding    Reel in.
Tap right-click             Open the bail so line runs out. Any click closes it.
Rod idle: hold right-click, pull the mouse back, swing it toward the water, let go
                            Cast. How far you drew back and how fast you swing set the power.
Jerk the rod up on BITE!    Set the hook. Fast and hard is best; a late or weak jerk can miss.
Mouse wheel (or X / Z)      Tighten / loosen the reel's drag. Loose is safe but slow; tight tires the fish fast
                            and can break the line.
Jerk the rod up, fish on    Pump the fish: wears it down, and can turn one that is running.
F9 / F10                    Lower / raise mouse sensitivity for the rod.
F8                          Switch the mod off and on.

The panel at the bottom of the screen shows the rod, a meter for how well you are working the lure, and what the
lure wants. Fish only come while the meter is past the white tick. When a fish follows, a strip above the panel
says what kind it is and what will make it bite.

  Float            leave it still
  Crankbait, spinnerbait, swimbait   steady retrieve
  Spoon            lift, let it flutter down
  Jig head         hop it: lift, drop, pause
  Blade bait       rip it up hard, let it fall
  Wacky rig        gentle lifts, let it sink on a slack line
  Glide bait       pull and pause
  Drop shot        small shakes in place
  Topwater         pop it: a short twitch, a pause, another

The game's own controls still work whenever you are not holding the rod, and idle mode fishes the normal way.


SETTINGS
--------
BepInEx\config\mike.castnchill.livelure.cfg appears after the first run with the mod. Open it in Notepad; every
setting has a description. The ones most worth a look: MouseSensitivity, YankPower, the [Hookset] section, the
[Fight] section, and [Topwater] PopSound if you do not like the pop.

To hide the black BepInEx window: in BepInEx\config\BepInEx.cfg, under [Logging.Console], set Enabled = false.


GOOD TO KNOW
------------
* Your save is not changed in any special way: fish you catch with the mod go in your journal like any other.
  If you want to be careful, copy your save folder first:
      %USERPROFILE%\AppData\LocalLow\Wombat Brawler\Cast n Chill
* It is for the player on the mouse. Controller players fish as normal.
* Windows only. Steam Deck / Linux has not been tried.
* A game update may break it until the mod is updated. If the game stops starting after an update, remove the mod.


REMOVE
------
Delete the folder BepInEx\plugins\LiveLure. To remove BepInEx as well, delete winhttp.dll, doorstop_config.ini,
.doorstop_version and the BepInEx and dotnet folders from the game folder. The game is then exactly as Steam
installed it.


SOMETHING WRONG?
----------------
In BepInEx\config\mike.castnchill.livelure.cfg set Telemetry = true under [Debug], play until it happens again,
close the game, and send the file BepInEx\LogOutput.log along with what you were doing. That file is overwritten
every time the game starts, so send it before you start the game again.
