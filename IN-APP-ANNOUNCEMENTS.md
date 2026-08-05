# Credits
Created and maintained with ❤️‍🔥 by Cubeir with special thanks to: nattyhob, EchoQuasar, Miriel, Giuseppe DiMarca, Cody Starr, Dabadking, Spaceowl, Joseph, Willström, Bastha, PotatoHour, Kittygamer123, Lanaismymommy, James Kelly, Aaerox, jessehall(Maneating-Zebras), Nash Knowlden, OmarVillegas, Isttret, Superluminal, Travis Bishop, Dylan, Kyo Don, Commander Grub, The_Asa_Games, Koiboi, jamesyoung, Nick Da Fox, Richard Anderson (Rich), Jacob, DomoTurbulence, Rory, Luxalios, Oxbow117, Mono234_Glitch, Austin Mullings, mIbU, Spikey ᵈᵉʳ ᶠᵘᶜʰˢ, Bryan Tepox, 67, Ryan S Beers, TyTGM, AgusRomero0501, IcyFer, Smiletrap, Justin Klaassen, Dogtag, Kudo Cyylentaar — and to everyone who has supported this project in any way along the way.

Consider supporting development of Vanilla RTX — maybe you'll find your name here next time!

# PSA

###
🧊 Vanilla RTX 1.26.20 with Chaos Cubed coverage (and a lot more...) is out!
📯 Read the changelog for this release and get the update from "Get latest RTX packs" menu.

# PackUpdateAnnouncements

### Knwon Issue
Known issue: due to a game issue (MCPE-240950), animated textures have a minor visual glitch in Vanilla RTX, if that bothers you, stick to Vanilla RTX Normals/Opus, which aren't impacted by MCPE-240950

## 1.26.15 [cd:"9999999"] [glyph:"E70F"]
1.26.20 Release Notes:

Full support for Minecraft 1.26.40 and the Chaos Cubed game drop.
BetterRTX 1.5+ is required for Subsurface Scattering and Parallax Occlusion Mapping features.

- Chaos Cubed Game Drop — New Blocks:
Complete PBR support for all new Chaos Cubed blocks: sulfur blocks, potent sulfur, cinnabar block set, and sulfur spikes.
Sulfur Caves biome now features a uniquely sulfuric atmosphere with deep cyan-green water colors matching the vanilla game.

- Sulfur Cube:
Fixed ray tracing rendering issues, including the top texture rendering black and flickering/Z-fighting.
Known issue: interior renders black when the block is submerged but the player camera isn't (or vice versa).
Improved walking animation particles for sulfur cube and slime mobs.

- Cave Biomes — Fog & Atmosphere:
Revamped fog for Deep Dark, Lush Caves, and Dripstone Caves. Water colors now closely match vanilla. Fog heights adjusted to account for surface-exposed generation.
Fixed an issue where the air atmosphere would appear exposed when the camera was inside a small body of water in caves. Note: this is a workaround for a Minecraft bug; it slightly sacrifices vanilla faithfulness in exchange for resolving the issue.

- Subsurface Scattering:
Comprehensive SSS data added to all MER files, which are now migrated to MERS format.
Primarily intended for BetterRTX 1.5+ presets, but can also appear in Vibrant Visuals graphics mode.
All thin surfaces — leaves, foliage, paper parts (e.g. birch trapdoor), even the thin pixels on scaffolding — now properly support light scattering. Every block was individually reviewed.

- Emissive Entities:
Emissive texture data added to all applicable entities. Requires a BetterRTX preset with Emissive Entities enabled.
This is separate from the existing rasterized glowing eyes enhancement and has no impact without BetterRTX installed.
Glow squids, ender chest eyes, ghast mouth and eyes during shooting, drowned, blaze, and many more now feature emissive textures.

- Parallax Occlusion Mapping (Normals & Opus only):
POM data baked into normal maps for use with BetterRTX 1.5+ presets, derived from heightmaps at 1/5th intensity.
Normal maps can be flattened via the Vanilla RTX App; POM data is retained when doing so (a future app update will allow you to reduce POM intensity)
Animated normal map added to the Nether portal texture, creating subtle distortions when viewing anything behind it.

- End Dimension:
Sky overhauled to appear purple instead of black. Fixed lighting issues and addressed unplayability with BetterRTX enabled.
End flash texture made less prominent rather than removed outright, due to it not being properly implemented with ray tracing.

