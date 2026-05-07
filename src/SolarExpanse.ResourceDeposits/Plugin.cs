using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using Newtonsoft.Json;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace SolarExpanse.ResourceDeposits
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public sealed class Plugin : BaseUnityPlugin
    {
        private const string PluginGuid = "noahh.solarexpanse.resourcedeposits";
        private const string PluginName = "Solar Expanse Resource Deposits";
        private const string PluginVersion = "0.1.7";
        private const string ConfigFileName = "SolarExpanse.ResourceDeposits.json";
        private const string AppliedStateFileName = "SolarExpanse.ResourceDeposits.applied.json";

        private ManualLogSource _log;
        private DepositConfig _config;
        private string _configPath;
        private string _appliedStatePath;
        private AppliedState _appliedState = new AppliedState();
        private Type _objectInfoManagerType;
        private Type _rowResourcesDataType;
        private Type _resourceStateType;
        private readonly Dictionary<string, object> _resourceCache = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _warningOnce = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _appliedKeysThisSession = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> _recentCampaignKeys = new List<string>();
        private Harmony _harmony;
        private int _lastObjectCount = -1;
        private int _emptyScanCount;
        private float _nextUpdateScanTime;
        private string _currentCampaignKey = "runtime";
        private bool _initialApplicationComplete;
        private bool _appliedStateDirty;
        private static Plugin _instance;

        private void Awake()
        {
            _log = Logger;
            _instance = this;
            DontDestroyOnLoad(gameObject);
            gameObject.hideFlags = HideFlags.HideAndDontSave;
            LoadConfig();
            LoadAppliedState();
            InstallHarmonyHooks();
            SceneManager.sceneLoaded += OnSceneLoaded;
            TryApplySafe("awake");
            StartCoroutine(ApplyLoop());
            _log.LogInfo($"{PluginName} {PluginVersion} loaded. Config: {_configPath}");
        }

        private void OnDestroy()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
            _log?.LogWarning("Plugin MonoBehaviour received OnDestroy. Keeping static Harmony hooks installed for this process.");
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            _lastObjectCount = -1;
            _nextUpdateScanTime = 0f;
            _initialApplicationComplete = false;
            TryApplySafe("sceneLoaded:" + scene.name);
        }

        private void Update()
        {
            if (_config == null || !_config.enabled)
            {
                return;
            }

            if (_initialApplicationComplete && !_config.continuousRescan)
            {
                return;
            }

            if (Time.realtimeSinceStartup < _nextUpdateScanTime)
            {
                return;
            }

            var seconds = Mathf.Max(2f, _config.scanIntervalSeconds);
            _nextUpdateScanTime = Time.realtimeSinceStartup + seconds;
            TryApplySafe("update");
        }

        private IEnumerator ApplyLoop()
        {
            while (true)
            {
                if (!_initialApplicationComplete || (_config != null && _config.continuousRescan))
                {
                    TryApplySafe("coroutine");
                }

                var seconds = _config == null ? 8f : Mathf.Max(2f, _config.scanIntervalSeconds);
                yield return new WaitForSecondsRealtime(seconds);
            }
        }

        private void TryApplySafe(string trigger)
        {
            try
            {
                TryApply(trigger);
            }
            catch (Exception ex)
            {
                _log.LogError($"Apply failed during {trigger}: {ex}");
            }
        }

        private void InstallHarmonyHooks()
        {
            _harmony = new Harmony(PluginGuid);
            var patched = 0;
            patched += PatchApplyTrigger("Manager.ObjectInfoManager", "SolarSystemLoad") ? 1 : 0;
            patched += PatchApplyTrigger("Manager.LoadSaveManager", "ExtractAllFromSaveData") ? 1 : 0;
            patched += PatchApplyTrigger("Game.Info.ObjectInfo", "Start") ? 1 : 0;
            patched += PatchApplyTrigger("Game.Info.ObjectInfo", "CustomInitialization") ? 1 : 0;
            patched += PatchApplyTrigger("Game.Info.ObjectInfo", "CustomExtractFromSaveGameData") ? 1 : 0;
            patched += PatchApplyTrigger("Manager.LoadSaveManager", "AfterLoadState") ? 1 : 0;
            _log.LogInfo($"Installed {patched} lifecycle hook(s) for resource deposit application.");
        }

        private bool PatchApplyTrigger(string typeName, string methodName)
        {
            try
            {
                var type = FindType(typeName);
                var method = type?.GetMethod(methodName, BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                var postfix = typeof(Plugin).GetMethod(nameof(ApplyTriggerPostfix), BindingFlags.Static | BindingFlags.NonPublic);
                if (method == null || postfix == null)
                {
                    WarnOnce("missing-hook-" + typeName + "." + methodName, $"Lifecycle hook target not found: {typeName}.{methodName}");
                    return false;
                }

                _harmony.Patch(method, postfix: new HarmonyMethod(postfix));
                _log.LogInfo($"Patched lifecycle hook: {typeName}.{methodName}");
                return true;
            }
            catch (Exception ex)
            {
                WarnOnce("hook-error-" + typeName + "." + methodName, $"Could not patch lifecycle hook {typeName}.{methodName}: {ex.Message}");
                return false;
            }
        }

        private static void ApplyTriggerPostfix()
        {
            _instance?.TryApplySafe("harmonyPostfix");
        }

        private void LoadConfig()
        {
            _configPath = Path.Combine(Paths.ConfigPath, ConfigFileName);
            Directory.CreateDirectory(Paths.ConfigPath);

            if (!File.Exists(_configPath))
            {
                var defaultConfig = DefaultDepositConfig.Create();
                File.WriteAllText(_configPath, JsonConvert.SerializeObject(defaultConfig, Formatting.Indented), Encoding.UTF8);
                _config = defaultConfig;
                return;
            }

            try
            {
                var json = File.ReadAllText(_configPath, Encoding.UTF8);
                _config = JsonConvert.DeserializeObject<DepositConfig>(json) ?? DefaultDepositConfig.Create();
                _config.Normalize();
            }
            catch (Exception ex)
            {
                _config = DefaultDepositConfig.Create();
                WarnOnce("config-load", $"Could not load {ConfigFileName}; using built-in defaults. {ex.Message}");
            }
        }

        private void LoadAppliedState()
        {
            _appliedStatePath = Path.Combine(Paths.ConfigPath, AppliedStateFileName);
            Directory.CreateDirectory(Paths.ConfigPath);

            if (!File.Exists(_appliedStatePath))
            {
                _appliedState = new AppliedState();
                return;
            }

            try
            {
                var json = File.ReadAllText(_appliedStatePath, Encoding.UTF8);
                _appliedState = JsonConvert.DeserializeObject<AppliedState>(json) ?? new AppliedState();
                _appliedState.Normalize();
            }
            catch (Exception ex)
            {
                _appliedState = new AppliedState();
                WarnOnce("applied-state-load", $"Could not load {AppliedStateFileName}; this run will still be guarded in memory. {ex.Message}");
            }
        }

        private void SaveAppliedStateIfDirty()
        {
            if (!_appliedStateDirty || _appliedStatePath == null)
            {
                return;
            }

            try
            {
                _appliedState.Normalize();
                File.WriteAllText(_appliedStatePath, JsonConvert.SerializeObject(_appliedState, Formatting.Indented), Encoding.UTF8);
                _appliedStateDirty = false;
            }
            catch (Exception ex)
            {
                WarnOnce("applied-state-save", $"Could not save {AppliedStateFileName}; this run remains guarded in memory only. {ex.Message}");
            }
        }

        private void TryApply(string trigger)
        {
            if (_config == null || !_config.enabled)
            {
                return;
            }

            ResolveTypes();
            RefreshCampaignKey();
            if (_initialApplicationComplete && !_config.continuousRescan)
            {
                return;
            }

            if (_objectInfoManagerType == null || _rowResourcesDataType == null)
            {
                WarnOnce("missing-core-types", "Solar Expanse runtime types are not loaded yet.");
                return;
            }

            var objectInfos = GetObjectInfos();
            if (objectInfos.Count == 0)
            {
                _emptyScanCount++;
                if (_emptyScanCount == 1 || _emptyScanCount % 10 == 0)
                {
                    _log.LogInfo($"No ObjectInfoManager/allObjectInfos ready yet during {trigger}. Empty scans: {_emptyScanCount}");
                }

                return;
            }

            _emptyScanCount = 0;
            if (objectInfos.Count != _lastObjectCount)
            {
                _lastObjectCount = objectInfos.Count;
                _log.LogInfo($"Scanning {objectInfos.Count} Solar Expanse objects for resource deposit minimums during {trigger}.");
            }

            var addedRows = 0;
            var cleanedRows = 0;
            foreach (var objectInfo in objectInfos)
            {
                if (objectInfo == null)
                {
                    continue;
                }

                if (_config.onlyAddToMineableObjects && !GetBoolMember(objectInfo, "CanMineResources", true))
                {
                    continue;
                }

                var objectChanged = false;
                foreach (var rule in _config.rules)
                {
                    if (rule == null || rule.deposits == null || !RuleMatches(objectInfo, rule))
                    {
                        continue;
                    }

                    foreach (var deposit in rule.deposits)
                    {
                        if (deposit == null || string.IsNullOrWhiteSpace(deposit.resourceId) || deposit.minimumAmount <= 0d)
                        {
                            continue;
                        }

                        if (EnsureDeposit(objectInfo, rule, deposit, out var depositCleanedRows))
                        {
                            addedRows++;
                            objectChanged = true;
                        }

                        if (depositCleanedRows > 0)
                        {
                            objectChanged = true;
                        }

                        cleanedRows += depositCleanedRows;
                    }
                }

                if (objectChanged)
                {
                    RefreshObjectAfterDepositChange(objectInfo);
                }
            }

            if (addedRows > 0 || cleanedRows > 0)
            {
                _log.LogInfo($"One-time resource pass changed {addedRows} deposit row(s) and cleaned {cleanedRows} duplicate/unsafe row(s).");
            }

            _initialApplicationComplete = true;
            SaveAppliedStateIfDirty();
            _log.LogInfo($"Completed one-time resource deposit scan for campaign '{_currentCampaignKey}' during {trigger}.");
        }

        private void RefreshCampaignKey()
        {
            var resolved = ResolveCampaignKey();
            if (string.IsNullOrWhiteSpace(resolved))
            {
                resolved = "runtime";
            }

            if (string.Equals(_currentCampaignKey, resolved, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            RememberRecentCampaignKey(_currentCampaignKey);
            _currentCampaignKey = resolved;
            _appliedKeysThisSession.Clear();
            _initialApplicationComplete = false;
            _lastObjectCount = -1;
        }

        private void RememberRecentCampaignKey(string campaignKey)
        {
            if (!IsPersistentCampaignKey(campaignKey) ||
                _recentCampaignKeys.Any(key => string.Equals(key, campaignKey, StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            _recentCampaignKeys.Add(campaignKey);
            if (_recentCampaignKeys.Count > 4)
            {
                _recentCampaignKeys.RemoveAt(0);
            }
        }

        private string ResolveCampaignKey()
        {
            var gameManagerType = FindType("Manager.GameManager");
            var gameManager = GetSingletonInstance(gameManagerType) ?? FindUnityObject(gameManagerType);
            var startGameConfig = GetMemberValue(gameManager, "StartGameConfig") ?? GetMemberValue(gameManager, "startGameConfig");
            var gameplayId = GetStringMember(startGameConfig, "gameplayID");
            if (!string.IsNullOrWhiteSpace(gameplayId))
            {
                return "gameplay:" + gameplayId.Trim();
            }

            var loadSaveManagerType = FindType("Manager.LoadSaveManager");
            var loadSaveManager = GetSingletonInstance(loadSaveManagerType) ?? FindUnityObject(loadSaveManagerType);
            var lastSaveName = GetStringMember(loadSaveManager, "LastSaveName") ?? GetStringMember(loadSaveManager, "lastSaveName");
            if (!string.IsNullOrWhiteSpace(lastSaveName))
            {
                return "save:" + lastSaveName.Trim();
            }

            return "runtime";
        }

        private bool IsPersistentCampaignKey(string campaignKey)
        {
            return campaignKey != null &&
                (campaignKey.StartsWith("gameplay:", StringComparison.OrdinalIgnoreCase) ||
                 campaignKey.StartsWith("save:", StringComparison.OrdinalIgnoreCase));
        }

        private bool IsApplicationRecorded(string applicationKey)
        {
            if (string.IsNullOrWhiteSpace(applicationKey))
            {
                return false;
            }

            if (_appliedKeysThisSession.Contains(applicationKey))
            {
                return true;
            }

            if (!_config.applyOncePerCampaign || !IsPersistentCampaignKey(_currentCampaignKey))
            {
                return false;
            }

            var state = GetCampaignAppliedState(_currentCampaignKey);
            return state.appliedKeys.Any(key => string.Equals(key, applicationKey, StringComparison.OrdinalIgnoreCase));
        }

        private void RecordApplication(string applicationKey)
        {
            if (string.IsNullOrWhiteSpace(applicationKey) || !_config.applyOncePerCampaign)
            {
                return;
            }

            _appliedKeysThisSession.Add(applicationKey);
            if (!IsPersistentCampaignKey(_currentCampaignKey))
            {
                return;
            }

            var state = GetCampaignAppliedState(_currentCampaignKey);
            if (state.appliedKeys.Any(key => string.Equals(key, applicationKey, StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            state.appliedKeys.Add(applicationKey);
            state.lastAppliedUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
            _appliedStateDirty = true;
        }

        private CampaignAppliedState GetCampaignAppliedState(string campaignKey)
        {
            _appliedState.Normalize();
            if (!_appliedState.campaigns.TryGetValue(campaignKey, out var state) || state == null)
            {
                state = new CampaignAppliedState { campaignKey = campaignKey };
                _appliedState.campaigns[campaignKey] = state;
                _appliedStateDirty = true;
            }

            state.appliedKeys = state.appliedKeys ?? new List<string>();
            return state;
        }

        private string BuildApplicationKey(object objectInfo, DepositRule rule, DepositSpec deposit, string effectiveStateName, double targetMinimum)
        {
            var objectId = GetIntMember(objectInfo, "id", -1);
            var objectKey = objectId >= 0
                ? "id:" + objectId.ToString(CultureInfo.InvariantCulture)
                : "name:" + Normalize(GetBestObjectName(objectInfo));

            return string.Join("|", new[]
            {
                objectKey,
                Normalize(rule.name),
                Normalize(deposit.resourceId),
                Normalize(effectiveStateName),
                targetMinimum.ToString("R", CultureInfo.InvariantCulture),
                Math.Max(0.001f, deposit.miningFactor).ToString("R", CultureInfo.InvariantCulture)
            });
        }

        private bool EnsureDeposit(object objectInfo, DepositRule rule, DepositSpec deposit, out int cleanedRows)
        {
            var effectiveStateName = GetEffectiveDepositStateName(objectInfo, deposit);
            var targetMinimum = GetEffectiveMinimumAmount(objectInfo, deposit, effectiveStateName);
            cleanedRows = CleanupLegacyHabitabilityStateRows(objectInfo, rule, deposit, effectiveStateName, targetMinimum);
            cleanedRows += CleanupLegacyUnsafeFluidDepositRows(objectInfo, rule, deposit);
            cleanedRows += CleanupDuplicateConfiguredDepositRows(objectInfo, rule, deposit, effectiveStateName, targetMinimum);

            var applicationKey = BuildApplicationKey(objectInfo, rule, deposit, effectiveStateName, targetMinimum);
            if (_config.applyOncePerCampaign && IsApplicationRecorded(applicationKey))
            {
                return false;
            }

            var existingTotal = GetExistingResourceTotal(objectInfo, deposit.resourceId, effectiveStateName);
            var missingAmount = targetMinimum - existingTotal;
            if (missingAmount <= Math.Max(0d, _config.minimumAddAmount))
            {
                RecordApplication(applicationKey);
                return false;
            }

            var resource = ResolveResourceDefinition(deposit.resourceId);
            if (resource == null)
            {
                WarnOnce("missing-resource-" + deposit.resourceId, $"Resource definition not found: {deposit.resourceId}");
                return false;
            }

            var row = Activator.CreateInstance(_rowResourcesDataType);
            SetMemberValue(row, "ResourcesType", resource);
            SetMemberValue(row, "Value", missingAmount);
            SetMemberValue(row, "MiningFactor", Mathf.Clamp(deposit.miningFactor <= 0f ? 0.01f : deposit.miningFactor, 0.001f, 1f));
            SetMemberValue(row, "ForcePrimary", deposit.forcePrimary);

            var state = ParseResourceState(effectiveStateName);
            if (state != null)
            {
                SetMemberValue(row, "ResourceState", state);
            }

            var addDeposit = objectInfo.GetType().GetMethod("AddDeposit", BindingFlags.Instance | BindingFlags.Public);
            if (addDeposit == null)
            {
                WarnOnce("missing-adddeposit", "ObjectInfo.AddDeposit was not found.");
                return false;
            }

            addDeposit.Invoke(objectInfo, new object[] { row, deposit.fullyExplored });

            var objectName = GetBestObjectName(objectInfo);
            _log.LogInfo($"{rule.name}: added {missingAmount.ToString("0", CultureInfo.InvariantCulture)} {deposit.resourceId} to {objectName} as {effectiveStateName}.");
            RecordApplication(applicationKey);
            return true;
        }

        private int CleanupLegacyHabitabilityStateRows(object objectInfo, DepositRule rule, DepositSpec deposit, string effectiveStateName, double targetMinimum)
        {
            if (!_config.cleanupLegacyHabitabilityStateDeposits ||
                !string.Equals(Normalize(effectiveStateName), "UNDERGROUND", StringComparison.Ordinal) ||
                targetMinimum <= 0d)
            {
                return 0;
            }

            var rows = GetResourceRows(objectInfo)
                .Where(row =>
                    string.Equals(GetRowResourceId(row), deposit.resourceId, StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(Normalize(GetRowResourceStateName(row)), "UNDERGROUND", StringComparison.Ordinal))
                .ToList();
            if (rows.Count == 0)
            {
                return 0;
            }

            var hasRecordedLegacyApplication = HasRecordedLegacyNonUndergroundApplication(objectInfo, rule, deposit);
            var candidates = rows
                .Where(row => ShouldCleanLegacyHabitabilityRow(row, targetMinimum, hasRecordedLegacyApplication))
                .ToList();
            if (candidates.Count == 0)
            {
                return 0;
            }

            var removeDeposit = objectInfo.GetType().GetMethod("RemoveDeposit", BindingFlags.Instance | BindingFlags.Public);
            if (removeDeposit == null)
            {
                WarnOnce("missing-removedeposit-habitability", "ObjectInfo.RemoveDeposit was not found; cannot clean legacy habitability-affecting rows.");
                return 0;
            }

            var totalRemoved = candidates.Sum(row => GetDoubleMember(row, "Value", 0d));
            foreach (var row in candidates)
            {
                removeDeposit.Invoke(objectInfo, new[] { row });
            }

            _log.LogInfo($"{rule.name}: removed {candidates.Count} legacy non-underground row(s) totaling {totalRemoved.ToString("0", CultureInfo.InvariantCulture)} {deposit.resourceId} from {GetBestObjectName(objectInfo)} so the modded reserve can be restored as Underground.");
            return candidates.Count;
        }

        private bool ShouldCleanLegacyHabitabilityRow(object row, double targetMinimum, bool hasRecordedLegacyApplication)
        {
            if (hasRecordedLegacyApplication)
            {
                return true;
            }

            var rowState = GetRowResourceStateName(row);
            var fallbackStates = _config.cleanupLegacyHabitabilityFallbackStates ?? new List<string> { "Solid" };
            if (!fallbackStates.Any(state => string.Equals(Normalize(state), Normalize(rowState), StringComparison.Ordinal)))
            {
                return false;
            }

            var rowValue = GetDoubleMember(row, "Value", 0d);
            var threshold = Math.Max(
                Math.Max(0d, _config.cleanupLegacyHabitabilityMinimumValue),
                targetMinimum * Math.Max(0d, _config.cleanupLegacyHabitabilityMinimumFraction));
            return rowValue >= threshold;
        }

        private bool HasRecordedLegacyNonUndergroundApplication(object objectInfo, DepositRule rule, DepositSpec deposit)
        {
            foreach (var state in new[] { "Solid", "Liquid", "Gas" })
            {
                foreach (var targetMinimum in GetLegacyTargetMinimumCandidates(objectInfo, deposit))
                {
                    var applicationKey = BuildApplicationKey(objectInfo, rule, deposit, state, targetMinimum);
                    if (IsApplicationRecordedForCurrentCampaignOrSession(applicationKey))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private IEnumerable<double> GetLegacyTargetMinimumCandidates(object objectInfo, DepositSpec deposit)
        {
            var baseAmount = Math.Max(0d, deposit.minimumAmount);
            yield return baseAmount;

            if (IsLargeBody(objectInfo))
            {
                var multiplied = baseAmount * Math.Max(1d, _config.largeBodyReserveMultiplier);
                if (!multiplied.Equals(baseAmount))
                {
                    yield return multiplied;
                }
            }
        }

        private bool IsApplicationRecordedForCurrentCampaignOrSession(string applicationKey)
        {
            if (string.IsNullOrWhiteSpace(applicationKey))
            {
                return false;
            }

            if (_appliedKeysThisSession.Contains(applicationKey))
            {
                return true;
            }

            if (!IsPersistentCampaignKey(_currentCampaignKey))
            {
                return false;
            }

            _appliedState.Normalize();
            foreach (var campaignKey in new[] { _currentCampaignKey }.Concat(_recentCampaignKeys))
            {
                if (!IsPersistentCampaignKey(campaignKey))
                {
                    continue;
                }

                if (_appliedState.campaigns.TryGetValue(campaignKey, out var state) &&
                    state != null &&
                    state.appliedKeys != null &&
                    state.appliedKeys.Any(key => string.Equals(key, applicationKey, StringComparison.OrdinalIgnoreCase)))
                {
                    return true;
                }
            }

            return false;
        }

        private int CleanupLegacyUnsafeFluidDepositRows(object objectInfo, DepositRule rule, DepositSpec deposit)
        {
            if (!_config.cleanupLegacyUnsafeFluidDeposits || !IsLargeBody(objectInfo) || !IsFluidState(deposit.state))
            {
                return 0;
            }

            var rows = GetResourceRows(objectInfo).ToList();
            if (rows.Count == 0)
            {
                return 0;
            }

            var threshold = Math.Max(
                _config.cleanupLegacyFluidMinimumValue,
                Math.Max(1d, deposit.minimumAmount) * Math.Max(1d, _config.cleanupLegacyFluidMultiplierThreshold));

            var removeDeposit = objectInfo.GetType().GetMethod("RemoveDeposit", BindingFlags.Instance | BindingFlags.Public);
            if (removeDeposit == null)
            {
                WarnOnce("missing-removedeposit", "ObjectInfo.RemoveDeposit was not found; cannot clean legacy oversized fluid rows.");
                return 0;
            }

            var removedRows = 0;
            foreach (var row in rows)
            {
                var rowResourceId = GetRowResourceId(row);
                if (!string.Equals(rowResourceId, deposit.resourceId, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var rowState = GetRowResourceStateName(row);
                if (!string.Equals(Normalize(rowState), Normalize(deposit.state), StringComparison.Ordinal))
                {
                    continue;
                }

                var rowValue = GetDoubleMember(row, "Value", 0d);
                if (rowValue < threshold)
                {
                    continue;
                }

                removeDeposit.Invoke(objectInfo, new[] { row });
                removedRows++;
                _log.LogInfo($"{rule.name}: removed legacy oversized {deposit.state} row {rowValue.ToString("0", CultureInfo.InvariantCulture)} {deposit.resourceId} from {GetBestObjectName(objectInfo)}. Threshold: {threshold.ToString("0", CultureInfo.InvariantCulture)}.");
            }

            return removedRows;
        }

        private int CleanupDuplicateConfiguredDepositRows(object objectInfo, DepositRule rule, DepositSpec deposit, string effectiveStateName, double targetMinimum)
        {
            if (!_config.cleanupDuplicateConfiguredDeposits || targetMinimum <= 0d)
            {
                return 0;
            }

            var matches = GetResourceRows(objectInfo)
                .Where(row =>
                    string.Equals(GetRowResourceId(row), deposit.resourceId, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(Normalize(GetRowResourceStateName(row)), Normalize(effectiveStateName), StringComparison.Ordinal))
                .ToList();

            if (matches.Count <= 1)
            {
                return 0;
            }

            var total = matches.Sum(row => GetDoubleMember(row, "Value", 0d));
            var tolerance = Math.Max(1d, targetMinimum * Math.Max(0d, _config.duplicateCleanupTolerance));
            if (total <= targetMinimum + tolerance)
            {
                return 0;
            }

            var removeDeposit = objectInfo.GetType().GetMethod("RemoveDeposit", BindingFlags.Instance | BindingFlags.Public);
            if (removeDeposit == null)
            {
                WarnOnce("missing-removedeposit-duplicates", "ObjectInfo.RemoveDeposit was not found; cannot clean duplicate configured deposit rows.");
                return 0;
            }

            var excess = total - targetMinimum;
            var changedRows = 0;
            foreach (var row in matches.AsEnumerable().Reverse())
            {
                if (excess <= 0d)
                {
                    break;
                }

                var value = GetDoubleMember(row, "Value", 0d);
                if (value <= 0d)
                {
                    continue;
                }

                if (value <= excess + 0.000001d)
                {
                    removeDeposit.Invoke(objectInfo, new[] { row });
                    excess -= value;
                    changedRows++;
                    continue;
                }

                SetMemberValue(row, "Value", value - excess);
                excess = 0d;
                changedRows++;
            }

            if (changedRows > 0)
            {
                _log.LogInfo($"{rule.name}: trimmed duplicate {effectiveStateName} rows for {deposit.resourceId} on {GetBestObjectName(objectInfo)} from {total.ToString("0", CultureInfo.InvariantCulture)} toward {targetMinimum.ToString("0", CultureInfo.InvariantCulture)}.");
            }

            return changedRows;
        }

        private void ResolveTypes()
        {
            _objectInfoManagerType = _objectInfoManagerType ?? FindType("Manager.ObjectInfoManager");
            _rowResourcesDataType = _rowResourcesDataType ?? FindType("Game.UI.Windows.Elements.ObjectInfoElements.RowResourcesData");
            if (_rowResourcesDataType != null && _resourceStateType == null)
            {
                _resourceStateType = _rowResourcesDataType.GetNestedType("EResourceState", BindingFlags.Public);
            }
        }

        private List<object> GetObjectInfos()
        {
            var result = new List<object>();
            var manager = FindUnityObject(_objectInfoManagerType);
            if (manager == null)
            {
                return result;
            }

            var allObjectInfos = GetMemberValue(manager, "allObjectInfos") as IEnumerable;
            if (allObjectInfos == null)
            {
                return result;
            }

            foreach (var objectInfo in allObjectInfos)
            {
                if (objectInfo != null)
                {
                    result.Add(objectInfo);
                }
            }

            return result;
        }

        private object ResolveResourceDefinition(string resourceId)
        {
            if (_resourceCache.TryGetValue(resourceId, out var cached))
            {
                return cached;
            }

            var allScriptableObjectManagerType = FindType("Manager.AllScriptableObjectManager");
            var manager = GetSingletonInstance(allScriptableObjectManagerType) ?? FindUnityObject(allScriptableObjectManagerType);
            var allResourceDefinitions = GetMemberValue(manager, "AllResourceDefinitions");
            var fromRepository = InvokeGetById(allResourceDefinitions, resourceId);
            if (fromRepository != null)
            {
                _resourceCache[resourceId] = fromRepository;
                return fromRepository;
            }

            var resourceDefinitionType = FindType("ScriptableObjectScripts.ResourceDefinition");
            if (resourceDefinitionType != null)
            {
                foreach (var obj in Resources.FindObjectsOfTypeAll(resourceDefinitionType))
                {
                    if (string.Equals(GetResourceDefinitionId(obj), resourceId, StringComparison.OrdinalIgnoreCase))
                    {
                        _resourceCache[resourceId] = obj;
                        return obj;
                    }
                }
            }

            return null;
        }

        private static object InvokeGetById(object repository, string id)
        {
            if (repository == null)
            {
                return null;
            }

            var method = repository.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public)
                .FirstOrDefault(m =>
                    string.Equals(m.Name, "GetByID", StringComparison.Ordinal) &&
                    m.GetParameters().Length == 1 &&
                    m.GetParameters()[0].ParameterType == typeof(string));

            return method == null ? null : method.Invoke(repository, new object[] { id });
        }

        private object ParseResourceState(string stateName)
        {
            if (_resourceStateType == null)
            {
                return null;
            }

            var normalized = string.IsNullOrWhiteSpace(stateName) ? "Underground" : stateName.Trim();
            try
            {
                return Enum.Parse(_resourceStateType, normalized, true);
            }
            catch
            {
                WarnOnce("bad-state-" + normalized, $"Unknown resource state '{normalized}', using Underground.");
                return Enum.Parse(_resourceStateType, "Underground", true);
            }
        }

        private double GetEffectiveMinimumAmount(object objectInfo, DepositSpec deposit, string effectiveStateName)
        {
            var amount = Math.Max(0d, deposit.minimumAmount);
            if (!IsLargeBody(objectInfo) || IsLargeBodyMultiplierExcludedState(effectiveStateName))
            {
                return amount;
            }

            return amount * Math.Max(1d, _config.largeBodyReserveMultiplier);
        }

        private string GetEffectiveDepositStateName(object objectInfo, DepositSpec deposit)
        {
            if (_config.forceConfiguredDepositsUnderground)
            {
                return string.IsNullOrWhiteSpace(_config.forcedDepositState)
                    ? "Underground"
                    : _config.forcedDepositState;
            }

            if (!_config.remapLargeBodyFluidDepositsToUnderground || !IsLargeBody(objectInfo) || !IsFluidState(deposit.state))
            {
                return string.IsNullOrWhiteSpace(deposit.state) ? "Underground" : deposit.state;
            }

            return string.IsNullOrWhiteSpace(_config.largeBodyFluidDepositStateOverride)
                ? "Underground"
                : _config.largeBodyFluidDepositStateOverride;
        }

        private bool IsLargeBody(object objectInfo)
        {
            var objectType = Normalize(GetMemberValue(objectInfo, "objectTypes")?.ToString());
            var largeBodyTypes = _config.largeBodyObjectTypes ?? new List<string>();
            return largeBodyTypes.Any(t => Normalize(t) == objectType);
        }

        private bool IsLargeBodyMultiplierExcludedState(string state)
        {
            var states = _config.largeBodyReserveMultiplierExcludedStates ?? new List<string>();
            return states.Any(s => Normalize(s) == Normalize(state));
        }

        private static bool IsFluidState(string state)
        {
            var normalized = Normalize(state);
            return normalized == "GAS" || normalized == "LIQUID";
        }

        private void RefreshObjectAfterDepositChange(object objectInfo)
        {
            TryInvokeInstanceMethod(objectInfo, "UpdatePrimaryResourceCache");
            TryInvokeInstanceMethod(objectInfo, "MergeDeposit");
            TryInvokeInstanceMethod(objectInfo, "UpdateHabitabilityAndOrVisualization", true);
            TryInvokeInstanceMethod(objectInfo, "MarkIsDirty");
        }

        private void TryInvokeInstanceMethod(object target, string methodName, params object[] args)
        {
            if (target == null || string.IsNullOrWhiteSpace(methodName))
            {
                return;
            }

            try
            {
                var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                var method = target.GetType().GetMethods(flags)
                    .FirstOrDefault(m =>
                        string.Equals(m.Name, methodName, StringComparison.Ordinal) &&
                        m.GetParameters().Length == args.Length);
                method?.Invoke(target, args);
            }
            catch (Exception ex)
            {
                WarnOnce("invoke-" + methodName, $"Could not invoke {methodName} after resource deposit changes: {ex.Message}");
            }
        }

        private double GetExistingResourceTotal(object objectInfo, string resourceId, string stateName)
        {
            double total = 0;
            foreach (var row in GetResourceRows(objectInfo))
            {
                if (row == null)
                {
                    continue;
                }

                var rowResourceId = GetRowResourceId(row);
                if (!string.Equals(rowResourceId, resourceId, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!string.Equals(Normalize(GetRowResourceStateName(row)), Normalize(stateName), StringComparison.Ordinal))
                {
                    continue;
                }

                total += GetDoubleMember(row, "Value", 0d);
            }

            return total;
        }

        private IEnumerable<object> GetResourceRows(object objectInfo)
        {
            var rows = GetMemberValue(objectInfo, "ListRowResourcesData") as IEnumerable;
            if (rows == null)
            {
                yield break;
            }

            foreach (var row in rows)
            {
                if (row != null)
                {
                    yield return row;
                }
            }
        }

        private string GetRowResourceId(object row)
        {
            var saveId = GetMemberValue(row, "resourceTypeIDSave");
            var idFromSave = GetStringMember(saveId, "id");
            if (!string.IsNullOrWhiteSpace(idFromSave))
            {
                return idFromSave;
            }

            var resource = GetMemberValue(row, "ResourcesType");
            return GetResourceDefinitionId(resource);
        }

        private string GetResourceDefinitionId(object resource)
        {
            return GetStringMember(resource, "ID")
                ?? GetStringMember(resource, "id")
                ?? GetStringMember(resource, "Id");
        }

        private string GetRowResourceStateName(object row)
        {
            return GetMemberValue(row, "ResourceState")?.ToString()
                ?? GetMemberValue(row, "resourceState")?.ToString()
                ?? string.Empty;
        }

        private bool RuleMatches(object objectInfo, DepositRule rule)
        {
            var candidates = GetObjectNameCandidates(objectInfo).Select(Normalize).Where(s => s.Length > 0).ToList();
            var objectType = Normalize(GetMemberValue(objectInfo, "objectTypes")?.ToString());

            if (rule.excludeTargetContains != null)
            {
                foreach (var rawExclude in rule.excludeTargetContains)
                {
                    var exclude = Normalize(rawExclude);
                    if (exclude.Length > 0 && candidates.Any(c => c.Contains(exclude)))
                    {
                        return false;
                    }
                }
            }

            var typeMatched = rule.objectTypes == null || rule.objectTypes.Count == 0 ||
                rule.objectTypes.Any(t => Normalize(t) == objectType);
            if (!typeMatched)
            {
                return false;
            }

            var hasNameCriteria =
                HasEntries(rule.targetNames) ||
                HasEntries(rule.targetContains) ||
                HasEntries(rule.targetTranslationPrefixes);

            if (!hasNameCriteria)
            {
                return typeMatched;
            }

            if (rule.targetNames != null)
            {
                foreach (var rawName in rule.targetNames)
                {
                    var name = Normalize(rawName);
                    if (name.Length > 0 && candidates.Contains(name))
                    {
                        return true;
                    }
                }
            }

            if (rule.targetContains != null)
            {
                foreach (var rawNeedle in rule.targetContains)
                {
                    var needle = Normalize(rawNeedle);
                    if (needle.Length > 0 && candidates.Any(c => ContainsToken(c, needle)))
                    {
                        return true;
                    }
                }
            }

            if (rule.targetTranslationPrefixes != null)
            {
                var idTranslation = Normalize(GetStringMember(objectInfo, "idTranslation"));
                foreach (var rawPrefix in rule.targetTranslationPrefixes)
                {
                    var prefix = Normalize(rawPrefix);
                    if (prefix.Length > 0 && idTranslation.StartsWith(prefix, StringComparison.Ordinal))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private static bool HasEntries(List<string> values)
        {
            return values != null && values.Any(v => !string.IsNullOrWhiteSpace(v));
        }

        private IEnumerable<string> GetObjectNameCandidates(object objectInfo)
        {
            yield return GetStringMember(objectInfo, "ObjectName");
            yield return GetStringMember(objectInfo, "TrueObjectName");
            yield return GetStringMember(objectInfo, "ObjectNameForLogs");
            yield return GetStringMember(objectInfo, "idTranslation");

            if (objectInfo is Object unityObject)
            {
                yield return unityObject.name;
            }

            if (objectInfo is Component component && component.gameObject != null)
            {
                yield return component.gameObject.name;
            }
        }

        private string GetBestObjectName(object objectInfo)
        {
            return GetObjectNameCandidates(objectInfo).FirstOrDefault(s => !string.IsNullOrWhiteSpace(s)) ?? "<unknown object>";
        }

        private object FindUnityObject(Type type)
        {
            if (type == null)
            {
                return null;
            }

            try
            {
                var active = Object.FindObjectsOfType(type);
                if (active != null && active.Length > 0)
                {
                    return active[0];
                }
            }
            catch
            {
                // Fall through to Resources for inactive Unity objects.
            }

            try
            {
                var all = Resources.FindObjectsOfTypeAll(type);
                return all != null && all.Length > 0 ? all[0] : null;
            }
            catch
            {
                return null;
            }
        }

        private static object GetSingletonInstance(Type type)
        {
            if (type == null)
            {
                return null;
            }

            const BindingFlags flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.FlattenHierarchy;
            var property = type.GetProperty("Instance", flags);
            if (property != null)
            {
                try
                {
                    return property.GetValue(null, null);
                }
                catch
                {
                    return null;
                }
            }

            return null;
        }

        private static Type FindType(params string[] fullNames)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                foreach (var fullName in fullNames)
                {
                    var type = assembly.GetType(fullName, false);
                    if (type != null)
                    {
                        return type;
                    }
                }
            }

            return null;
        }

        private static object GetMemberValue(object target, string name)
        {
            if (target == null || string.IsNullOrWhiteSpace(name))
            {
                return null;
            }

            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.FlattenHierarchy;
            var type = target.GetType();
            var property = type.GetProperty(name, flags);
            if (property != null && property.GetIndexParameters().Length == 0)
            {
                try
                {
                    return property.GetValue(target, null);
                }
                catch
                {
                    return null;
                }
            }

            var field = type.GetField(name, flags);
            if (field != null)
            {
                return field.GetValue(target);
            }

            return null;
        }

        private static void SetMemberValue(object target, string name, object value)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.FlattenHierarchy;
            var type = target.GetType();
            var property = type.GetProperty(name, flags);
            if (property != null && property.CanWrite)
            {
                property.SetValue(target, ConvertForMember(value, property.PropertyType), null);
                return;
            }

            var field = type.GetField(name, flags);
            if (field != null)
            {
                field.SetValue(target, ConvertForMember(value, field.FieldType));
            }
        }

        private static object ConvertForMember(object value, Type destinationType)
        {
            if (value == null)
            {
                return null;
            }

            var nullableType = Nullable.GetUnderlyingType(destinationType);
            var effectiveType = nullableType ?? destinationType;

            if (effectiveType.IsEnum)
            {
                return value;
            }

            if (effectiveType == typeof(float))
            {
                return Convert.ToSingle(value, CultureInfo.InvariantCulture);
            }

            if (effectiveType == typeof(double))
            {
                return Convert.ToDouble(value, CultureInfo.InvariantCulture);
            }

            if (effectiveType == typeof(bool))
            {
                return Convert.ToBoolean(value, CultureInfo.InvariantCulture);
            }

            return value;
        }

        private static string GetStringMember(object target, string name)
        {
            return GetMemberValue(target, name) as string;
        }

        private static bool GetBoolMember(object target, string name, bool fallback)
        {
            var value = GetMemberValue(target, name);
            return value is bool boolValue ? boolValue : fallback;
        }

        private static double GetDoubleMember(object target, string name, double fallback)
        {
            var value = GetMemberValue(target, name);
            if (value == null)
            {
                return fallback;
            }

            try
            {
                return Convert.ToDouble(value, CultureInfo.InvariantCulture);
            }
            catch
            {
                return fallback;
            }
        }

        private static int GetIntMember(object target, string name, int fallback)
        {
            var value = GetMemberValue(target, name);
            if (value == null)
            {
                return fallback;
            }

            try
            {
                return Convert.ToInt32(value, CultureInfo.InvariantCulture);
            }
            catch
            {
                return fallback;
            }
        }

        private static string Normalize(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim().ToUpperInvariant();
        }

        private static bool ContainsToken(string candidate, string needle)
        {
            if (string.IsNullOrEmpty(candidate) || string.IsNullOrEmpty(needle))
            {
                return false;
            }

            var index = candidate.IndexOf(needle, StringComparison.Ordinal);
            while (index >= 0)
            {
                var beforeOk = index == 0 || !char.IsLetterOrDigit(candidate[index - 1]);
                var after = index + needle.Length;
                var afterOk = after >= candidate.Length || !char.IsLetterOrDigit(candidate[after]);
                if (beforeOk && afterOk)
                {
                    return true;
                }

                index = candidate.IndexOf(needle, index + 1, StringComparison.Ordinal);
            }

            return false;
        }

        private void WarnOnce(string key, string message)
        {
            if (_warningOnce.Add(key))
            {
                _log.LogWarning(message);
            }
        }
    }

    [Serializable]
    public sealed class DepositConfig
    {
        public bool enabled = true;
        public bool onlyAddToMineableObjects = true;
        public bool applyOncePerCampaign = true;
        public bool continuousRescan = false;
        public float scanIntervalSeconds = 8f;
        public double minimumAddAmount = 1d;
        public double largeBodyReserveMultiplier = 1000000d;
        public List<string> largeBodyObjectTypes = new List<string> { "Planet", "Moons", "DwarfPlanet", "Protoplanet" };
        public bool forceConfiguredDepositsUnderground = true;
        public string forcedDepositState = "Underground";
        public List<string> largeBodyReserveMultiplierExcludedStates = new List<string> { "Gas", "Liquid" };
        public bool remapLargeBodyFluidDepositsToUnderground = true;
        public string largeBodyFluidDepositStateOverride = "Underground";
        public bool cleanupLegacyHabitabilityStateDeposits = true;
        public double cleanupLegacyHabitabilityMinimumFraction = 0.25d;
        public double cleanupLegacyHabitabilityMinimumValue = 1000000d;
        public List<string> cleanupLegacyHabitabilityFallbackStates = new List<string> { "Solid" };
        public bool cleanupLegacyUnsafeFluidDeposits = true;
        public bool cleanupDuplicateConfiguredDeposits = true;
        public double duplicateCleanupTolerance = 0.001d;
        public double cleanupLegacyFluidMultiplierThreshold = 100000d;
        public double cleanupLegacyFluidMinimumValue = 1000000000d;
        public List<DepositRule> rules = new List<DepositRule>();

        public void Normalize()
        {
            rules = rules ?? new List<DepositRule>();
            largeBodyObjectTypes = largeBodyObjectTypes ?? new List<string> { "Planet", "Moons", "DwarfPlanet", "Protoplanet" };
            largeBodyReserveMultiplierExcludedStates = largeBodyReserveMultiplierExcludedStates ?? new List<string> { "Gas", "Liquid" };
            cleanupLegacyHabitabilityFallbackStates = cleanupLegacyHabitabilityFallbackStates ?? new List<string> { "Solid" };
            if (scanIntervalSeconds < 2f)
            {
                scanIntervalSeconds = 8f;
            }
            if (largeBodyReserveMultiplier < 1d)
            {
                largeBodyReserveMultiplier = 1000000d;
            }
            if (minimumAddAmount < 0d)
            {
                minimumAddAmount = 1d;
            }
            if (duplicateCleanupTolerance < 0d)
            {
                duplicateCleanupTolerance = 0.001d;
            }
            if (cleanupLegacyFluidMultiplierThreshold < 1d)
            {
                cleanupLegacyFluidMultiplierThreshold = 100000d;
            }
            if (cleanupLegacyFluidMinimumValue < 0d)
            {
                cleanupLegacyFluidMinimumValue = 1000000000d;
            }
            if (string.IsNullOrWhiteSpace(forcedDepositState))
            {
                forcedDepositState = "Underground";
            }
            if (string.IsNullOrWhiteSpace(largeBodyFluidDepositStateOverride))
            {
                largeBodyFluidDepositStateOverride = "Underground";
            }
            if (cleanupLegacyHabitabilityMinimumFraction < 0d)
            {
                cleanupLegacyHabitabilityMinimumFraction = 0.25d;
            }
            if (cleanupLegacyHabitabilityMinimumValue < 0d)
            {
                cleanupLegacyHabitabilityMinimumValue = 1000000d;
            }
        }
    }

    [Serializable]
    public sealed class DepositRule
    {
        public string name;
        public string notes;
        public List<string> objectTypes = new List<string>();
        public List<string> targetNames = new List<string>();
        public List<string> targetContains = new List<string>();
        public List<string> targetTranslationPrefixes = new List<string>();
        public List<string> excludeTargetContains = new List<string>();
        public List<DepositSpec> deposits = new List<DepositSpec>();
    }

    [Serializable]
    public sealed class DepositSpec
    {
        public string resourceId;
        public double minimumAmount;
        public float miningFactor;
        public string state = "Underground";
        public bool fullyExplored = true;
        public bool forcePrimary;
        public string reason;
    }

    [Serializable]
    public sealed class AppliedState
    {
        public int version = 1;
        public Dictionary<string, CampaignAppliedState> campaigns = new Dictionary<string, CampaignAppliedState>(StringComparer.OrdinalIgnoreCase);

        public void Normalize()
        {
            campaigns = campaigns ?? new Dictionary<string, CampaignAppliedState>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in campaigns.ToList())
            {
                if (pair.Value == null)
                {
                    campaigns.Remove(pair.Key);
                    continue;
                }

                pair.Value.campaignKey = string.IsNullOrWhiteSpace(pair.Value.campaignKey) ? pair.Key : pair.Value.campaignKey;
                pair.Value.appliedKeys = pair.Value.appliedKeys ?? new List<string>();
            }
        }
    }

    [Serializable]
    public sealed class CampaignAppliedState
    {
        public string campaignKey;
        public string lastAppliedUtc;
        public List<string> appliedKeys = new List<string>();
    }

    internal static class DefaultDepositConfig
    {
        public static DepositConfig Create()
        {
            return new DepositConfig
            {
                enabled = true,
                onlyAddToMineableObjects = true,
                applyOncePerCampaign = true,
                continuousRescan = false,
                scanIntervalSeconds = 8f,
                minimumAddAmount = 1d,
                largeBodyReserveMultiplier = 1000000d,
                largeBodyObjectTypes = new List<string> { "Planet", "Moons", "DwarfPlanet", "Protoplanet" },
                forceConfiguredDepositsUnderground = true,
                forcedDepositState = "Underground",
                largeBodyReserveMultiplierExcludedStates = new List<string> { "Gas", "Liquid" },
                remapLargeBodyFluidDepositsToUnderground = true,
                largeBodyFluidDepositStateOverride = "Underground",
                cleanupLegacyHabitabilityStateDeposits = true,
                cleanupLegacyHabitabilityMinimumFraction = 0.25d,
                cleanupLegacyHabitabilityMinimumValue = 1000000d,
                cleanupLegacyHabitabilityFallbackStates = new List<string> { "Solid" },
                cleanupLegacyUnsafeFluidDeposits = true,
                cleanupDuplicateConfiguredDeposits = true,
                duplicateCleanupTolerance = 0.001d,
                cleanupLegacyFluidMultiplierThreshold = 100000d,
                cleanupLegacyFluidMinimumValue = 1000000000d,
                rules = new List<DepositRule>
                {
                    Rule("Luna baseline", new[] { "LUNA", "MOON", "CELESTIALBODIESNAMES.MOON" }, null,
                        "Lunar regolith contains oxygen, silicon, metals, trace volatiles, polar ice, solar-wind hydrogen, noble gases, and trace helium-3.",
                        D("id_resource_oxygen", 1400000, 0.12f, "Underground", "oxygen bound in silicates and oxides"),
                        D("id_resource_silicon", 1200000, 0.14f, "Underground", "silicate-rich crust and regolith"),
                        D("id_resource_metal", 850000, 0.10f, "Underground", "iron-bearing basalt, oxides, and core-derived metal"),
                        D("id_resource_raremetal", 90000, 0.035f, "Underground", "minor titanium, thorium, rare metals"),
                        D("id_resource_volatile", 90000, 0.025f, "Underground", "carbon-rich impactor contribution and trace lunar volatiles"),
                        D("id_resource_water", 80000, 0.025f, "Underground", "polar shadow ice and hydrated regolith"),
                        D("id_resource_hydrogen", 60000, 0.02f, "Underground", "solar-wind implanted hydrogen"),
                        D("id_resource_noblegas", 7000, 0.004f, "Underground", "helium, neon, argon in the lunar exosphere/regolith"),
                        D("id_resource_nitrogen", 3000, 0.002f, "Underground", "trace nitrogen-bearing volatiles"),
                        D("id_resource_hel3", 12000, 0.004f, "Underground", "solar-wind implanted helium-3"),
                        D("id_resource_uran", 12000, 0.004f, "Underground", "trace uranium and thorium")),

                    Rule("Mercury baseline", new[] { "MERCURY", "CELESTIALBODIESNAMES.MERCURY" }, null,
                        "Mercury is metal-rich, sulfur/volatile-bearing, has oxygen/sodium/hydrogen/helium/potassium exosphere species, and possible polar water ice.",
                        D("id_resource_metal", 1000000, 0.12f, "Underground", "large metallic core and metal-bearing crust"),
                        D("id_resource_silicon", 900000, 0.11f, "Underground", "silicate crust"),
                        D("id_resource_oxygen", 800000, 0.10f, "Underground", "oxygen bound in surface minerals"),
                        D("id_resource_raremetal", 120000, 0.035f, "Underground", "minor refractory and metal-rich material"),
                        D("id_resource_water", 70000, 0.018f, "Underground", "permanently shadowed polar ice"),
                        D("id_resource_hydrogen", 30000, 0.012f, "Underground", "exosphere and polar volatile association"),
                        D("id_resource_volatile", 20000, 0.008f, "Underground", "low but non-zero carbon-bearing impactor volatiles"),
                        D("id_resource_uran", 20000, 0.006f, "Underground", "trace fissile elements")),

                    Rule("Venus baseline", new[] { "VENUS", "CELESTIALBODIESNAMES.VENUS" }, null,
                        "Venus has a carbon-dioxide dominated atmosphere, nitrogen, sulfuric-acid clouds, volcanic basaltic surface, and an iron core.",
                        D("id_resource_volatile", 5000000, 0.30f, "Underground", "carbon dioxide atmosphere mapped to Carbon"),
                        D("id_resource_nitrogen", 600000, 0.08f, "Underground", "nitrogen atmosphere"),
                        D("id_resource_noblegas", 60000, 0.015f, "Underground", "argon and other trace gases"),
                        D("id_resource_oxygen", 1000000, 0.10f, "Underground", "oxygen bound in basaltic crust and CO2"),
                        D("id_resource_silicon", 1000000, 0.12f, "Underground", "basaltic silicates"),
                        D("id_resource_metal", 900000, 0.10f, "Underground", "iron-bearing basalt and core material"),
                        D("id_resource_raremetal", 80000, 0.025f, "Underground", "minor crustal rare metals"),
                        D("id_resource_water", 10000, 0.003f, "Underground", "trace atmospheric water vapor"),
                        D("id_resource_uran", 25000, 0.006f, "Underground", "trace fissile elements")),

                    Rule("Earth baseline", new[] { "EARTH", "CELESTIALBODIESNAMES.EARTH" }, null,
                        "Earth is resource-rich; this rule only tops up missing deposits if the base game omitted them.",
                        D("id_resource_water", 5000000, 0.30f, "Underground", "surface oceans and groundwater"),
                        D("id_resource_oxygen", 4000000, 0.22f, "Underground", "oxygen atmosphere and crustal oxides"),
                        D("id_resource_nitrogen", 3500000, 0.22f, "Underground", "nitrogen atmosphere"),
                        D("id_resource_volatile", 3500000, 0.18f, "Underground", "biosphere, carbonates, hydrocarbons, and organics"),
                        D("id_resource_metal", 3000000, 0.18f, "Underground", "metal ores and core-derived metals"),
                        D("id_resource_silicon", 2200000, 0.18f, "Underground", "silicate crust"),
                        D("id_resource_raremetal", 400000, 0.06f, "Underground", "rare metal deposits"),
                        D("id_resource_noblegas", 100000, 0.02f, "Underground", "argon and trace noble gases"),
                        D("id_resource_uran", 100000, 0.025f, "Underground", "uranium and thorium ores")),

                    Rule("Mars baseline", new[] { "MARS", "CELESTIALBODIESNAMES.MARS" }, null,
                        "Mars has a CO2 atmosphere, nitrogen and argon traces, water ice, hydrated minerals, basalt, iron oxides, sulfates, and perchlorates.",
                        D("id_resource_volatile", 1500000, 0.12f, "Underground", "carbon dioxide atmosphere mapped to Carbon"),
                        D("id_resource_water", 600000, 0.08f, "Underground", "polar and subsurface ice"),
                        D("id_resource_nitrogen", 150000, 0.03f, "Underground", "thin nitrogen atmosphere"),
                        D("id_resource_noblegas", 90000, 0.025f, "Underground", "argon-rich trace atmosphere"),
                        D("id_resource_oxygen", 900000, 0.10f, "Underground", "iron oxides and silicate minerals"),
                        D("id_resource_metal", 1000000, 0.11f, "Underground", "iron-rich basalt and oxides"),
                        D("id_resource_silicon", 900000, 0.11f, "Underground", "basaltic silicates"),
                        D("id_resource_raremetal", 90000, 0.025f, "Underground", "minor rare metals"),
                        D("id_resource_hydrogen", 70000, 0.02f, "Underground", "water and hydrated minerals"),
                        D("id_resource_uran", 25000, 0.006f, "Underground", "trace fissile elements")),

                    Rule("Phobos and Deimos baseline", new[] { "PHOBOS", "DEIMOS", "CELESTIALBODIESNAMES.PHOBOS", "CELESTIALBODIESNAMES.DEIMOS" }, null,
                        "Mars' small moons are treated as primitive rocky/carbonaceous bodies with low volatiles.",
                        D("id_resource_metal", 300000, 0.06f, "Underground", "rocky material"),
                        D("id_resource_silicon", 250000, 0.06f, "Underground", "silicates"),
                        D("id_resource_volatile", 90000, 0.025f, "Underground", "carbonaceous material"),
                        D("id_resource_water", 50000, 0.015f, "Underground", "hydrated minerals and impactor volatiles"),
                        D("id_resource_raremetal", 40000, 0.012f, "Underground", "minor metals"),
                        D("id_resource_oxygen", 120000, 0.04f, "Underground", "oxygen bound in minerals")),

                    Rule("Jupiter baseline", new[] { "JUPITER", "CELESTIALBODIESNAMES.JUPITER" }, null,
                        "Jupiter is hydrogen and helium dominated with water/ammonia clouds, methane traces, and noble gases.",
                        D("id_resource_hydrogen", 12000000, 0.40f, "Underground", "hydrogen-rich atmosphere"),
                        D("id_resource_hel3", 750000, 0.05f, "Underground", "helium isotope in giant-planet atmosphere"),
                        D("id_resource_noblegas", 500000, 0.05f, "Underground", "helium and noble gases"),
                        D("id_resource_water", 400000, 0.03f, "Underground", "water clouds and deep atmospheric water"),
                        D("id_resource_volatile", 250000, 0.02f, "Underground", "methane and carbon-bearing trace species"),
                        D("id_resource_nitrogen", 200000, 0.02f, "Underground", "ammonia and nitrogen-bearing species")),

                    Rule("Io baseline", new[] { "IO", "CELESTIALBODIESNAMES.IO" }, null,
                        "Io is rocky, volcanic, sulfur-rich, and depleted in water compared with the icy Galilean moons.",
                        D("id_resource_oxygen", 650000, 0.08f, "Underground", "silicate and sulfur-oxide chemistry"),
                        D("id_resource_silicon", 650000, 0.09f, "Underground", "silicate volcanics"),
                        D("id_resource_metal", 550000, 0.08f, "Underground", "rocky metal-bearing interior"),
                        D("id_resource_raremetal", 55000, 0.018f, "Underground", "minor refractory metals"),
                        D("id_resource_volatile", 40000, 0.01f, "Underground", "sulfur dioxide and trace carbon-bearing volatiles"),
                        D("id_resource_uran", 10000, 0.003f, "Underground", "trace fissile elements")),

                    Rule("Icy Galilean moons baseline", new[] { "EUROPA", "GANYMEDE", "CALLISTO", "CELESTIALBODIESNAMES.EUROPA", "CELESTIALBODIESNAMES.GANYMEDE", "CELESTIALBODIESNAMES.CALLISTO" }, null,
                        "Europa, Ganymede, and Callisto are ice-rich worlds with water ice/oceans above rocky and metallic interiors.",
                        D("id_resource_water", 1600000, 0.18f, "Underground", "ice shells and subsurface water"),
                        D("id_resource_oxygen", 400000, 0.08f, "Underground", "water ice and oxidized surface material"),
                        D("id_resource_hydrogen", 300000, 0.07f, "Underground", "water ice"),
                        D("id_resource_volatile", 120000, 0.025f, "Underground", "carbonaceous and radiolytic chemistry"),
                        D("id_resource_metal", 250000, 0.05f, "Underground", "rocky and metallic interiors"),
                        D("id_resource_silicon", 200000, 0.045f, "Underground", "silicate interiors"),
                        D("id_resource_raremetal", 30000, 0.008f, "Underground", "minor metals")),

                    Rule("Small Jovian moon baseline", new[] { "AMALTHEA", "CELESTIALBODIESNAMES.AMALTHEA" }, null,
                        "Amalthea is treated as a small, low-density, mixed rocky/icy moon.",
                        D("id_resource_water", 250000, 0.05f, "Underground", "icy component"),
                        D("id_resource_volatile", 90000, 0.02f, "Underground", "dark carbonaceous surface material"),
                        D("id_resource_metal", 120000, 0.03f, "Underground", "rocky material"),
                        D("id_resource_silicon", 110000, 0.03f, "Underground", "silicates"),
                        D("id_resource_oxygen", 100000, 0.03f, "Underground", "ice and silicates")),

                    Rule("Saturn baseline", new[] { "SATURN", "CELESTIALBODIESNAMES.SATURN" }, null,
                        "Saturn is mostly hydrogen and helium, with water, ammonia, methane, and trace gases.",
                        D("id_resource_hydrogen", 11000000, 0.38f, "Underground", "hydrogen-rich atmosphere"),
                        D("id_resource_hel3", 700000, 0.045f, "Underground", "helium isotope in giant-planet atmosphere"),
                        D("id_resource_noblegas", 450000, 0.045f, "Underground", "helium and noble gases"),
                        D("id_resource_water", 350000, 0.025f, "Underground", "water clouds and ring/moon ice source"),
                        D("id_resource_volatile", 250000, 0.02f, "Underground", "methane and carbon-bearing species"),
                        D("id_resource_nitrogen", 180000, 0.018f, "Underground", "ammonia and nitrogen-bearing species")),

                    Rule("Titan baseline", new[] { "TITAN", "CELESTIALBODIESNAMES.TITAN" }, null,
                        "Titan has a dense nitrogen/methane atmosphere, hydrocarbon lakes, organic-rich surface materials, water ice, and likely subsurface ocean.",
                        D("id_resource_nitrogen", 4000000, 0.25f, "Underground", "nitrogen-rich atmosphere"),
                        D("id_resource_volatile", 2500000, 0.22f, "Underground", "methane, ethane, and organic hydrocarbons mapped to Carbon"),
                        D("id_resource_hydrogen", 700000, 0.08f, "Underground", "hydrocarbons and water ice"),
                        D("id_resource_water", 1100000, 0.12f, "Underground", "water ice and subsurface ocean"),
                        D("id_resource_oxygen", 300000, 0.06f, "Underground", "water ice and oxygen-bearing organics"),
                        D("id_resource_noblegas", 100000, 0.02f, "Underground", "trace atmospheric gases"),
                        D("id_resource_metal", 150000, 0.03f, "Underground", "rocky interior"),
                        D("id_resource_silicon", 150000, 0.03f, "Underground", "silicate interior"),
                        D("id_resource_raremetal", 15000, 0.004f, "Underground", "minor metals")),

                    Rule("Enceladus baseline", new[] { "ENCELADUS", "CELESTIALBODIESNAMES.ENCELADUS" }, null,
                        "Enceladus has water ice, a salty global ocean, organic compounds, CO2/CO, hydrogen, nitrogen/oxygen-bearing organics, silica, and rocky hydrothermal interaction.",
                        D("id_resource_water", 1500000, 0.18f, "Underground", "ice shell and global ocean"),
                        D("id_resource_volatile", 350000, 0.06f, "Underground", "organic compounds and carbon oxides"),
                        D("id_resource_hydrogen", 220000, 0.05f, "Underground", "hydrogen in plume chemistry"),
                        D("id_resource_nitrogen", 120000, 0.03f, "Underground", "nitrogen-bearing organics"),
                        D("id_resource_oxygen", 250000, 0.05f, "Underground", "water ice and oxygen-bearing compounds"),
                        D("id_resource_silicon", 100000, 0.025f, "Underground", "silica nanograins from water-rock interaction"),
                        D("id_resource_metal", 100000, 0.025f, "Underground", "rocky core"),
                        D("id_resource_raremetal", 10000, 0.003f, "Underground", "minor metals")),

                    Rule("Other icy Saturn moons baseline", new[] { "RHEA", "IAPETUS", "TETHYS", "MIMAS", "HYPERION", "DIONE", "CELESTIALBODIESNAMES.RHEA", "CELESTIALBODIESNAMES.IAPETUS", "CELESTIALBODIESNAMES.TETHYS", "CELESTIALBODIESNAMES.MIMAS", "CELESTIALBODIESNAMES.HYPERION", "CELESTIALBODIESNAMES.DIONE" }, null,
                        "Saturn's mid-sized icy moons are water-ice rich with dark carbonaceous contaminants and rocky fractions.",
                        D("id_resource_water", 900000, 0.12f, "Underground", "water ice"),
                        D("id_resource_oxygen", 180000, 0.04f, "Underground", "water ice and oxidized surface material"),
                        D("id_resource_hydrogen", 150000, 0.035f, "Underground", "water ice"),
                        D("id_resource_volatile", 100000, 0.025f, "Underground", "dark carbonaceous material"),
                        D("id_resource_metal", 120000, 0.03f, "Underground", "rocky fraction"),
                        D("id_resource_silicon", 100000, 0.025f, "Underground", "silicate fraction"),
                        D("id_resource_raremetal", 10000, 0.003f, "Underground", "minor metals")),

                    Rule("Uranus and Neptune baseline", new[] { "URANUS", "NEPTUNE", "CELESTIALBODIESNAMES.URANUS", "CELESTIALBODIESNAMES.NEPTUNE" }, null,
                        "Uranus and Neptune are ice giants with hydrogen/helium/methane atmospheres and water/ammonia/methane-rich interiors.",
                        D("id_resource_hydrogen", 8000000, 0.28f, "Underground", "hydrogen-rich atmosphere"),
                        D("id_resource_hel3", 450000, 0.035f, "Underground", "helium isotope in atmosphere"),
                        D("id_resource_noblegas", 350000, 0.035f, "Underground", "helium and noble gases"),
                        D("id_resource_volatile", 1200000, 0.12f, "Underground", "methane and carbon-bearing ices"),
                        D("id_resource_water", 2500000, 0.16f, "Underground", "water-rich ice-giant interior"),
                        D("id_resource_nitrogen", 300000, 0.025f, "Underground", "ammonia/nitrogen-bearing species"),
                        D("id_resource_oxygen", 600000, 0.05f, "Underground", "water and ice-giant oxygen inventory")),

                    Rule("Uranian icy moons baseline", new[] { "UMBRIEL", "PUCK", "TITANIA", "OBERON", "ARIEL", "MIRANDA", "CELESTIALBODIESNAMES.UMBRIEL", "CELESTIALBODIESNAMES.PUCK", "CELESTIALBODIESNAMES.TITANIA", "CELESTIALBODIESNAMES.OBERON", "CELESTIALBODIESNAMES.ARIEL", "CELESTIALBODIESNAMES.MIRANDA" }, null,
                        "Uranian moons are treated as icy-rocky bodies with carbon-darkened surfaces and rocky interiors.",
                        D("id_resource_water", 700000, 0.10f, "Underground", "water ice"),
                        D("id_resource_oxygen", 160000, 0.04f, "Underground", "water ice and oxidized material"),
                        D("id_resource_hydrogen", 120000, 0.03f, "Underground", "water ice"),
                        D("id_resource_volatile", 100000, 0.025f, "Underground", "carbonaceous dark material"),
                        D("id_resource_nitrogen", 40000, 0.01f, "Underground", "minor nitrogen-bearing volatiles"),
                        D("id_resource_metal", 100000, 0.025f, "Underground", "rocky interior"),
                        D("id_resource_silicon", 90000, 0.025f, "Underground", "silicates")),

                    Rule("Triton baseline", new[] { "TRITON", "CELESTIALBODIESNAMES.TRITON" }, null,
                        "Triton has nitrogen, methane, carbon monoxide, and water ice over a rocky interior.",
                        D("id_resource_nitrogen", 1000000, 0.12f, "Underground", "nitrogen ice and thin atmosphere"),
                        D("id_resource_volatile", 600000, 0.08f, "Underground", "methane and carbon monoxide ices"),
                        D("id_resource_water", 900000, 0.12f, "Underground", "water ice crust"),
                        D("id_resource_hydrogen", 180000, 0.04f, "Underground", "water and methane ice"),
                        D("id_resource_oxygen", 180000, 0.04f, "Underground", "water ice"),
                        D("id_resource_metal", 100000, 0.025f, "Underground", "rocky interior"),
                        D("id_resource_silicon", 90000, 0.025f, "Underground", "silicate interior")),

                    Rule("Other Neptune moons baseline", new[] { "PROTEUS", "NEREID", "CELESTIALBODIESNAMES.PROTEUS", "CELESTIALBODIESNAMES.NEREID" }, null,
                        "Neptune's smaller moons are treated as icy-rocky irregular bodies.",
                        D("id_resource_water", 500000, 0.08f, "Underground", "water ice"),
                        D("id_resource_volatile", 150000, 0.035f, "Underground", "carbon/nitrogen volatile inventory"),
                        D("id_resource_nitrogen", 80000, 0.02f, "Underground", "nitrogen-bearing volatiles"),
                        D("id_resource_oxygen", 120000, 0.03f, "Underground", "water ice"),
                        D("id_resource_hydrogen", 100000, 0.025f, "Underground", "water ice"),
                        D("id_resource_metal", 80000, 0.02f, "Underground", "rocky fraction"),
                        D("id_resource_silicon", 70000, 0.02f, "Underground", "silicates")),

                    Rule("Pluto and Charon baseline", new[] { "PLUTO", "CHARON", "CELESTIALBODIESNAMES.PLUTO", "CELESTIALBODIESNAMES.CHARON" }, null,
                        "Pluto and Charon are icy-rocky Kuiper Belt bodies with water ice, nitrogen, methane, and carbon monoxide, with Charon more water-ice dominated.",
                        D("id_resource_nitrogen", 750000, 0.09f, "Underground", "nitrogen ice and atmosphere on Pluto"),
                        D("id_resource_volatile", 500000, 0.08f, "Underground", "methane and carbon monoxide ices"),
                        D("id_resource_water", 750000, 0.10f, "Underground", "water ice crust and mantle"),
                        D("id_resource_hydrogen", 160000, 0.04f, "Underground", "methane and water ice"),
                        D("id_resource_oxygen", 160000, 0.04f, "Underground", "water ice"),
                        D("id_resource_metal", 90000, 0.02f, "Underground", "rocky core"),
                        D("id_resource_silicon", 80000, 0.02f, "Underground", "silicate core material")),

                    Rule("Ceres baseline", new[] { "CERES", "CELESTIALBODIESNAMES.CERES" }, null,
                        "Ceres is water-rich and has hydrated minerals, carbonates, ammoniated clays, organics, and rocky material.",
                        D("id_resource_water", 900000, 0.12f, "Underground", "subsurface ice and hydrated minerals"),
                        D("id_resource_volatile", 300000, 0.06f, "Underground", "organics and carbonates"),
                        D("id_resource_nitrogen", 80000, 0.02f, "Underground", "ammoniated clays"),
                        D("id_resource_metal", 250000, 0.055f, "Underground", "rocky material"),
                        D("id_resource_silicon", 220000, 0.05f, "Underground", "silicates"),
                        D("id_resource_raremetal", 50000, 0.014f, "Underground", "minor metals"),
                        D("id_resource_uran", 15000, 0.004f, "Underground", "trace fissile elements"),
                        D("id_resource_oxygen", 180000, 0.04f, "Underground", "ice, carbonates, and silicates")),

                    Rule("Psyche metal-rich baseline", new[] { "PSYCHE", "CELESTIALBODIESNAMES.PSYCHE" }, null,
                        "Psyche is treated as a metal-rich asteroid with low volatiles.",
                        D("id_resource_metal", 2000000, 0.18f, "Underground", "metal-rich asteroid composition"),
                        D("id_resource_raremetal", 300000, 0.08f, "Underground", "nickel-iron and siderophile metals"),
                        D("id_resource_silicon", 200000, 0.035f, "Underground", "silicate fraction"),
                        D("id_resource_volatile", 20000, 0.006f, "Underground", "minor carbonaceous/impact volatiles"),
                        D("id_resource_water", 10000, 0.004f, "Underground", "trace hydrated material")),

                    RuleForTypes("Generic asteroid and protoplanet baseline", new[] { "Asteroid", "Protoplanet" },
                        "Applies to named belt bodies not given a specific rule. Asteroids get low metal/silicate baselines plus conservative water/carbon traces.",
                        D("id_resource_metal", 300000, 0.06f, "Underground", "rocky and metallic asteroid material"),
                        D("id_resource_silicon", 250000, 0.05f, "Underground", "silicates"),
                        D("id_resource_raremetal", 60000, 0.018f, "Underground", "minor metal-rich phases"),
                        D("id_resource_volatile", 40000, 0.012f, "Underground", "carbonaceous material on many primitive asteroids"),
                        D("id_resource_water", 30000, 0.01f, "Underground", "hydrated minerals and ice in primitive asteroids"),
                        D("id_resource_oxygen", 100000, 0.03f, "Underground", "oxygen bound in silicates"),
                        D("id_resource_uran", 8000, 0.002f, "Underground", "trace fissile elements")),

                    RuleForTypes("Generic comet baseline", new[] { "Comet" },
                        "Comets are volatile-rich mixtures of water ice, carbon-bearing compounds, dust, and trapped gases.",
                        D("id_resource_water", 500000, 0.12f, "Underground", "water ice"),
                        D("id_resource_volatile", 250000, 0.08f, "Underground", "carbon-bearing ices and organics"),
                        D("id_resource_hydrogen", 150000, 0.05f, "Underground", "water and hydrocarbon ice"),
                        D("id_resource_nitrogen", 80000, 0.025f, "Underground", "nitrogen-bearing volatiles"),
                        D("id_resource_noblegas", 20000, 0.006f, "Underground", "trapped noble gases"),
                        D("id_resource_oxygen", 120000, 0.035f, "Underground", "water ice"),
                        D("id_resource_silicon", 60000, 0.015f, "Underground", "dust and silicates")),

                    RuleForTypes("Generic dwarf planet baseline", new[] { "DwarfPlanet" },
                        "Fallback for dwarf planets beyond the named Pluto/Ceres cases: icy-rocky bodies with nitrogen/carbon volatiles.",
                        D("id_resource_water", 650000, 0.09f, "Underground", "water ice"),
                        D("id_resource_volatile", 300000, 0.06f, "Underground", "methane and carbon-bearing ices"),
                        D("id_resource_nitrogen", 200000, 0.04f, "Underground", "nitrogen-bearing ice"),
                        D("id_resource_hydrogen", 120000, 0.03f, "Underground", "water and methane ice"),
                        D("id_resource_oxygen", 180000, 0.035f, "Underground", "water ice"),
                        D("id_resource_metal", 120000, 0.025f, "Underground", "rocky core"),
                        D("id_resource_silicon", 100000, 0.025f, "Underground", "silicates"),
                        D("id_resource_raremetal", 20000, 0.005f, "Underground", "minor metals"))
                }
            };
        }

        private static DepositRule Rule(string name, string[] contains, string[] objectTypes, string notes, params DepositSpec[] deposits)
        {
            return new DepositRule
            {
                name = name,
                notes = notes,
                targetContains = contains.ToList(),
                objectTypes = objectTypes == null ? new List<string>() : objectTypes.ToList(),
                deposits = deposits.ToList()
            };
        }

        private static DepositRule RuleForTypes(string name, string[] objectTypes, string notes, params DepositSpec[] deposits)
        {
            return new DepositRule
            {
                name = name,
                notes = notes,
                objectTypes = objectTypes.ToList(),
                deposits = deposits.ToList()
            };
        }

        private static DepositSpec D(string resourceId, double minimumAmount, float miningFactor, string state, string reason)
        {
            return new DepositSpec
            {
                resourceId = resourceId,
                minimumAmount = minimumAmount,
                miningFactor = miningFactor,
                state = state,
                fullyExplored = true,
                forcePrimary = false,
                reason = reason
            };
        }
    }
}
