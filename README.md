# Vanilla-RTX-App
Everything you'll ever need to easily acesss Minecraft RTX and unlock its full potential brought together in one place.  

All-in-one Vanilla RTX app lets you automatically update ray tracing packages for the latest Minecraft releases, tune various aspects of Vanilla RTX resource packs to your preferences, easily enable Minecraft's ray tracing, manage and install BetterRTX presets, swap or upgrade DLSS, and more...   
Ensuring ray tracing is accessible to new players, and frictionless for existing users—despite years of neglect from Mojang.
<!-- Microsoft Store badge -->
<p align="center">
  <a href="https://apps.microsoft.com/detail/9N6PCRZ5V9DJ?referrer=appbadge&mode=direct">
    <img src="https://get.microsoft.com/images/en-us%20dark.svg" width="400"/>
  </a>
</p>

<!-- Cover image -->
<p align="center">
  <img src="https://github.com/user-attachments/assets/b24ba844-f752-42a9-9e01-08d3ad376ce0" alt="vanilla-rtx-app-cover-render"/>
</p>

# Overview
<!-- Second Cover Image -->
<p align="center">
   <img src="https://github.com/user-attachments/assets/8a7a07b3-f241-4e20-9cfb-c5355c310b80" alt="vanilla-rtx-app-cover"/>
</p>
<!-- Badges -->
<p align="center">
  <a href="https://discord.gg/A4wv4wwYud">
    <img src="https://img.shields.io/discord/721377277480402985?style=flat-square&logo=discord&logoColor=F4E9D3&label=Discord&color=F4E9D3&cacheSeconds=3600"/>
  </a>
  <a href="https://ko-fi.com/cubeir">
    <img src="https://img.shields.io/badge/-support%20my%20work-F4E9D3?style=flat-square&logo=ko-fi&logoColor=F4E9D3&labelColor=555555"/>
  </a>
  <img src="https://img.shields.io/github/repo-size/Cubeir/Vanilla-RTX-App?style=flat-square&color=F4E9D3&label=Repo%20Size&cacheSeconds=3600"/>
  <img src="https://img.shields.io/github/last-commit/Cubeir/Vanilla-RTX-App?style=flat-square&color=F4E9D3&label=Last%20Commit&cacheSeconds=1800"/>
</p>

## Quick Guide (For Beginners)

1. **Install the app from the Microsoft Store, open the app.**  
2. Click **`Install latest RTX packages`**  
   - This installs the latest version of official Vanilla RTX resource packs (or updates it for you if an update is available)  
3. Then click **`Launch Minecraft RTX`**  
   - Starts the game with ray tracing enabled.
4. In Minecraft → **Settings → Global Resources → Enable one of the provided Vanilla RTX resource packs.**
5. Enter your world.

