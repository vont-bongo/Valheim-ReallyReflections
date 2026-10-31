# ReallyReflections

The sea reflects what is really across it: the shore, trees, buildings, ships, creatures and you, under the sky, clouds and weather above. The game's own water only reflects a blurry sky.

## Features

- A real mirror on the sea: land, buildings, trees, ships, creatures and players show up where they should in the water.
- The weather reflects too: clouds, fog, rain and the Mistlands mist; fires and the Ashlands fortress beacons glow in the water.
- Ships keep their reflection under the hull while they ride the waves.
- The waves bend the reflection as much as they bend the game's own reflection, and it softens as the wind rises.
- The water itself looks as before: the same waves, colours, foam and lighting, including the long sun glitter with the game's Tessellation setting on.
- Every setting applies live. Turn it off and the game's own water and reflection are back, exactly.

## Installation

Install with your mod manager, or drop `ReallyReflections.dll` into `BepInEx/plugins`. Needs BepInEx (BepInExPack Valheim) and nothing else.

## Configuration

`BepInEx/config/ReallyReflections.cfg`, section `Reflections`, created on first start. Changes apply while you play (with a configuration manager or by editing the file).

- **Enabled** (default on): the mirror on the sea. Off = the game's water and reflection.
- **Mirror resolution** (Quarter / Half / Full, default Half): how sharp the reflection is. Half costs about 0.4 ms per frame (1080p, RTX 3080); Full costs more, up to about 3 ms in a storm.
- **Mirror distance** (100–4000 m, default 1000): how far away land and buildings still show in the reflection. The sky always does. Shorter is cheaper.
- **Mirror detail** (TerrainAndBuildings / Everything, default Everything): TerrainAndBuildings leaves creatures, players, ships and items out of the reflection, which is cheaper.
- **Wave distortion strength** (0–2, default 0.3): how much the waves bend the reflection. 1 = as much as the game's own water, 0 = a flat mirror.
- **Sky softness** (0–1, default 0.1): softens the reflected sky and clouds. Land and ships stay sharp.
- **Reflection blur** (0–3, default 1): softens the whole reflection. 0 = sharp.
- **Storm blur** (0–4, default 2): extra softness as the wind rises, the way rough water blurs a reflection.
- **Shadows** (default on): shadows in the reflection, so trees shade themselves as they do on land. Off is a little cheaper.

## Compatibility

- Client-side only: install it on your own game. Servers and other players don't need it, and nothing is stored in your world or character, so you can remove it at any time.
- Made for Valheim 1.0.21. If a game update changes the game's water shader, the mod leaves the game's water alone and writes a warning to the log until it is updated.
- Conflicts with other mods that replace the water's shader: only one of them can draw the sea. The mod keeps the values the game and other mods set on the water (colours, depth), so mods that only adjust those should work alongside it.
- The game colours each 64 m water tile by one corner, so near coasts the water can show square colour seams. That is the game's own look and this mod doesn't change it; a mod that fixes the seams works alongside this one (tested).
- Fish are left out of the reflection: they swim right under the surface and would show as huge shapes.

## Credits

- The water shader is a port of Valheim's own water shader by Iron Gate, so the sea looks the same with the mirror added. It is included in compiled form only.
- The lighting uses Unity's built-in shader functions.
