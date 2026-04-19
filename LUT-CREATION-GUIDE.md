# Minecraft RTX: Look Up Table Modification Guide
In this short guide, you will learn to create and test your own LUT to customize Minecraft's ray tracing by editing the two primary files named `look_up_tables.png` and `sky.png`, and if you think you've came up with something cool or new, you can submit it through the forum channel of the [Vanilla RTX Discord](https://discord.gg/A4wv4wwYud) server to have it featured among the presets bundled with the app.

# Files to modify
The first step is to download and extract the [example files](https://www.mediafire.com/file/1lwqkcvh0ylupa8/Whimsical-Bright-Examples-LUT-Assets.zip/file).

Once you have `look_up_tables.png` and `sky.png` extracted, you need an image editing software, such as Microsoft Paint, [Paint.NET](http://paint.net/) or whatever you're comfortable with.

## `look_up_tables.png`
This file contains four rows of colors each with 128 columns, here's what each of the rows of color control:  
1. First row of colors: Controls the color of the directional lights throughout the day, i.e. the color of the light emitted by the sun, and later moon. Any color is accepted here.
2. Second row of colors: Controls skylight intensity throughout the day, which corresponds to both the intensity of `sky.png` as well as outdoor ambient lighting intensity.
3. Third row of colors: Controls directional light intensity throughout the day (i.e. Sun and Moon).
White RGB(255, 255, 255) correspond to strongest directional lights can be, while black turns off directional lights RGB(0, 0, 0)  
Furthermore , this impacts the opacity of sun and moon textures in the sky, as the directional lights get stronger, so do the textures of sun and moon.
Note: The brightness of the colors can also be used to control the intensity of the directional light in combination with `1.`, the effects compound.
3. Fourth row of colors: Unknown, does not seem to have an impact on anything, is fully white throughout the day by default.

The first column in each row corresponds to the time `noon` in Minecraft, while the last column corresponds to `midnight`; This means the look up table is read from left to right for one half of the Minecraft daylight cycle (noon -> midnight), and then in reverse from right to left for the other half (midnight -> noon).

By grasping the above, you can now begin modifying the look up table with precise knowledge of what colors and when it is going to leave an impact. For instance, if the start is noon, and the end is midnight, the columns close to the center correspond to both sunrise and sunset, the same colors are used for both, but they are simply read and play in reverse!


## `sky.png`
This is another low resolution image file that is upscaled and stretched all around the player to form a skydome.
Contains 8 strages, where the first three correspond to daytime, the last three to night time, and the middle ones to sunrise and sunset.  
Similar to `look_up_tables.png`, this file starts at `noon` and ends with `midnight`, then plays in reverse to go back from `midnight` to `noon`, this means the same limitation that sunset is also sunrise, but in reverse, applies here as well.

The image is 64 pixels wide and 256 pixels tall, each stage is 32 pixels tall and 64 pixels wide.
To understand how this image appears in-game, imagine as if two identical copies of each stage were put on the opposing sides of an sphere, and were then stretched so they meet at the front and back of the sphere, as well as the north and the south!
One side of the image is brighter, this side points towards the sun.


## `water_n.png`
This is not really a look up table, this file is meant to be a seamless normal map which the game engine animates in multiple directions to produce water waves.


## Testing
You can test your preset by first locating where your Minecraft Bedrock Edition is installed, this is usually the `XboxGames` folder in your `C:` drive, if not, it could also be at `C:\Program Files\Microsoft Games\Minecraft for Windows`, if you don't know where you game is installed and can't find it, try looking into Vanilla RTX App's log:  
First use a feature that requires game's location, such as BetterRTX manager, then in the main menu, hold Shiftkey and click the lamp icon in the titlebar, this exports the logs to your clipboard, paste them anywhere, assuming the app has successfully auto-located the game for you, you will see something along the lines
`✓ Found MicrosoftGame.Config at: C:\Program Files\Microsoft Games\Minecraft for Windows\Content\MicrosoftGame.Config`, where `C:\Program Files\Microsoft Games\Minecraft for Windows\Content` corresponds to game install location.  

Once you've located the game, head to `[Game Location]]\Content\data\ray_tracing` and paste your lut presets.  
Now you can launch Minecraft RTX through the Vanilla RTX App and test your very own lut preset.

## Done!
Congrats!
Keep playing around, once you've come up with something decently unique, share it in the forum channel at [Vanilla RTX Discord](https://discord.gg/A4wv4wwYud) and ping `@Cubeir` and you'll likely see it listed among the bundled presets with the future updates! You can also make a pull request.


Note: ideally, back up your default lut files before replacing them, if you've previously used the lut manager of the app, chances are it has the backups stored away, you'll have to reinstall your game at worst, or copy the same files from Minecraft Preview/Release into your other Minecraft installation that you made these changes to.

## Making a pull request for your lut preset
Create a folder inside `src\Assets\lut` folder of project's files, folder name determiens your preset name.  
Inside this folder, you have to provide four files:
1. `image.png`: This is your preset's preview image in lut manager
2. and 3. `look_up_tables.png` and `sky.png` these are your primary lut files.
4. `water_n.tga`: leave the default one if unchanged.

Create your pull request with these four files added inside a folder with the name of your choice.
The name of the folder has to be appropriate.
