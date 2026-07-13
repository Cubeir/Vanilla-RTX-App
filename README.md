# Vanilla-RTX-App
Everything you'll ever need to easily acesss Minecraft RTX and unlock its full potential brought together in one place.  

All-in-one Vanilla RTX app lets you automatically update the ray tracing packages for the latest Minecraft releases, tune various aspects of Vanilla RTX resource packs to your preferences, easily enable Minecraft's ray tracing, manage and install BetterRTX shader mods, swap or upgrade DLSS, and more...   
Ensuring ray tracing is accessible to new players, and frictionless for existing users—despite years of neglect from Mojang.
<!-- Microsoft Store badge -->
<p align="center">
  <a href="https://apps.microsoft.com/detail/9N6PCRZ5V9DJ?referrer=appbadge&mode=direct">
    <img src="https://get.microsoft.com/images/en-us%20dark.svg" width="400"/>
  </a>
</p>

<!-- Cover image -->
<p align="center">
  <img src="https://github.com/user-attachments/assets/2af758a4-e047-4b74-8c6e-c9a72177decf" alt="vanilla-rtx-app-cover-render" />
</p>

# Overview
<!-- Second Cover Image -->
<p align="center">
   <img src="https://github.com/user-attachments/assets/8a7a07b3-f241-4e20-9cfb-c5355c310b80" alt="vanilla-rtx-app-cover"/>
</p>
<!-- Badges -->
<p align="center">
  <a href="https://discord.gg/A4wv4wwYud">
    <img src="https://img.shields.io/discord/721377277480402985?style=flat-square&logo=discord&logoColor=F4E9D3&label=Discord&color=F4E9D3&cacheSeconds=86400"/>
  </a>
  <a href="https://ko-fi.com/cubeir">
    <img src="https://img.shields.io/badge/-support%20my%20work-F4E9D3?style=flat-square&logo=ko-fi&logoColor=F4E9D3&labelColor=555555"/>
  </a>
  <img src="https://img.shields.io/github/repo-size/Cubeir/Vanilla-RTX-App?style=flat-square&color=F4E9D3&label=Repo%20Size&cacheSeconds=86400"/>
  <img src="https://img.shields.io/github/last-commit/Cubeir/Vanilla-RTX-App?style=flat-square&color=F4E9D3&label=Last%20Commit&cacheSeconds=86400"/>
</p>

## Ray Tracing Quick Setup Guide (For Beginners)

