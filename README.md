# Vanilla-RTX-App
Everything you'll ever need to easily access Minecraft RTX and unlock its full potential, brought together in one place.

All-in-one Vanilla RTX App lets you automatically update the ray tracing packages for the latest Minecraft releases, tune various aspects of Vanilla RTX resource packs to your preferences, easily enable Minecraft's ray tracing, manage and install BetterRTX shader mods, swap DLSS versions, and much more...  
Ensuring ray tracing is accessible to new players, and frictionless for existing users — despite years of neglect from Mojang.
<!-- Microsoft Store badge -->
<p align="center">
  <a href="https://apps.microsoft.com/detail/9N6PCRZ5V9DJ?referrer=appbadge&mode=direct">
    <img src="https://get.microsoft.com/images/en-us%20dark.svg" width="400"/>
  </a>
</p>

<!-- Cover image -->
<p align="center">
  <img alt="vanilla-rtx-app-cover-render" src="https://github.com/user-attachments/assets/2af758a4-e047-4b74-8c6e-c9a72177decf"/>
</p>

# Overview
<!-- Second Cover Image -->
<p align="center">
   <img alt="vanilla-rtx-app-cover" src="https://github.com/user-attachments/assets/8a7a07b3-f241-4e20-9cfb-c5355c310b80"/>
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
5. In Minecraft → **Settings → Global Resources → Enable one of the provided Vanilla RTX resource packs** that you just installed.
6. Enter your world. And you're done!

