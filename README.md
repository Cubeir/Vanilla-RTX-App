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
  <img width="1920" height="1080" alt="vanilla-rtx-app-cover-render" src="https://github.com/user-attachments/assets/2af758a4-e047-4b74-8c6e-c9a72177decf" alt="vanilla-rtx-app-cover-render" />
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

1. Install and launch the Vanilla RTX App through [Microsoft Store](https://apps.microsoft.com/detail/9N6PCRZ5V9DJ) (or alternatively via a [winget](https://github.com/microsoft/winget-cli) command):
   ```powershell
   winget install "Vanilla RTX App"
   ```
3. Click **`Get latest RTX packs`**  
   - This opens a menu which allows you to swiftly install (or update) Vanilla RTX.
4. After installing a Vanilla RTX resource pack finishes, click **`Launch Minecraft RTX`**  
   - Starts the game with ray tracing enabled.
5. In Minecraft → **Settings → Global Resources → Enable one of the provided Vanilla RTX resource packs.**
6. Enter your world.

And you're done!  
Now ray tracing is enabled properly, you have the latest version of [Vanilla RTX](https://github.com/Cubeir/Vanilla-RTX), which is kept up-to-date for the latest version of Minecraft, with [bug fixes](https://minecraftrtx.net/enhancements) for as many issues as possible.  
This was a bare-bones guide for users who don't want any technical details and just want to get RTX working quickly.  
Below you'll find the full documentation of functionalities included in the app if you wish to take things further.  

## Documentation

This is your detailed guidebook on what each feature within the app does.

- `Launch Minecraft RTX`  
  Launches Minecraft with ray tracing pre-enabled, additionally disables VSync for better performance and enables in-game graphics mode switching, this circumvents issues such as these that make enabling ray tracing difficult:    
  [MCPE-191513](https://bugs.mojang.com/browse/MCPE/issues/MCPE-191513): Ray tracing can no longer be enabled while in the main menu.  
  [MCPE-152158](https://bugs.mojang.com/browse/MCPE/issues/MCPE-152158): PBR textures don't load properly upon enabling ray tracing after the game is freshly launched.  
  [MCPE-121850](https://bugs.mojang.com/browse/MCPE/issues/MCPE-121850): Ray Tracing performance starvation when VSync is enabled.
> Even if the game doesn't launch, it's okay, this can happen if the protocol to launch Minecraft isn't assigned on your computer. Just continue to launch the game manually as the app has already done it's main job at correcting the options for ray tracing.  

![vanilla-rtx-app-ui-screenshots](https://github.com/user-attachments/assets/09563ec6-0099-4afd-b2e5-73a3af930b02)

- `Get latest RTX packs`  
  Opens a window from which you can download and install/resintall, or update to the latest Vanilla RTX resource packs directly from the [Vanilla RTX GitHub repository](https://github.com/cubeir/Vanilla-RTX)  
  Vanilla RTX resource packs are up-to-date for the latest version of Minecraft and are constantly improving, in short, it is the ongoing effort to bring together a cohesive art direction for ray-traced Minecraft. This window lets you know if there are updates available and provides the most convenient way to stay updated. 
  Downloaded files remain cached for faster reinstalls, unless a new update becomes available to you, giving you a speedy way reset the packs back to original state for rapid experimentation with the tuning options explored later in this document.
  
  ![vanilla-rtx-app-ui-image](https://github.com/user-attachments/assets/65affd06-3396-4302-aca5-07952d593942)

- `DLSS version swapper`
Allows you to import and manage a catalogue of DLSS dll files for easy version switching. The currently-installed version is automatically catalogued, so you won't lose the original dll when replacing it, allowing you to revert easily. Accepts `.dll` files and `.zip` archives containing any number of dlls. You can also remove dll files from your catalogue that you no longer need. Your currently-installed version of DLSS is always highlighted.
> This feature is particularly essential to GeForce RTX 50 series users, as the current bundled DLSS version in Minecraft no longer works on their GPUs, for others upgrading to newer version of DLSS gives you sharper image quality and fewer artifacts.

- `BetterRTX manager`
Displays an up-to-date list of all available BetterRTX presets, which you can click to download and then install (Once a preset is finished downloading, it is moved to the top for convenience)
Upon installing your first ever preset, a Default RTX preset is created that stays at the very top allowing you to rollback/uninstall BetterRTX.
> All of your presets are wiped and the list is refreshed after game updates, the reason for that is to avoid potential game crashes, sometimes the files can become outdated, the app tracks changes in game files and removes your current presets in case of game updates, this ensures you aren't installing outdated RTX files by accident, and keeps your default RTX preset up-to-date.
You will have to redownload the presets as they may have likely been updated on [bedrock.graphics/api](https://bedrock.graphics/api), the app does its best to make sure you're always getting the latest, however, this depends entirely on BetterRTX updating itself just in time, but even if it didn't, this is not going to be a big issue, if you experience crashes after installing a BetterRTX preset, you can always rollback to the default preset. In other words if BetterRTX has delays after Minecraft updates, roll back to default in and occasionally hit the refresh button in the top left corner to manually reset the API and preset cache, refresh button won't touch your Default RTX preset.

> Note: Given the install paths can now vary compared to Minecraft UWP, the app first scans for the most common install locations, if that fails, a system-wide search takes place which will work if you give it time, but you're also given the choice to manually locate `Minecraft for Windows` or `Minecraft Preview for Windows` folders depending on the version of the game you're trying to swap DLSS of or install BetterRTX to. The app then caches the game's location after initial detection, making subsequent preset changes or DLSS swapping faster. The cache remains valid until Minecraft is relocated.

- `RTX lut manager`  
A simpler way to modify and improve Minecraft RTX's appearance that works across all game versions and Minecraft Preview, you can select from the list of pre-bundled presets, you can also [learn to create and submit your own lut preset](https://github.com/Cubeir/Vanilla-RTX-App/blob/main/LUT-CREATION-GUIDE.md) to see it included with future versions of the app.

- `Preview (Togglable)`  
All of the app's functionalities are targeted at Preview/Beta version of Minecraft instead of main release while  `Preview` is active. All app features and modules (with the exception of `BetterRTX manager`) fully support Minecraft Preview.
  
- `Export selection`  
  Exports selected packs. Useful for backing up your snapshot of the pack before making further changes, or to share your tuned version of Vanilla RTX (or another PBR resource pack) with friends! Since the app doesn't come with any quick way to reset PBR resource packs other than Vanilla RTX, it is recommended that you save them using this feature before tuning.

- `Coming later!`  
Currently a teaser for [RTX Reactor](http://minecraftrtx.net/reactor)'s merge with the Vanilla RTX App. Stay tuned!
  
## Tuner

The Vanilla RTX App includes tools to tune Vanilla RTX or any other RTX or Vibrant Visuals resource pack according to your preferences. Tuning options each use algorithms designed to preserve the PBR textures' composition, making it difficult to derail the pack's intended look while nudging it in your preferred direction. The whole tuning process works together with the app's other features to keep the experience streamlined and reversible.  

![vanilla-rtx-app-pack-selection-menu](https://github.com/user-attachments/assets/0b2dadce-a7ac-4d08-b44c-5c93618bcd02)

Upon launch, the app automatically scans for already-installed Vanilla RTX packs, available packs become selectable for tuning or export, you can also select up to one additional custom pack at a time.
>  Any action within the app that could result in pack stasus change refreshes the checkboxes automatically (such as toggling Preview or updating/reinstalling packs) if multiple versions of the same Vanilla RTX variant are present, the newest will be picked, but you can still select older versions manually from the list of local packs.

- `Select another pack`  
  Opens a menu containing a list of your installed RTX or Vibrant Visuals resource packs. You can select up to one pack to be added to the tuning or export queue alongside any of the 3 primary Vanilla RTX variants.
  
- `Fog multiplier`  
  Updates all fog densities by a given factor — e.g., `0.5` to halve, `3.0` to triple, or `0` to effectively disable air fog. If fog's density is already at 0, the multiplier is instead converted into and set as an acceptable literal number between `0.0-1.0`.
  If fog density is at maximum, excess of the multiplier will be used to scatter more light in the atmosphere. Underwater fog is also affected partially (to a much lesser degree to avoid messing underwater view distance, or the delicate colors seen in Vanilla RTX).

> Recommendation: If using a Vanilla RTX resource pack with BetterRTX installed, because of how differently fog appears compared to unmodded Minecraft RTX, tune fog with a `0.5-0.7` multiplier.

  ![fog-panel](https://github.com/user-attachments/assets/a013dc6a-bd46-41f1-b980-0620f0514588)

- `Emissivity multiplier`  
  Multiplies emissivity on blocks using a special formula that preserves the relative emissive values/keeps the composition intact, even if the multiplier is too high for a particular texture.
  
![street-default-vanilla-rtx](https://github.com/user-attachments/assets/19e802e9-42c6-4e70-a931-6474f5e10716)
![street-3x-emissivity-tuned-vanilla-rtx](https://github.com/user-attachments/assets/90e7d2a4-afdc-4250-9ecf-d5cc15fd9dc7)

- `Increase ambient light`  
Adds a small, uniform amount of emissivity to all surfaces, effectively increasing ambient light with ray tracing. This will result in a nightvision effect if made too strong.   
This option can work in conjunction with the Emissivity Multiplier — higher initial multipliers (e.g. 6.0) will amplify the effect.
Because changes stack on each tuning attempt, only use this once on freshly installed packs, and avoid setting higher emissivity multipliers than `1.0` on further consecutive tuning attempts.  
> For this reason, `Emissivity Multiplier` is automatically reset to default (1.0) if previous tuning attempt has had this option enabled, making it harder to break packs by accident.

![ambient-lighting](https://github.com/user-attachments/assets/e44fe5f8-06d0-4d43-978c-fc954f5d83e8)

- `Surface normal intensity`  
  Adjusts normal map and heightmap intensities. For instance, a value of 0 will flatten the textures in any given PBR resource pack. Larger values will increase the intensity of normal maps on blocks  This is done through a special algorithm that makes it impossible to lose relative intensity data even with extreme values.

- `Roughness control`  
  Increases roughness on materials using a decaying curve function to impact glossy surfaces more than already-rough surfaces, for example, high positive values allow alignment with Vibrant Visuals' PBR artstyle.
  Metalness is slightly reduced in relation to how much roughness is boosted. With negative values, extremely rough surfaces are instead made less rough, and metalness is amplified in relation to how much roughness boost would've taken place if the given value was a positive.
  
- `Lazify surface normals`  
  Uses a modified color texture to make heightmaps/normal maps less refined. The given number determines effectiveness `(0 = no change, 255 = fully lazy)`. For normal maps in particular, not all original detail is lost even at 255, instead it is partially blended and overlayed. Additionally, the final normal map always retains the same overall intensity as the starting normal map.  
  > Note: Assuming the resource pack isn't already shoddy, a tame value of `1-10` adds a subtle layer of organic detail.  
  > This option works only if the normal map or heightmap resolution matches the color texture; therefore, it has no impact on Opus variant of Vanilla RTX.  

- `Material grain offset`  
  Creates grainy materials by adding a layer of noise, input value determines the maximum allowed deviation from the original value.
  This is done safely with an algorithm that preserves pack's intended appearance while adding a layer of detail, emissives are affected minimally, additionally, noise patterns persist across animated textures or texture variations (e.g. a redstone lamp off and redstone lamp on retain the same noise pattern)
  The noise has a subtle checkerboard pattern that mimics the same noise seen on the PBR textures of default Vibrant Visuals while still giving the pack a slightly fresh look each time the noise is applied!

- `Tune selection`  
  Begins the tuning process with your given parameters and pack selections (That would be, the checked Vanilla RTX packages + one other local pack, if any was selected, if the exact same Vanilla RTX resource pack is selected twice, it won't be tuned twice)
  Packages are then processed locally, changes are permanent and stack on top of each other, unless a pack is updated or freshly reinstalled.

> These tools can be extraordinarily powerful when used correctly on the right pack, for instance, PBR textures in Vanilla RTX can be processed to fully match the "style" of most other vanilla PBR resource packs or even Mojang's Vibrant Visuals, however this statement won't be true the other way around!  

- `Clear Selection` 
  Clears the Vanilla RTX and custom pack selections.

- `Reset`  
  Resets tuning values and options to their defaults — this does not reset the pack back to its default state, to do that, you must reinstall the packages via the `Get latest RTX packs` or if it is a custom pack, manually reimport the original pack to Minecraft.

- `Wipe / Hard reset (Hold Shift + Reset)`  
  Wipes all of app's storage, as well as any temporary data, including: cached timestamps (like update check cooldowns), tuning options, cached pack locations or game locations, DLSS and BetterRTX catalogues, PSA messages, and more..., then restarts the app.
  Effectively making it as if the app was just freshly installed on your system, removing all its traces in the most comprehensive way.

## Miscellaneous

![in-app-image](https://github.com/user-attachments/assets/2a37b0b5-cbfb-42a2-976d-03f309fa3b1d)

- Hovering any control in the app displays a unique pixel art communicating its function in the bottom left side of the app, for instance, sliders show how they should impact the visuals seen in-game as you change them, toggles show before/after, and buttons/other controls display a mildly artistic interpretation of what the they do!
  In combination with descriptive tooltips, this is meant to help make the app less intimidating and more beginner-friendly.  
  
- Holding shift while clicking the lamp icon next to app's title will copy logs to your clipboard for debugging, it may be helpful to attach these when reporting issues. The same lamp also dynamically reacts to what you do while using the app.

- The following settings persist between sessions: tuning options/slider values, Preview toggle, theme choice, and game install locations.
  Allowing you to keep your personal favorite values and quickly re-tune newer versions without having to remember everything, or come back to the app and swap DLSS again or reinstall your BetterRTX preset quickly in case of a Minecraft update.

- Top-left titlebar buttons in order:
  - Cycle themes: Change between dark, light, or system theme.  
  - Help: Opens this page, which will hold up-to-date information about the app.  
  - Donate: Opens the developer's Ko-Fi page.  
    > When this button is hovered, an up-to-date list of Vanilla RTX Insiders is displayed. I'm able to maintain Vanilla RTX projects thanks to them. Consider becoming a supporter and have your name up there?!  
  - Invitation to the Vanilla RTX Discord server.

- The app may occasionally display PSAs from this readme file and caches it for several hours, these announcements will occasionally show up on startup. They are used to inform users of important issues, Vanilla RTX or App updates, etc...

### Known Issues
- Missing icons on some Windows installations (limitation, non-critical)   
If you see missing icons (boxes/squares instead of symbols), your system is missing the Segoe Fluent Icons font. Download and install Segoe Fluent Icons from Microsoft:  
https://aka.ms/SegoeFluentIcons (or http://aka.ms/SegoeMDL2 for Windows 10)
- Non-English languages are not supported, there may be issues with other system languages. (untested, non-critical)
- The app's appearance is not properly accounted for with Windows accessibility features. (untested, non-critical)
- Light theme is generally not as pretty as Dark theme (tested, non-critical, seriously? what's wrong with you? use dark theme)

### Need help?

Ask your questions or post suggestions/issues to forum channel of [Vanilla RTX Discord](https://discord.gg/A4wv4wwYud).
You can also alert @cubeir on [Minecraft RTX Community Discord](https://discord.gg/eKVKD3c).

### Disclaimer

Vanilla RTX App (formely known as Vanilla RTX Tuner) is not associated or affiliated with Mojang Studios or Nvidia.

### PSA
If you're seeing major visual glitches, or your game crashes after installing a BetterRTX preset, it's because BetterRTX API hasn't been updated or your game is outdated, go back to your default preset or update the game, hit the refresh button in the top left of the window to clear API's preset cache, so you can get the fresh version that works for the latest Minecraft update!