1. Install the Vanilla RTX App through [Microsoft Store](https://apps.microsoft.com/detail/9N6PCRZ5V9DJ) (or alternatively via a [winget](https://github.com/microsoft/winget-cli) command):
   ```powershell
   winget install "Vanilla RTX App"
   ```
3. Launch the app, click **`Get latest RTX packs`**  
   - This opens a menu which allows you to install (or update) Vanilla RTX resource packs required for enabling ray tracing.
4. After installing a Vanilla RTX resource pack finishes, click **`Launch Minecraft RTX`**  
5. In Minecraft → **Settings → Global Resources → Enable one of the provided Vanilla RTX resource packs.**
6. Enter your world. And you're done!  

Now ray tracing is enabled properly, you have the latest version of [Vanilla RTX](https://github.com/Cubeir/Vanilla-RTX), which is kept up-to-date for the recent version of Minecraft, with [bug fixes](https://minecraftrtx.net/enhancements) for as many issues as possible. This was a bare-bones guide for users who don't want any technical details and just want to get RTX working quickly. Below you'll find the full documentation of functionalities included in the app if you wish to take things further.  

## Documentation

This is your detailed guidebook on what each feature within the app does.

- `Launch Minecraft RTX`  
  Launches Minecraft with ray tracing pre-enabled, additionally disables VSync for fixing performance issues and enables in-game graphics mode switching, this circumvents issues such as these that make enabling ray tracing difficult:    
  [MCPE-191513](https://bugs.mojang.com/browse/MCPE/issues/MCPE-191513): Ray tracing can no longer be enabled while in the main menu.  
  [MCPE-152158](https://bugs.mojang.com/browse/MCPE/issues/MCPE-152158): PBR textures don't load properly upon enabling ray tracing after the game is freshly launched.  
  [MCPE-121850](https://bugs.mojang.com/browse/MCPE/issues/MCPE-121850): Ray Tracing performance starvation when VSync is enabled.
> Even if the game doesn't launch, it's okay, this can happen if the protocol to launch Minecraft isn't assigned on your computer. Just continue to launch the game manually as the app has already done its main job at correcting the options for ray tracing.  
> Holding shift while pressing this button will enable VSync instead of disabling it (not recommended).

![vanilla-rtx-app-ui-screenshots](https://github.com/user-attachments/assets/09563ec6-0099-4afd-b2e5-73a3af930b02)

- `Get latest RTX packs`  
  Opens a window from which you can download and install/resintall, or update to the latest Vanilla RTX resource packs directly from the [Vanilla RTX GitHub repository](https://github.com/cubeir/Vanilla-RTX)  
>   Vanilla RTX resource packs are up-to-date for the latest version of Minecraft and are constantly improving, in short, it is the ongoing effort to bring together a cohesive art direction for ray-traced Minecraft. This window lets you know if there are updates available + update news, and provides the most convenient way to stay updated. 
  Downloaded files remain cached for faster reinstalls until a new update becomes available to you, giving a speedy way reset the packs back to original state for rapid experimentation with the tuning options explored later in this document.
  
  ![vanilla-rtx-app-ui-image](https://github.com/user-attachments/assets/65affd06-3396-4302-aca5-07952d593942)

- `DLSS version swapper`
Allows you to import and manage a catalogue of DLSS dll files for easy version switching. The currently-installed version is automatically catalogued, so you won't lose the original dll when replacing it, allowing you to revert easily. Accepts `.dll` files and `.zip` archives containing any number of dlls. You can also remove dll files from your catalogue that you no longer need. Your currently-installed version of DLSS is always highlighted.

- `BetterRTX manager`
Displays an up-to-date list of all available [BetterRTX](https://bedrock.graphics/) presets, which you can click to download and then install (Once a preset is finished downloading it is moved to the top for convenience)
> Upon installing your first ever preset, a Default RTX preset is created from your local game files that sticks the top, allowing you to rollback/uninstall BetterRTX.
The app watches for game updates on your system, upon detecting updates all of your presets are wiped and the list is refreshed automatically, this automatically guarantees you aren't installing outdated RTX files by accident, and keeps your Default RTX preset up-to-date.
You will also have to redownload the presets as they may have likely been updated on [bedrock.graphics/api](https://bedrock.graphics/api), the app does its best to make sure you're always getting the latest, however, this depends entirely on BetterRTX updating itself just in time, sometimes it takes a while for BetterRTX updates to arrive, if you experience crashes after installing a BetterRTX preset, rollback to the default preset. The app also periodically watches for potential changes in the API and automatically resets your presets to ensure you won't be installing stale files. A manual refresh button is also provided which clears BetterRTX API and preset cache, but not your Default RTX preset (cleared exclusively by game update detection).

- `RTX LUT  manager`  
A simpler way to modify and improve Minecraft RTX's appearance that works reliably across all game versions and Minecraft Preview, you can select from a growing list of pre-bundled presets. Currently, 4 distinct LUT presets are provided:
>  - Gamescom 2019 Demo V2: attempts to restore Minecraft RTX's graphics from its early demos shown to the public, viable alternative to the default preset.
>  - Matrix Fever: based on [Matrix Fever](https://www.curseforge.com/minecraft-bedrock/texture-packs/matrix)
>  - Night Night: features extremely dark nights, great for a horror vibe.  
>  - Whimsical Bright: opposite of Night Night, whimsically colorful and bright!  
With more coming soon. Have an idea for one? Create a suggestion!

- `Preview (Togglable)`  
All of the app's functionalities are targeted at Preview/Beta version of Minecraft instead of main release while  `Preview` is active. All app features and modules (with the exception of `BetterRTX manager`) fully support Minecraft Preview.
  
- `Export selection`  
  Exports selected packs. Useful for backing up your snapshot of the pack before making further changes, or to share your tuned version of Vanilla RTX (or another PBR resource pack) with friends! Since the app doesn't come with any quick way to reset PBR resource packs other than Vanilla RTX, it is recommended that you save them using this feature before tuning.

- `Delete selection`  
Deletes your selected packs from your Minecraft user data directory. Worth noting: it walks up to the parent directory of where the manifest.json of a pack resides and deletes the entire folder from the root of `resource_packs` or `development_resource_packs` directory, something to be aware of if you happen to store other improtant files within your resource packs folders.

- `RTX Reactor`  
[RTX Reactor](http://minecraftrtx.net/reactor) is joining the Vanilla RTX App down the road, the window currently hosts its development news.
  
## Resource Pack Tuner & Management Tools

The Vanilla RTX App includes tools to tune Vanilla RTX or any other RTX or Vibrant Visuals resource pack according to your preferences. Tuning options each use algorithms designed to preserve the PBR textures' composition, making it difficult to derail a pack's intended look while nudging it in your preferred direction. Changes are made to a resource pack's files are permanent, however, the whole tuning process works together with the app's other resource pack management features to keep the experience streamlined/quickly reversible, for instance, you can quickly export your resource packs to keep a backup before tuning them, and if you aren't satisfied with the results, reimport them through the app. Or in case of Vanilla RTX resource packs, simply hit their Reinstall button from `Get latest RTX packs` menu.

![vanilla-rtx-app-pack-selection-menu](https://github.com/user-attachments/assets/0b2dadce-a7ac-4d08-b44c-5c93618bcd02)

The app keeps track of your installed Vanilla RTX resource packs, and their checkboxes become enabled for each one that is installed, for selecting resource packs other than Vanilla RTX, you can do it from the Select other packs menu.  

To tune a resource pack, you must first select it, set the parameters you wish, and hit Tune, which begins modifying the pack's files.

- `Select other packs` + `Import`  
  Opens a menu containing a list of your installed resource packs. You can select any number of packs to perform bulk actions on them, every pack is listed and they may feature different Tags with different meanings:
  - Ray Traced: RTX resource packs that Tuner is capable of tuning.
  - Vibrant Visuals: resource packs that declare Vibrant Visuals capability and may also be tuned by the Tuner.
  - Incompatible with Tuner: other resource packs that the tuner should not have an impact on, but you can still select them for bulk exports, or deletion.
  - RTX Reactor Candidate: resource packs that will potentially be suitable to be put through RTX Reactor once it is available later down the road.

  You can also import resource packs from this same menu, the following formats are supported:
  - `.mcpack`
  - `.mcaddon` (the app does not import behavior packs, however, multiple resource packs might be bundled inside one .mcaddon, this a rare practice among resource pack authors, which the app supports nevertheless)
  - `.zip`
  
- `Fog multiplier`  
  Updates all fog densities by a given factor — e.g., `0.5` to halve, `3.0` to triple, or `0.0` to effectively disable air fog. If fog's density is already at zero, the multiplier is instead converted into, and set as an acceptable literal number between `0.0-1.0`.
  If fog density is at maximum, excess of the multiplier will be used to scatter more light in the atmosphere. Underwater fog is also affected partially (to a much lesser degree to avoid messing underwater view distance, or the delicate colors seen for example, in Vanilla RTX).

> Recommendation: If using a Vanilla RTX resource pack with BetterRTX installed, because of how differently fog appears compared to unmodded Minecraft RTX, tune fog with a `0.5-0.7` multiplier.

  ![fog-panel](https://github.com/user-attachments/assets/a013dc6a-bd46-41f1-b980-0620f0514588)

- `Emissivity multiplier`  
  Multiplies emissivity on blocks using a special formula that preserves the relative emissive values/keeps the composition intact, even if the multiplier is too high for a particular texture.
  
![street-default-vanilla-rtx](https://github.com/user-attachments/assets/19e802e9-42c6-4e70-a931-6474f5e10716)
![street-3x-emissivity-tuned-vanilla-rtx](https://github.com/user-attachments/assets/90e7d2a4-afdc-4250-9ecf-d5cc15fd9dc7)

- `Increase ambient light`  
Adds a small, uniform amount of emissivity to every surface, simulating ambient light under ray tracing. Push it too far and you'll get a night-vision effect.  
This option is scaled by the Emissivity Multiplier — a higher multiplier means stronger ambient lighting, whether it's the pack's first tuning pass or a later one.
> Warning: Tuning a pack with this option enabled marks it. On every tuning pass after that, the Emissivity Multiplier stops applying to the pack's regular emissivity, and continues to scale only the ambient light this option adds. This prevents the multiplier from compounding across repeated passes and blowing out the pack's lighting entirely. Because of this, it's best used sparingly, and generally as the last step once you're happy with everything else.

![ambient-lighting](https://github.com/user-attachments/assets/e44fe5f8-06d0-4d43-978c-fc954f5d83e8)

- `Surface normal intensity`  
  Adjusts the intensity of normal maps and heightmaps (in percentage). A value of 0% flattens the textures while larger values increase the intensity of normal maps on blocks. This is handled through a special algorithm that preserves composition and relative intensity data even at extreme values.

- `Roughness control`  
  Increases roughness using a decaying curve, so glossy surfaces are affected more than already-rough ones — high positive values, for example, let you align a pack with Vibrant Visuals' PBR art style. Metalness is reduced slightly, in proportion to how much roughness was added.  
  Negative values invert this: extremely rough surfaces are made less rough instead, and metalness is boosted in proportion to how much roughness *would have* been added had the value been positive.

  > This may sound convoluted, but the logic holds up in practice. Put simply: negative values make surfaces shinier, positive values make them less shiny. Apply it in multiple passes until you're satisfied with the result.
  
- `Lazify surface normals`  
  Uses a modified color texture to make heightmaps/normal maps less refined. The given number determines effectiveness `(0 = no change, 255 = fully lazy)`. For normal maps in particular, not all original detail is lost even at 255, instead it is partially blended and overlayed. Additionally, the final normal map always retains the same overall intensity as the starting normal map.  
  > Note: Assuming the resource pack isn't already shoddy, a tame value of `1-10` adds a subtle layer of organic detail. This option works only if the normal map or heightmap resolution matches the color texture; therefore, it has no impact on Opus variant of Vanilla RTX.  

- `Material grain offset`  
  Creates grainy materials by adding a layer of noise, input value determines the maximum allowed deviation from the original value.
  > This is done safely with an algorithm that preserves pack's intended appearance while adding a layer of detail, emissives are affected minimally, additionally, noise patterns persist across animated textures or texture variations (e.g. a redstone lamp off and redstone lamp on retain the same noise pattern)
  The noise has a subtle checkerboard pattern that mimics the same noise seen on the PBR textures of default Vibrant Visuals while still giving the pack a slightly fresh look each time the noise is applied!

- `Tune selection`  
  Begins applying your chosen parameters to every resource pack you've selected. Changes are permanent and each tuning pass stacks/builds on top the previous, until the pack is updated or its original files are freshly reinstalled.

> These tools can be extraordinarily powerful when used correctly on the right pack — for instance, PBR textures in Vanilla RTX can be processed to fully match the "style" of most other vanilla-esque PBR resource packs, or even Mojang's Vibrant Visuals. However, this doesn't hold true the other way around!

> While tuning, the button turns into `Abort tuning operation`, which can be used to stop the tuning operation, textures already finished keep their changes, anything not yet reached by the Tuner is left untouched. This is more of a safety net in case you accidentally press the Tune button, since the app takes a few seconds to collect the resources it needs before tuning, if you're quick enough, likely nothing will have been modified.

- `Clear Selection` 
  Clears the Vanilla RTX and custom pack selections.

- `Reset`  
  Resets tuning values and options to their defaults — this does not reset packs back to its default states, to do that, you must reinstall the packages via the `Get latest RTX packs` or if it is a custom pack, manually reimport the original/unmodified pack from the `Select other packs` menu.

- `Wipe / Hard reset (Hold Shift + Reset)`  
  Completely wipes the app's storage and all temporary data — cached timestamps (e.g. update check cooldowns), tuning options, cached pack and game locations, DLSS and BetterRTX caches, PSA messages, and more — then restarts the app. This is the most thorough way to remove every trace of the app, effectively returning it to a freshly installed state.

  > This will also attempt to restore your game's files to their defaults (e.g. if you've installed BetterRTX or LUT presets, you'll see several UAC prompts to restore those files). This is a guardrail, not an accident: the app's "Default" presets are generated from *your* game files the first time it runs. If you wipe the app's cache while your game no longer has its own defaults intact, those defaults are gone for good. **Press Yes on every UAC prompt.** If you don't, you risk  losing your game's defaults — recovering from that means reinstalling the game and running Hard Wipe again, since otherwise the app may mistake your non-default files for defaults and carry that mistake forward.

## Miscellaneous

![in-app-image](https://github.com/user-attachments/assets/2a37b0b5-cbfb-42a2-976d-03f309fa3b1d)

- Dynamic PSAs: Every feature in the app can display any number of "info" cards, as defined [here](https://github.com/Cubeir/Vanilla-RTX-App/blob/main/IN-APP-ANNOUNCEMENTS.md) They may also contain warnings, announcements, tutorials, or even brief you on Vanilla RTX's changelogs in its install menu. The PSAs are cached and automatically updated every few hours.

- Hovering any control in the app displays a unique pixel art communicating its function in the bottom left side of the app, for instance, sliders show how they should impact the visuals seen in-game as you change them, toggles show before/after, and buttons/other controls display a mildly artistic interpretation of what the they do!
> In combination with descriptive tooltips and non-static PSAs, this is meant to help make the app less intimidating and more beginner-friendly by presenting you with every resource you may need directly in the app.  
  
- Holding shift while clicking the lamp icon next to app's title will copy the app's stack trace to your clipboard for debugging, it may be helpful to attach these when reporting issues.

> The lamp is called Tuner Lamp, and it dynamically reacts to to the actions you take with the app. It can also occasionally display erratic behavior. All intended! Nothing's bugging out!

- The following settings persist between sessions: tuning options/slider values, Preview toggle, theme choice, and game install locations.
  Allowing you to keep your personal favorite values and quickly re-tune newer versions without having to remember everything, or come back to the app and swap DLSS again or reinstall your BetterRTX preset quickly in case of a Minecraft update.

- Top-left titlebar buttons in order:
  - Suspend UI Animations: Disables all custom UI animations, e.g. Tuner Lamp's reactivity (including the startup blink) as well as the typewriter animations and pixel art in the log area, fade effects, etc... 
     > Recommended to enable if you're sensitive to flashing images, bonus point: it makes the app open faster.
  - Cycle themes: Change between dark, light, or system theme.  
  - Help: Opens this page, which will hold up-to-date information about the app.  
  - Donate: Opens the developer's Ko-Fi page.  
    > When this button is hovered, an up-to-date list of Vanilla RTX Insiders is displayed. I'm able to maintain Vanilla RTX and other projects thanks to them. Consider becoming a supporter and have your name up there?!  
  - Invitation to the Vanilla RTX Discord server.

### Known Issues
- Missing icons on some Windows installations (limitation, non-critical)   
If you see missing icons (boxes/squares instead of symbols), your system is missing the Segoe Fluent Icons font. Download and install Segoe Fluent Icons from Microsoft:  
https://aka.ms/SegoeFluentIcons (or http://aka.ms/SegoeMDL2 for Windows 10)
- Non-English languages are not supported, there may be issues with other system languages. (untested, non-critical)
- The app's appearance is not properly accounted for with Windows accessibility features. (untested, non-critical)

### Need help?

Open an issue here, ask your questions or post suggestions/issues to forum channel of [Vanilla RTX Discord](https://discord.gg/A4wv4wwYud) — You can also alert @cubeir on [Minecraft RTX Community Discord](https://discord.gg/eKVKD3c). Anything works!

### Disclaimer

Vanilla RTX App (formely known as Vanilla RTX Tuner) is not associated or affiliated with Mojang Studios or Nvidia.
