# Castlevania: Belmont's Curse DualSense Haptics

Real DualSense voice-coil haptics for Castlevania: Belmont's Curse on Steam, as a BepInEx 6 plugin.

* **HD haptics instead of rumble:** the PC version only sends Unity's basic two-motor rumble, which feels weak on a DualSense. The plugin catches that rumble and plays proper waveforms on the controller's left and right haptic actuators: a deep thump for the heavy motor, a sharper buzz for the light one, and an extra click on hits and landings.
* **Feel the sound effects (v0.3.5):** footsteps (alternating left/right), landings, jumps/wall moves/dodges, whip, weapon swings and hits, spells and arcana, unlocked abilities, healing, pickups and level-ups, the save mirror, menu and map navigation, enemy hits and staggers, breakables and taking damage, taken straight from the game's FMOD sound engine. Music, dialog, barks and ambience are never felt.
* **Tunable:** strength, frequencies, gains and left/right bleed in the config file.

## Requirements

* Castlevania: Belmont's Curse on Steam (Windows).
* [BepInEx 6 Bleeding Edge, Unity IL2CPP x64](https://builds.bepinex.dev/projects/bepinex_be), build **be.788** or newer.
* A DualSense controller connected by **USB cable** (haptics need USB; wireless only works through DSX with DSX+ "BT Audio/Haptics").
* Nothing plugged into the controller's headphone jack. A headset in the jack turns the haptics off.

## Install and run

1. Extract BepInEx into the game folder (next to the game's .exe) and start the game once so it can set itself up. The first start takes a while.
2. Download the latest zip from **Releases** and copy its `BepInEx` folder into the game folder. It merges into `BepInEx/plugins/BelmontHaptics`.
3. Plug in the DualSense by USB and start the game. You should feel a short chime when the haptics connect.

The game's own rumble is muted while the haptics are running and comes back by itself if the controller's audio device disappears.

## Settings

`BepInEx/config/amine.nr.belmonthaptics.cfg` is created on the first run:

* `Strength`, `LowMotorHz`, `HighMotorHz`, `LowGain`, `HighGain`, `SideBleed`, `TransientGain`
* `KeepNativeRumble` to also keep the game's original rumble
* `AudioHaptics` the old v0.2 mode that feels all game audio, music and voices included (off by default)
* `RumbleLayer` how much of the game's own rumble plays on top of the SFX haptics (0 = SFX only)
* `[SFX]` `SfxHaptics`, `SfxStrength`, `AudioDrive` (follow each sound's real loudness), `AudioSensitivity`, `ExcludeEvents`
* `[SFX.<Category>]` per category (Footsteps, Landing, Movement, Whip, Weapon, Spells, Damage, Healing, Abilities, Pickups, SaveMirror, MenuNavigate, Menu, EnemyHits, EnemyFeedback, Breakables): `Enabled`, `Events` (FMOD event path prefixes), `Gain`, `Hz`, `ReleaseMs`, `Kick`, `Side` (Both/Left/Right/Alternate)
* `[Discovery]` `FmodDiscoveryLog` turns the v0.3.0 sound-mapping logs back on (off by default)

## Sound mapping (FmodDiscoveryLog)

With `FmodDiscoveryLog = true` the plugin logs the game's FMOD sounds, handy for adding events to a category:

* `BepInEx/LogOutput.log` lists every sound bank, bus and event, then `[fmod-play]` lines the first time each sound plays (with the bus it goes through), bus levels every 2 seconds, and the most played sounds every 30 seconds.
* `BepInEx/BelmontHaptics_fmod.txt` has the full list of sounds with play counts.

## Files

* `CHANGELOG.md` what changed in each version.
* `source/BelmontHaptics/` the plugin source: `Plugin.cs` (hooks and settings), `HapticEngine.cs` (finds the DualSense audio device and makes the waveforms), `FmodNative.cs` and `FmodDiscovery.cs` (FMOD access and sound mapping), `FmodSfx.cs` (SFX categories and the FMOD taps), `GameTypes.cs` (finds the game's classes at runtime).

## Building it yourself

You need the .NET SDK (6 or newer). No game files or interop DLLs are needed: BepInEx comes from its NuGet feed and the game's classes are looked up when the game runs.

```
cd source/BelmontHaptics
dotnet build -c Release
```

Copy `BelmontHaptics.dll`, `NAudio.Core.dll` and `NAudio.Wasapi.dll` from `bin/Release/net6.0/` into `BepInEx/plugins/BelmontHaptics/`.

## How it works

* Harmony prefixes on `Gamepad.SetMotorSpeeds` and `DualSenseGamepadHID.SetMotorSpeeds` (plus Pause/Resume/Reset) catch the game's rumble, already scaled by the in-game vibration slider.
* The waveforms go to the DualSense's USB audio device (Speakers (DualSense Wireless Controller)), channels 3 and 4, which drive the left and right actuators.
* The FMOD part calls the game's own `fmodstudio.dll` directly. Each new instance of a whitelisted event gets a tiny pass-through DSP on its own channel group that measures that one sound's level on FMOD's mixer thread. Every category turns that level (plus an instant kick when the sound starts) into its own sine at its own frequency and grip, mixed on top of the rumble layer.

## Troubleshooting

* Look in `BepInEx/LogOutput.log` for `DualSense haptics connected`. If it says no DualSense audio device was found, check the USB cable.
* No vibration but the log says connected: unplug anything from the controller's headphone jack.
* Too strong or too weak: change `Strength` in the config.

## Disclaimer

This project is not affiliated with or endorsed by Konami, Evil Empire, Motion Twin or Sony. It contains no game files: it only ships its own plugin and the NAudio library. You need your own legitimate copy of the game. Use it at your own risk.

Castlevania is a trademark of Konami. PlayStation and DualSense are trademarks of Sony Interactive Entertainment. They are named here only to describe what the mod does.

## Credits

* [BepInEx](https://github.com/BepInEx/BepInEx) and [HarmonyX](https://github.com/BepInEx/HarmonyX).
* [NAudio](https://github.com/naudio/NAudio) by Mark Heath, MIT.

## License

MIT, see `LICENSE`.
