# Changelog

## 0.1.7

- Force all mod-added deposits into `Underground` rows by default so they do not affect atmosphere, surface water, or monthly habitability-state repartitioning.
- Normalize the shipped config and built-in defaults so every configured deposit state is `Underground`.
- Add cleanup for legacy mod-created non-underground rows, including old `Solid` rows that Solar Expanse could convert into gas/liquid/solid habitability inputs.
- Refresh object caches and habitability after cleanup so loaded saves recover without waiting for a monthly tick.

## 0.1.6

- Make resource injection one-time per campaign instead of a recurring top-up.
- Add a persistent applied-state file so restarting the game does not re-apply already injected campaign resources.
- Stop periodic rescans after the initial successful pass by default.
- Add duplicate cleanup for configured deposits created by older repeated-scan builds.
- Avoid creating tiny near-zero deposit rows.

## 0.1.5

- Prevent large-body reserve multiplication from applying to effective `Gas` and `Liquid` rows.
- Remap added large-body gas/liquid resources to `Underground` by default so large reserve floors remain available without mutating active atmosphere/ocean mass.
- Add cleanup for oversized legacy `Gas`/`Liquid` rows created by older mod versions.
- Fixes Earth and other major bodies becoming uninhabitable due to oversized atmosphere/ocean resource rows.

## 0.1.4

- Make the BepInEx plugin host persistent across Solar Expanse scene loads.
- Keep Harmony hooks installed if Unity destroys the host object.
- Hook closer to Solar Expanse object creation and save-load paths:
  - `Manager.ObjectInfoManager.SolarSystemLoad`
  - `Manager.LoadSaveManager.ExtractAllFromSaveData`
  - `Game.Info.ObjectInfo.Start`
  - `Game.Info.ObjectInfo.CustomInitialization`
  - `Game.Info.ObjectInfo.CustomExtractFromSaveGameData`
- Keep unscaled realtime polling as a fallback.
- Ship a default config for Solar Expanse `0.26.4.29.11 BETA`.

## 0.1.0

- Initial BepInEx plugin and configurable resource-deposit rules.