Now ray tracing is enabled properly, you have the latest version of [Vanilla RTX](https://github.com/Cubeir/Vanilla-RTX), which is kept up-to-date for the recent version of Minecraft, with [bug fixes](https://minecraftrtx.net/enhancements) for as many issues as possible. This was a bare-bones guide for users who don't want any technical details and just want to get RTX working quickly. Below you'll find the full documentation of functionalities included in the app if you wish to take things further.

## Documentation

This is your detailed guidebook on what each feature within the app does.

### How the App Is Organized

The app's features generally split into two groups, based on what they touch:

- **Game Modification Tools** — work directly with your Minecraft installation: BetterRTX, RTX LUT manager and DLSS swapper.
- **Resource Pack & User Data Tools** — work with the resource packs living in your Minecraft user data folder: installing and updating Vanilla RTX, tuning packs, RTX Reactor (soon), and launching the game itself with ray tracing enabled.

Both your game installations and your user data locations for Minecraft and Minecraft Preview (if installed) are detected automatically by the app — you will never need to think about this if using official Minecraft installations; However, if detection ever fails for you, see [Troubleshooting](#troubleshooting) for how to point the app in the right direction manually.

## User Data & Resource Pack Tools
- `Launch Minecraft RTX`  
  Launches Minecraft with ray tracing pre-enabled, additionally disables VSync for fixing performance issues and enables in-game graphics mode switching, this circumvents issues that make enabling ray tracing difficult, such as:  
  [MCPE-191513](https://bugs.mojang.com/browse/MCPE/issues/MCPE-191513): Ray tracing can no longer be enabled while in the main menu.  
  [MCPE-152158](https://bugs.mojang.com/browse/MCPE/issues/MCPE-152158): PBR textures don't load properly upon enabling ray tracing after the game is freshly launched.  
  [MCPE-121850](https://bugs.mojang.com/browse/MCPE/issues/MCPE-121850): Ray Tracing performance starvation when game's VSync is enabled.
> Holding shift while pressing this button will enable VSync instead of disabling it (not recommended, you should manually enable VSync from your GPU's Control Panel instead if you don't want screen tearing).

<img alt="Vanilla RTX App UI Images" src="https://github.com/user-attachments/assets/fe39284b-4275-4fb6-9339-26dec8057e5b" />

- `Get latest RTX packs`  
  Opens a window from which you can download and install, reinstall, or update Vanilla RTX resource packs (delivered from the [Vanilla RTX GitHub repository](https://github.com/cubeir/Vanilla-RTX)).
  > Vanilla RTX resource packs are up-to-date for the latest version of Minecraft and are constantly improving — in short, this is the ongoing effort to bring together a cohesive art direction for ray-traced Minecraft. This window lets you know if updates are available, shares update news, and is the most convenient way to stay current.

  Downloaded files remain cached for faster reinstalls until a new update becomes available, giving you a quick way to reset packs back to their original state for rapid experimentation with the tuning options covered below.
  > Note: updates delivered by the app will automatically replace your older installations of Vanilla RTX resource packs (per-pack).

<img alt="Vanilla RTX App UI Images" src="https://github.com/user-attachments/assets/faa01f9f-98bc-447f-8bde-cdfe7f152311" />

- `Preview (Toggle)`  
  While `Preview` is active, all of the app's functionality targets the Preview/Beta version of Minecraft instead of the main release. All features except `BetterRTX manager` support Minecraft Preview.

- `Export selection`  
  Exports selected packs. Useful for backing up your snapshot of a pack before making further changes, or for sharing your tuned version of Vanilla RTX (or another PBR resource pack) with friends! Since the app doesn't come with any quick way to reset PBR resource packs other than Vanilla RTX, it's recommended that you save them using this feature before using the Tuning features covered later in this document, and reimport in case you aren't satisfied with the results.

- `Delete selection`  
  Deletes your selected packs from your Minecraft user data directory. Worth noting: it walks up to the parent directory of where a pack's `manifest.json` resides and deletes the entire folder from the root of the `resource_packs` or `development_resource_packs` directory — something to be aware of if you happen to store other important files within your resource pack folders.

## Resource Pack Tuner
The Vanilla RTX App includes tools to tune Vanilla RTX, or any other Ray-Traced or Vibrant Visuals resource pack, to your preferences. Each tuning option uses an algorithm designed to preserve a PBR texture's original composition, making it hard to derail a pack's intended look while still nudging it in your preferred direction. Changes made to a resource pack's files are permanent.

Tuning is built to work alongside the app's other resource pack management features, so the whole process stays safe and easy to reverse: export a pack to back it up before tuning it, then, if you're not happy with the result, reimport the backup through the app. For Vanilla RTX specifically, it's even simpler — just hit Reinstall from the `Get latest RTX packs` menu.

![vanilla-rtx-app-pack-selection-menu](https://github.com/user-attachments/assets/0b2dadce-a7ac-4d08-b44c-5c93618bcd02)

The app keeps track of your installed Vanilla RTX resource packs, and their checkboxes become enabled for each one that is installed. For selecting resource packs other than Vanilla RTX, use the `Select other packs` menu, from which you may also `Import` more resource packs.

To tune a resource pack, select it, set the parameters you want, and hit Tune — this begins modifying the pack's files.

- `Select other packs` + `Import`
  Opens a menu containing a list of your installed resource packs. You can select any number of packs to perform bulk actions on them; every pack is listed and may carry different tags with different meanings:
  - Ray Traced: RTX resource packs that Tuner is capable of tuning.
  - Vibrant Visuals: resource packs that declare Vibrant Visuals capability and may also be tuned by the Tuner.
  - Incompatible with Tuner: other resource packs the Tuner should not have an impact on, but you can still select them for bulk exports or deletion.
  - RTX Reactor Candidate: resource packs that will potentially be suitable to run through RTX Reactor once it's available down the road.

  You can also import resource packs from this same menu; the following formats are supported:
  - `.mcpack`
  - `.mcaddon` (the app does not import behavior packs; however, multiple resource packs might be bundled inside one `.mcaddon` — a rare practice among resource pack authors, which the app supports nevertheless)
  - `.zip`

  > Features decide whether they can work with a pack or not based on its tag, Delete and Export can work regardless of the tag, resource packs marked as `Incomaptible with Tuner` will be ignored by Tuner, and resource packs without the `RTX Reactor Candidate` tag will be ignored by RTX Reactor.  

  > If the app is ever unable to locate your Minecraft user data automatically, this same button turns into a highlighted `Locate (Stable/Preview) User Data` button — see [Troubleshooting](#troubleshooting) for details.

- `Fog density multiplier`
  Updates all fog densities by a given factor — e.g., `0.5` to halve, `3.0` to triple, or `0.0` to effectively disable air fog. If a fog's density is already at zero, the multiplier is instead converted into, and set as, an acceptable literal number between `0.0` and `1.0`.
  If fog density is already at maximum, any excess of the multiplier is instead used to scatter more light into the atmosphere. Underwater fog is also partially affected (to a much lesser degree, to avoid disrupting underwater view distance or delicate colors, as seen for example in Vanilla RTX).

> Recommendation: If you're using a Vanilla RTX resource pack with BetterRTX installed, fog appears quite differently compared to unmodded Minecraft RTX — tune fog with a `0.5–0.7` multiplier to compensate.

  ![fog-panel](https://github.com/user-attachments/assets/a013dc6a-bd46-41f1-b980-0620f0514588)

- `Emissivity strength multiplier`
  Multiplies emissivity on blocks using a special formula that preserves relative emissive values and keeps composition intact, even if the multiplier is too high for a particular texture.

![street-default-vanilla-rtx](https://github.com/user-attachments/assets/19e802e9-42c6-4e70-a931-6474f5e10716)
![street-3x-emissivity-tuned-vanilla-rtx](https://github.com/user-attachments/assets/90e7d2a4-afdc-4250-9ecf-d5cc15fd9dc7)

- `Increase ambient light`
  Adds a small, uniform amount of emissivity to every surface, simulating ambient light under ray tracing. Push it too far and you'll get a night-vision effect.
  This option is scaled by the Emissivity Multiplier — a higher multiplier means stronger ambient lighting, whether it's the pack's first tuning pass or a later one.
> Warning: Tuning a pack with this option enabled marks it. On every tuning pass after that, the Emissivity Multiplier stops applying to the pack's regular emissivity, and continues to scale only the ambient light this option adds. This prevents the multiplier from compounding across repeated passes and blowing out the pack's lighting entirely. Because of this, it's best used sparingly, and generally as the last step once you're happy with everything else.

![ambient-lighting](https://github.com/user-attachments/assets/e44fe5f8-06d0-4d43-978c-fc954f5d83e8)

- `Surface normal intensity`
  Adjusts the intensity of normal maps and heightmaps (in percentage). A value of 0% flattens the textures, while larger values increase the intensity of normal maps on blocks. This is handled through a special algorithm that preserves composition and relative intensity data even at extreme values.

- `Roughness control`
  Increases roughness using a decaying curve, so glossy surfaces are affected more than already-rough ones — high positive values, for example, let you align a pack with Vibrant Visuals' PBR art style. Metalness is reduced slightly, in proportion to how much roughness was added.
  Negative values invert this: extremely rough surfaces are made less rough instead, and metalness is boosted in proportion to how much roughness *would have* been added had the value been positive.

  > This may sound convoluted, but the logic holds up in practice. Put simply: negative values make surfaces generally shinier, positive values make them less shiny. Apply it in multiple passes until you're satisfied with the result.

- `Lazify surface normals`
  Uses a modified color texture to make heightmaps/normal maps less refined. The given number determines effectiveness `(0 = no change, 255 = fully lazy)`. For normal maps in particular, not all original detail is lost even at 255 — instead it's partially blended and overlaid. The final normal map also always retains the same overall intensity as the starting normal map.
  > Note: assuming the resource pack isn't already shoddy, a tame value of `1–10` adds a subtle layer of organic detail. This option only works if the normal map or heightmap resolution matches the color texture; therefore, it has no impact on the Opus variant of Vanilla RTX.

- `Material grain offset`
  Creates grainy materials by adding a layer of noise; the input value determines the maximum allowed deviation from the original value.
  > This is done safely with an algorithm that preserves the pack's intended appearance while adding a layer of detail — emissives are affected minimally, and noise patterns persist across animated textures or texture variations (e.g. a redstone lamp off and a redstone lamp on retain the same noise pattern).

  The noise has a subtle checkerboard pattern that mimics the same noise seen on the PBR textures of default Vibrant Visuals, while still giving the pack a slightly fresh look each time the noise is applied.

- `Tune selection`
  Begins applying your chosen parameters to every resource pack you've selected. Changes are permanent, and each tuning pass stacks on top of the previous one, until the pack is updated or its original files are freshly reinstalled.

> While tuning, the button turns into `Abort tuning operation`, which can be used to stop the operation — textures already finished keep their changes, and anything not yet reached by the Tuner is left untouched. This also acts as a safety net for accidental clicks: the app takes a few seconds to collect the resources it needs before tuning actually starts, so if you abort quickly enough, likely nothing will have been modified.  

> These tools can be extraordinarily powerful when used correctly on the right pack — for instance, PBR textures in Vanilla RTX can be processed to fully match the style of most other vanilla-esque PBR resource packs, or even Mojang's Vibrant Visuals. This statement won't be true the other way around, however.

- `Clear selection`
  Clears all Vanilla RTX and custom pack selections.

- `Reset`
  Resets tuning values and options to their defaults — this does not reset packs back to their default state. To do that, reinstall the packages via `Get latest RTX packs`, or, for a custom pack, manually reimport the original/unmodified pack from the `Select other packs` menu.

- `Wipe / Hard reset (Hold Shift + Reset)`
  Completely wipes the app's storage and all temporary data — cached timestamps (e.g. update check cooldowns), tuning options, cached pack and game/user data locations, DLSS and BetterRTX caches, PSA messages, and everything else — then restarts the app. This is the most thorough way to remove every trace of the app, effectively returning it to a freshly-installed state.

  > This will also attempt to restore your game's files to their defaults (e.g. if you've installed BetterRTX or LUT presets, you'll see several UAC prompts to restore those files). This is a guardrail, not an accident: the app's "Default" presets are generated from *your* game files the first time it runs. If you wipe the app's cache while your game no longer has its own defaults intact, those defaults are gone for good. **Press Yes on every UAC prompt.** If you don't, you risk losing your game's defaults — recovering from that means reinstalling the game and running Hard Wipe again, since otherwise the app may mistake your non-default files for defaults and carry that mistake forward.

### Game Modification Tools

- `BetterRTX manager`
  Displays an up-to-date list of all available [BetterRTX](https://bedrock.graphics/) presets, which you can click to download and then install (once a preset finishes downloading, it's moved to the top for convenience).
  > Upon installing your first ever preset, a Default RTX preset is created from your local game files and pinned to the top, allowing you to roll back or uninstall BetterRTX at any time.

  The app watches for game updates on your system; when one is detected, all of your presets are wiped and the list refreshes automatically, guaranteeing you aren't installing outdated RTX files by accident and keeping your Default RTX preset up to date. You'll then need to redownload presets, as they've likely been updated on [bedrock.graphics/api](https://bedrock.graphics/api) as well — though this ultimately depends on BetterRTX itself updating in time, which can occasionally lag behind. The app also periodically checks for changes in that API on its own and resets your presets automatically to keep you off stale files. A manual refresh button is also provided, which clears the BetterRTX API and preset cache, but not your Default RTX preset (that one is cleared exclusively by game-update detection).

<img alt="Vanilla RTX App UI Images" src="https://github.com/user-attachments/assets/022acfe7-6b5e-4135-a11d-acfaea4d9f11" />

- `RTX LUT manager`
  A simpler way to modify and improve Minecraft RTX's appearance that works reliably across all game versions and Minecraft Preview. You can select from a growing list of pre-bundled presets. Currently, 4 distinct LUT presets are provided:
  >  - Gamescom 2019 Demo V2: attempts to restore Minecraft RTX's graphics from its early demos shown to the public, a viable alternative to the default preset.
  >  - Matrix Fever: based on [Matrix Fever](https://www.curseforge.com/minecraft-bedrock/texture-packs/matrix).
  >  - Night Night: features extremely dark nights, great for a horror vibe.
  >  - Whimsical Bright: the opposite of Night Night — whimsically colorful and bright!

  With more coming soon. Have an idea for one? Make a suggestion!

- `DLSS version swapper`
  Allows you to import and manage a catalogue of DLSS `.dll` files for easy version switching. The currently-installed version is automatically catalogued, so you won't lose the original DLL when replacing it, allowing you to revert easily. Accepts `.dll` files and `.zip` archives containing any number of DLLs. You can also remove DLL files from your catalogue that you no longer need. Your currently-installed version of DLSS is always highlighted.

## Miscellaneous

![in-app-image](https://github.com/user-attachments/assets/2a37b0b5-cbfb-42a2-976d-03f309fa3b1d)

- Holding shift while clicking the lamp icon next to the app's title will copy app's logs to your clipboard for debugging. It's helpful to attach this when reporting issues — see [Troubleshooting](#troubleshooting).
> The lamp is called the Tuner Lamp; it dynamically reacts to the actions you take within the app. It may also occasionally display erratic behavior. All intended! Nothing's bugging out!

- Dynamic PSAs: every feature in the app can display any number of "info" cards, as defined [here](https://github.com/Cubeir/Vanilla-RTX-App/blob/main/IN-APP-ANNOUNCEMENTS.md). These may contain anything, e.g. warnings, announcements, tutorials, or even a briefing on Vanilla RTX's changelogs in its install menu. PSAs are cached and can update themselves once every few hours. You may also dismiss some of them for varying time periods, as decided by the app.

- Hovering any control in the app displays a unique pixel art piece in the bottom-left of the app, communicating its function — for instance, sliders show how they'll impact the visuals seen in-game as you change them, toggles show a before/after, and buttons/other controls display a mildly artistic interpretation of what they do!
> Combined with descriptive tooltips and non-static PSAs, this is meant to make the app feel less intimidating and more beginner-friendly, by putting every resource you might need directly in front of you.

- Other titlebar buttons:
  - Suspend UI Animations: disables all custom UI animations — e.g. the Tuner Lamp's reactivity (including its startup blink), the typewriter and pixel-art animations in the log area, fade effects, and more.
     > Recommended if you're sensitive to flashing images; bonus: it also makes the app open faster.
  - Cycle themes: change between dark, light, or system theme.
  - Help: opens this page, which holds up-to-date information about the app.
  - Donate: opens the developer's Ko-Fi page.
    > When this button is hovered, an up-to-date list of Vanilla RTX Insiders is displayed. I'm able to maintain Vanilla RTX and other projects thanks to them. Consider becoming a supporter and having your name up there?!
  - Invitation to the Vanilla RTX Discord server.

- `RTX Reactor`  
  [RTX Reactor](http://minecraftrtx.net/reactor) is joining the Vanilla RTX App down the road; the window currently hosts its development news.

## Troubleshooting

### Minecraft Not Found, or User Data Missing?

Your user data is required for the following features: Selecting/importing other packs & managing them, detection of installed Vanilla RTX resource packs (for checkboxes to become tickable), `Get latest RTX packs`, `RTX Reactor`, `Launch Minecraft RTX` as well as pack selection and all Tuner features.  
The right-hand tools (`RTX LUT manager`, `BetterRTX manager`, `DLSS swapper`) work with your game files directly and don't need user data to function, but the app needs to know your game's actual install path.  

The app locates both automatically: your Minecraft installation (game files) and your Minecraft user data folder (where resource packs live). These are handled by two independent systems, and for the vast majority of users, they succeed silently.-

**Finding your game:** the app queries the Windows Package Manager directly for your install path and resolves it down to the physical location of your game files, regardless of where you've installed Minecraft. If that doesn't work, it falls back to searching common install paths across all your drives, followed up by a system-wide search — this usually resolves within a few seconds. If the app is ever unable to find your game automatically, it will ask you to manually locate it — when prompted, find and select `Minecraft.Windows.exe` directly, not its folder (select the right executable for the right version of the game, i.e. if you try to select Minecraft Preview's exe for Minecraft, the app will not accept!)

**Finding your user data:** the app looks for a valid Minecraft user data folder structure on your system. This requires that you've launched Minecraft at least once, so the folder actually exists to be found. If automatic detection fails, the `Select other packs` button in the main menu turns into a `Locate (Stable/Preview) User Data` button and stays highlighted until Minecraft's path resolved. Click to open folder picket, and select your user data folders, they're usually named `Minecraft Bedrock` or `Minecraft Bedrock Preview`, if you have trouble finding these locations (usually in `%appdata%`) you may be using an unofficial Minecraft Bedrock launcher, and you may be on your own for figuring out where it may store your user data.

### Game Doesn't Launch After Clicking "Launch Minecraft RTX"?
That's okay — this can happen if the protocol used to launch Minecraft isn't assigned on your computer. Just launch the game manually from here on; the app has likely already done its main job of correcting your options for ray tracing. (Unless it has failed to find your User Data, in which case, refer to the paragraph above.)

### Minecraft Crashes After Installing a BetterRTX Preset?
Roll back to the Default RTX preset from the `BetterRTX manager`. BetterRTX preset updates depend on the mod itself keeping pace with Minecraft updates, which can occasionally lag behind — if a preset is causing crashes, the default is always your safe fallback while you wait for an update. In the meantime you can use `RTX LUT manager` to change up the look of the game in the meantime which works reliably without needing updates.

### Known Issues
- Missing icons on some Windows installations (limitation, non-critical)
  If you see missing icons (boxes/squares instead of symbols), your system is missing the Segoe Fluent Icons font. Download and install Segoe Fluent Icons from Microsoft:
  https://aka.ms/SegoeFluentIcons (or http://aka.ms/SegoeMDL2 for Windows 10)
- Non-English languages are not supported; there may be issues with other system languages. (untested, non-critical)
- The app's appearance is not properly accounted for with Windows accessibility features. (untested, non-critical)

### Still Stuck? For assistance and reporting issues, or suggestions
Open an issue [here](https://github.com/Cubeir/Vanilla-RTX-App/issues), ask your questions, or post suggestions/issues to the forum channel of [Vanilla RTX Discord](https://discord.gg/A4wv4wwYud) — you can also alert @cubeir on the [Minecraft RTX Community Discord](https://discord.gg/eKVKD3c). Anything works!

When reporting a bug, it helps a lot to attach application logs *after* you've performed the steps to produce an issue — shift-click the lamp icon next to the app's title to copy them to your clipboard (see [Miscellaneous](#miscellaneous)).

### Disclaimer

Vanilla RTX App (formerly known as Vanilla RTX Tuner) is not associated or affiliated with Mojang Studios or Nvidia.
