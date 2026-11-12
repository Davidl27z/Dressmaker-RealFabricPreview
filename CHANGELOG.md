# Changelog

## 2.2.0
- Startup compatibility check: if a game update removes something essential, the mod switches
  itself off cleanly and the game runs normally. Optional features turn off individually.
- Errors can no longer propagate into the game. Repeated errors switch the mod off.
- Logs BepInEx, Unity and game versions, and warns about BepInEx older than 5.4.23.6.
- Fabrics are no longer adopted from old keys while still on the title screen.

## 2.1.0
- Fabrics are kept per save slot and per dress (previously all "Off rack dress" dresses shared them).
- Viewing an old dress could move the active dress's fabrics: fixed.
- Fabrics not loaded yet are retried instead of staying blank.
- Fixed a mesh leak, lost drops while hidden with F9, and double-click on buttons removing fabric.

## 2.0.0
- Rendering with the game's own fabric material (sheen, sequins, normal maps) at garment scale.
- Seams and edges drawn over the fabric colour instead of white.
- Standalone rewrite using the game's region map, so pieces always match the current components.