- Particles:
Revamped particle enhancements updated to the latest format version.
Sulfur biome geyser particles tuned for ray tracing. Older particles retuned for more consistent, better-blended opacities throughout.

- Fixes & Minor Enhancements:
Bee nest front: corrected a single misidentified honey pixel, PBR materials adjusted accordingly.
Candle wicks now burn brighter.
Sculk tendril: revised inactive state brightness and fixed heightmap seams in the animation.
XP orb texture bug workaround added (MCPE-183629). Orbs now also glow with an appropriate BetterRTX preset.
Hopper minecart glitchy texture workaround added (MCPE-241124). Model is an approximation until Mojang addresses the issue.
Removed sun and moon enhancements — minimal visual benefit, and they looked off with BetterRTX applied.
Removed unused padding property from terrain_texture.json.
Sulfur cave biome properties updated to match vanilla game parity.
Minimum required Minecraft version raised to 1.26.40, make sure your game is up-to-date.

### Tip [glyph:"E95B"]
Hint: It is always preferred to activate RTX resource packs in your Global Resource Pack settings instead of per-World or Realm.

### Tip [glyph:"E95B"]
Hint: You can come back here to quickly reinstall packs to restore them to their original state in case if you want to revert your tuning attempts. (Reinstalls happen quicker from a cached version, unless a new version happens to be available)

## PSA [cd:"9999999"] [glyph:"ECC5"]
All Vanilla RTX Add-Ons and Extensions have been refreshed for Vanilla RTX 1.26.20 (and higher.)
It is time to update (if you haven't already!) Simply hover their images, and click their names to be taken to their respective CurseForge download pages.

# BetterRTXAnnouncements

## Warning 1 [glyph:"E730"] [cd:"10000"]
Reminder: If your preset list has been auto-reset since your last visit, or this is your first visit:
It is a good idea to wait and check from the BetterRTX Discord whether it has been updated for the latest game version before installing a preset. Installing presets while it serves outdated files could result in crashes and visual glitches. In this scenario, revert to Default RTX, and once BetterRTX is updated, use the refresh button in the top left corner. 
In other words: Minecraft updates can break BetterRTX, it depends on you to update Minecraft, and BetterRTX's maintainer to update it for that game version just in time for everything to continue to work smoothly. If installing BetterRTX causes issues for you, follow these steps:
1. Revert to Default RTX for now
2. Wait until BetterRTX developers confirm they've updated the mod.
3. Use the refresh button in the top left corner to refetch the latest files & continue installing your presets.

## [cd:"120"] [glyph:"F78C"] // check:F78C // warning:E814 // alt text: ATTENTION: DO NOT INSTALL PRESETS FOR NOW. BetterRTX is currently out of date for the latest Minecraft version. Once it is updated, the text here will also change. CHECK BACK LATER!
It is currently safe to install BetterRTX presets, the files were tested and the endpoint seems up-to-date for the latest game version, hit the refresh button in the top left corner just to be sure you're not installing old files, and continue downloading and installing your presets.

# LutManagerAnnouncements 
Look up tables provide a simple way to improve or further customize Minecraft RTX, which works across all game versions reliably and without a performance hit as oppposed to heavier modifications such as BetterRTX. Select from the list of available presets and hit install. You can always revert back to defaults by selecting the default preset.

## [glyph:"E7BA"] [cd:"40000"] 
This feature will not work if you're using a BetterRTX Preset. Use Default/Unmodified RTX if you want to use LUT presets.



# DLSSAnnouncements
### A friendly note
Useful fact: the latest DLSS version isn't always the best! Users report 310.5.3.0 is the latest that gives a sharp image. Newer versions and most other versions in-between tend to be a bit blurry!




# ResourcePackSelectionAnnouncements
### Text below is a one time tutorial type of thing! [glyph:"E95B"]
Select from your resource packs from the list below and begin processing them in bulk, tune, delete, or export!
Use the clear selection button in the main window to clear your selections or by hitting confirm without selecting any packs.





# AlchitexDevProgressUpdates [glyph:"EC24"]
The redstone circuits for this feature are still being laid down.
That said, you can come back here anytime to check on the development news.

## [cd:"10000"] [glyph:"E823"]
July News:
Since I announced earlier this year that RTX Reactor is joining the app, I've mostly ended up working on app's core features instead, which included foundational changes and features that would've been vital if RTX Reactor is to integerate with the existing features smoothly.

No ETA, given I also maintain Vanilla RTX resource packs for Minecraft, I'm being stretched thin at the moment. All I can say is, hopefully things will be different before 2027.
