# Changelog

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