And you're done!  
Now ray tracing is enabled properly, you have the latest version of [Vanilla RTX](https://github.com/Cubeir/Vanilla-RTX), which is kept updated for the latest version of Minecraft, with [bug fixes](https://minecraftrtx.net/enhancements) for as many issues as possible.  
This guide was for users who don't want any technical breakdown and just want to get RTX working quickly.  
Below you'll find an up-to-date list of features & documentation of functionalities included in the app.   

## Documentation

This is your detailed guidebook on what each option within the app does.

- `Launch Minecraft RTX`  
  Launches Minecraft with ray tracing pre-enabled, additionally disables VSync for better performance and enables in-game graphics mode switching, this circumvents issues such as these that make enabling ray tracing difficult:    
  [MCPE-191513](https://bugs.mojang.com/browse/MCPE/issues/MCPE-191513): Ray tracing can no longer be enabled while in the main menu.  
  [MCPE-152158](https://bugs.mojang.com/browse/MCPE/issues/MCPE-152158): PBR textures don't load properly upon enabling ray tracing after the game is freshly launched.  
  [MCPE-121850](https://bugs.mojang.com/browse/MCPE/issues/MCPE-121850): Ray Tracing performance starvation when VSync is enabled.
> Even if the game doesn't launch, it's likely okay, this can happen if the protocol to launch Minecraft isn't assigned on your computer. Just continue to launch the game manually as the app has already done it's main job.

- `Install latest RTX packages`  
  Downloads and (re)installs the latest Vanilla RTX & Vanilla RTX Normals packages directly from the [Vanilla RTX GitHub repository](https://github.com/cubeir/Vanilla-RTX)  
  Vanilla RTX resource packs are up-to-date for the latest version of Minecraft and they are constantly improving, in short, they are the unofficial ongoing effort to bring the canonical default appearance of ray-traced Minecraft to life.  
  Downloaded files are cached, making pack reinstalls instant unless a new update is available. This serves two main purposes: first to give a quick way reset the packs back to original state, allowing you to rapidly experiment and learn in practice what the tuning options do, and second to ensure you're always getting the latest version of Vanilla RTX, acting like an auto-updater. The updater also ensures you will only ever have one instance of each Vanilla RTX pack installed across resource packs folders.  
  
The app might notify you of important updates on startup, Vanilla RTX is constantly evolving and adapting to the latest release version of Minecraft, however for the most reliable and up-to-date information, check the news channel on the [Vanilla RTX Discord server](https://discord.gg/A4wv4wwYud).
  
![vanilla-rtx-app-ui-in-game-images (2)](https://github.com/user-attachments/assets/c83f1da5-d4ec-407d-ae7e-91a3f936e2d7)

- `DLSS version swapper`
Allows you to import and manage a catalogue of DLSS dll files for easy version switching. The currently-installed version is automatically catalogued, so you won't lose the original dll when replacing it, allowing you to revert easily. Accepts `.dll` files and `.zip` archives containing any number of dlls. You can also remove dll files from your catalogue that you no longer need. Your currently-installed version of DLSS is always highlighted.
> This feature is particularly essential to RTX 50 series users, as the current bundled DLSS version in Minecraft no longer works on their GPUs, for others upgrading to the latest version of DLSS gives you sharper image as Minecraft's is heavily outdated. The app does not validate `.dll` files to belong to DLSS, it is your responsibility to provide valid DLSS files.

- `BetterRTX manager`
Installs and manages BetterRTX presets which you can download and import from BetterRTX's website. Supports `.rtpack` and `.zip` files which contain the `material.bin` files.  
You can create your own catalogue of presets to switch between instantly. On first use, the app automatically creates a "Default RTX" preset by backing up the game's original files, this preset cannot be deleted as it is the only way to restore default RTX (aside from game updates/reinstalls), your currently-selected preset is highlighted.

> Notes: The last 2 features only support Minecraft GDK (1.21.120 and higher), and given the install paths can now vary compared to Minecraft UWP, the app first scans for the most common install locations, if that fails, a system-wide search takes place which will work if you give it time, but you're also given the choice to manually locate `Minecraft for Windows` or `Minecraft Preview for Windows` folders depending on the version of the game you're trying to swap DLSS of or install BetterRTX to. The app then caches the game's location after initial detection, making subsequent preset changes or DLSS swapping faster. If the game is relocated, the cache is invalidated.

- `Preview (Toggle)`  
  All of the app's functionalities are targeted at Preview/Beta version of Minecraft instead of main release while  `Preview` is active.

- `Export selection`  
  Exports selected packs. Useful for backing up your snapshot of the pack before making further changes, or to share your tuned version of Vanilla RTX (or another PBR resource pack) with friends! Since the app doesn't come with any quick way to reset PBR resource packs other than Vanilla RTX, it is recommended that you save them using this feature before tuning.
  
## Tuner

The Vanilla RTX App includes tools to tune Vanilla RTX or any other RTX or Vibrant Visuals resource pack according to your preferences. Tuning options each use algorithms designed to preserve the PBR textures, making it difficult to derail the pack's intended look while nudging it in your preferred direction. The whole tuning process works together with the app's other features to keep the experience streamlined and reversible.  

![vanilla-rtx-app-pack-selection-menu](https://github.com/user-attachments/assets/0b2dadce-a7ac-4d08-b44c-5c93618bcd02)

Upon launch, the app automatically scans for already-installed Vanilla RTX packs, available packs become selectable for tuning or export, you can also select up to one additional custom pack at a time.
>  If Vanilla RTX packs are installed or reinstalled through the app, or if `Preview` button is toggled, checkboxes refresh. If multiple versions of the same Vanilla RTX variant are present, the newest will be picked, you can still select older versions manually from the list of local packs.

- `Select a local pack`  
  Opens a menu containing a list of your installed RTX or Vibrant Visuals resource packs. You can select one pack to be tuned alongside any of the 3 primary Vanilla RTX variants.
 > Holding `SHIFT` while pressing this button will instead trigger a Vanilla RTX version check which will appear in the logs.

- `Fog multiplier`  
  Updates all fog densities by a given factor — e.g., `0.5` to halve, `3.0` to triple, or `0` to effectively disable air fog. If a fog density is already at 0, the multiplier is instead converted into an acceptable literal number between `0.0-1.0`.
  If fog density is at maximum, excess of the multiplier will be used to scatter more light in the atmosphere. Underwater fog is also affected partially (to a much lesser degree to avoid exposing excessive underwater view distance).
  
  ![fog-panel](https://github.com/user-attachments/assets/a013dc6a-bd46-41f1-b980-0620f0514588)

- `Emissivity multiplier`  
  Multiplies emissivity on blocks using a special formula that preserves the relative emissive values/keeps the composition intact, even if the multiplier is too high for a particular texture.
  
![street-default-vanilla-rtx](https://github.com/user-attachments/assets/19e802e9-42c6-4e70-a931-6474f5e10716)
![street-3x-emissivity-tuned-vanilla-rtx](https://github.com/user-attachments/assets/90e7d2a4-afdc-4250-9ecf-d5cc15fd9dc7)

- `Increase ambient light`  
Adds a small amount of emissivity to all surfaces, effectively increasing ambient light with ray tracing. This will result in a nightvision effect if made too strong.   
This option can work in conjunction with the Emissivity Multiplier — higher initial multipliers (e.g. 6.0) will amplify the effect.
Because changes stack on each tuning attempt, only use this once on freshly installed packs, and avoid setting higher emissivity multipliers than `1.0` on further consecutive tuning attempts.  
> For this reason, `Emissivity Multiplier` is automatically reset to default (1.0) if previous tuning attempt has had this option enabled, making it harder to break packs.

![ambient-lighting](https://github.com/user-attachments/assets/e44fe5f8-06d0-4d43-978c-fc954f5d83e8)

- `Surface normal intensity`  
  Adjusts normal map and heightmap intensities. For instance, a value of 0 will flatten the textures in any given PBR resource pack. Larger values will increase the intensity of normal maps on blocks  This is done through a special algorithm that makes it impossible to lose relative intensity data even with extreme values.

- `Roughness control`  
  Increases roughness on materials using a decaying curve function to impact glossy surfaces more than already-rough surfaces, for example, high positive values allow alignment with Vibrant Visuals' PBR artstyle.
  Metalness is slightly reduced in relation to how much roughness is boosted. For negative values, extremely rough surfaces are instead made less rough, and metalness is amplified in relation to how much roughness boost would've taken place if the given value was a positive.
  
- `Lazify surface normals`  
  Uses a modified color texture to make heightmaps/normal maps less refined. The given number determines effectiveness `(0 = no change, 255 = fully lazy)`.
  > Note: Assuming the pack isn't already shoddy, a value of 1-10 can add a subtle layer of organic detailing to the textures.

- `Material grain offset`  
  Creates grainy materials by adding a layer of noise, input value determines the maximum allowed deviation.
  This is done safely with an algorithm that preserves pack's intended appearance while adding a layer of detail, emissives are affected minimally, additionally, noise patterns persist across animated textures or texture variations (e.g. a redstone lamp off and redstone lamp on retain the same noise pattern)
  The noise has a subtle checkerboard pattern that mimics the noise on PBR textures seen in default Vibrant Visuals while still giving the pack a slightly fresh look each time the noise is applied!

- `Tune selection`  
  Begins the tuning process with your given parameters and pack selections (That would be, the checked Vanilla RTX packages + one other local pack, if any was selected, if the exact same Vanilla RTX resource pack is selected twice, it won't be tuned)
  Packages are then processed locally, changes are permanent and stack on top of each other, unless the pack is updated or freshly reinstalled.

> These tools can be extraordinarily powerful when used correctly on the right pack, for instance, PBR textures in Vanilla RTX can be processed to fully match the "style" of Mojang's Vibrant Visuals or most other vanilla PBR resource packs, however this statement won't be true the other way around!  

- `Clear Selection` 
  Clears the Vanilla RTX and custom pack selections, as if you just booted up the app!

- `Reset`  
  Resets tuning values and options to their defaults — this does not reset the pack back to its default state, to do that, you must reinstall the packages via the `(Re)install latest RTX packs` or if it is a custom pack, manually reimport the original pack to Minecraft.

- `Hard Reset (Hold Shift + Reset)`  
  Wipes all of app's storage, as well as any temporary data, including: cache timestamps (like update check cooldowns), tuning options, cached pack locations or game locations, and more..., then restarts the app.
  Effectively making it as if the app was just freshly installed on your system, removing all traces.

## Miscellaneous

![in-app-image](https://github.com/user-attachments/assets/2a37b0b5-cbfb-42a2-976d-03f309fa3b1d)

- Hovering any control in the app displays a unique pixel art communicating its function in the bottom left side of the app, for instance, sliders show how they should impact the visuals seen in-game as you change them, toggles show before/after, and buttons/other controls display a mildly artistic interpretation of what the they do!
  In combination with descriptive tooltips, this is meant to help make the app less intimidating and more beginner-friendly.  
  
- Double-clicking the art artwork area will copy the logs for debugging, it may also be helpful to attach these when reporting issues.

- The following settings persist between sessions: tuning options/slider values, Preview toggle, theme choice, and game install locations.
  Allowing you to keep your personal favorite values and quickly re-tune newer versions without having to remember everything, or come back to the app and swap DLSS again or reinstall your BetterRTX preset quickly in case of a Minecraft update.

- Top-left titlebar buttons in order:
  - Cycle themes: Change between dark, light, or system theme.
  - Help: Opens this page, which will hold up-to-date information about the app.
  - Donate: Opens the developer's Ko-Fi page.  
    > When this button is hovered, an up-to-date list of Vanilla RTX Insiders is displayed. I'm able to maintain Vanilla RTX projects thanks to them. Consider becoming a supporter and have your name up there?!

- The app may occasionally display PSAs from this readme file and caches it for several hours, these announcements will be shown on startup. They are used to inform users of important issues, Vanilla RTX or App updates, etc...

### Known Issues
- Missing icons on some older Windows installations (non-critical)   
If you see missing icons (boxes/squares instead of symbols), your system is missing the Segoe Fluent Icons font. Download and install Segoe Fluent Icons from Microsoft:  
https://aka.ms/SegoeFluentIcons

### Need help?

Ask your questions or post suggestions/issues to forum channel of [Vanilla RTX Discord](https://discord.gg/A4wv4wwYud).
You can also alert @cubeir on [Minecraft RTX Community Discord](https://discord.gg/eKVKD3c).

### Disclaimer

Vanilla RTX App (formely known as Vanilla RTX Tuner) is not associated or affiliated with Mojang Studios or Nvidia.

### PSA
Be sure to update to 2.4.5.0 from the Microsoft Store! (There've been a few important fixes!)
