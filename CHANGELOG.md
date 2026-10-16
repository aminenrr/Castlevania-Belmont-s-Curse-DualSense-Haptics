# Changelog

## v0.3.8
* DualSense Edge fix: the Edge could connect but stay silent because it was stuck in classic rumble mode. The mod now switches the controller back to native haptics mode when it connects (and keeps it there).
* New `[Device]` options `EdgeHapticsFix` and `DualSenseHapticsFix` (both on) to turn that off if needed.

## v0.3.7
* The config now updates itself: when a new version of the mod starts, it rewrites the config with the new defaults and saves the old one as `.cfg.bak`. No more deleting the file by hand.
* New `[Updates] KeepSettingsOnUpdate` option (off by default) to keep your own tweaks across updates instead.
* The old `amine.nr.belmonthaptics.cfg` is removed automatically.

## v0.3.6
* The config file is now `BepInEx/config/haptics.castlevania.belmontscurse.cfg`. The old `amine.nr.belmonthaptics.cfg` is no longer used and can be deleted.

## v0.3.5
* New categories: Abilities (traversal moves you unlock), EnemyFeedback (staggers, glancing and break reactions), Pickups (XP, items, potions, mana, level-ups).

## v0.3.4
* Menu haptics: a crisp tick when moving through the main menu, pause menu, tabs and the map; a softer pulse on open, confirm and close.

## v0.3.3
* Save mirror haptics.
* Category matching now uses the longest matching prefix, so a specific category can override a broad exclude.

## v0.3.2
* Slide, roll, kick, dive, stomp and back dodge in Movement; stomp and dive landings in Landing.
* Arcana and weapon charge in Spells, new Healing category, death and petrify in Damage.
* `[sfx-miss]` log lines for player sounds that no category handles yet.

## v0.3.1
* Haptics driven by the game's FMOD sound effects only (no music or voices), with per-category frequency, strength and side.

## v0.3.0-discovery
* FMOD sound mapping build used to find the game's event paths.

## v0.2.0
* First working release: the game's rumble turned into DualSense HD haptics over USB.
