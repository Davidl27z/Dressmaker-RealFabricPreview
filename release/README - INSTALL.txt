DRESSMAKER - REAL FABRIC PREVIEW
================================
(Tested with Dressmaker 0.7.0 and BepInEx 5.4.23.6)

When you drop a fabric on the sketchbook, the piece shows the fabric the way it
really looks on the finished garment (colour, print, sheen, sequins).


WHICH FOLDER DO I USE?
----------------------
Look for a folder called "BepInEx" in the game folder.
To find the game folder: in Steam, right-click Dressmaker -> Manage -> Browse local files.

  * NO "BepInEx" folder   ->  use  "1 - Full install (Instalacion completa)"
  * "BepInEx" folder found ->  use  "2 - Mod only (Solo el mod)"
                                (and read "If you already had BepInEx" below)


1 - FULL INSTALL (no mods yet)
------------------------------
1. Close the game.
2. Copy EVERYTHING inside "1 - Full install (Instalacion completa)" into the
   game folder, next to Dressmaker.exe. If Windows asks to replace files, say yes.
3. Start the game. The first start takes a little longer.


2 - MOD ONLY (you already have BepInEx)
---------------------------------------
1. Close the game.
2. Copy the "RealFabricPreview" folder (inside
   "2 - Mod only (Solo el mod)\BepInEx\plugins") into the game's BepInEx\plugins.
3. Start the game.


IF YOU ALREADY HAD BepInEx
--------------------------
* It must be BepInEx 5, version 5.4.23.6 or newer.
  With 5.4.23.2 or older the game CRASHES AT STARTUP (this game uses Unity 6.3).
  If that happens, do the "Full install": it updates BepInEx and keeps your other mods.
  Your version is on the first line of BepInEx\LogOutput.log.
* BepInEx 6 ("pre" / "be" builds) is NOT supported: this mod is for BepInEx 5.
* "Dressmaker Mod Loader" already includes BepInEx: use "2 - Mod only".
* If you have "Dressmaker Fabric Pattern Preview" (DressmakerFabricPattern.dll),
  REMOVE it from BepInEx\plugins. Both mods paint the sketch.


HOW TO USE
----------
* Drag a fabric from your inventory and drop it on a piece of the sketch.
* Double-click a piece: removes its fabric.
* Colouring a piece with the pencil: removes its fabric.
* The game's "erase all" eraser: removes every fabric of the dress.
* F9: toggle between the real fabric and the game's drawn look.
* Fabrics are remembered per dress and per save slot.


SETTINGS (optional)
-------------------
After the first start, open BepInEx\config\dressmaker.realfabricpreview.cfg with Notepad:
  WholePiece = true     -> a fabric covers the whole piece (whole skirt)
  PageMeters = 2        -> lower = bigger prints
  LightIntensity = 1.2  -> higher = brighter fabrics
  RealShader = false    -> flat mode, no sheen


IF SOMETHING DOESN'T WORK
-------------------------
Open BepInEx\LogOutput.log with Notepad and search for "Real Fabric":
* "Real Fabric Preview ... loaded"   -> the mod is working.
* "Real Fabric Preview is OFF: this Dressmaker version is not compatible"
     -> a game update changed something the mod needs. The mod switched itself off
        and the game works normally without it. A new mod version is needed.
* "Some features are off"   -> it works, but without some feature (e.g. tinted seams).
* "older than 5.4.23.6"   -> update BepInEx with the "Full install".
* "switched itself OFF after repeated errors"   -> please open an Issue on the mod's
     GitHub page and attach this file.
The mod should never crash the game or touch your saves.


UNINSTALL
---------
Delete the folder BepInEx\plugins\RealFabricPreview.
To remove BepInEx completely, also delete the BepInEx folder and the files
winhttp.dll, doorstop_config.ini, .doorstop_version and changelog.txt.
The mod never changes your save files.
