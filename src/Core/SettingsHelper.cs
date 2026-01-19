using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text.Json;
using LiteMonitor.src.Core;

namespace LiteMonitor
{
    public static class SettingsHelper
    {
        // Cache path
        private static readonly string _cachedPath = Path.Combine(AppContext.BaseDirectory, "settings.json");
        public static string FilePath => _cachedPath;

        // Global block save lock
        public static bool GlobalBlockSave { get; set; } = false;

        public static Settings Load(bool forceReload = false)
        {
            // Note: The singleton instance management is kept in Settings.Load() facade
            // or handled by the caller. This method strictly loads from disk/creates default.
            
            Settings s = new Settings();
            try
            {
                if (File.Exists(FilePath))
                {
                    var json = File.ReadAllText(FilePath);
                    s = JsonSerializer.Deserialize<Settings>(json, new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    }) ?? new Settings();
                }
            }
            catch { }

            if (s.GroupAliases == null) s.GroupAliases = new Dictionary<string, string>();

            // 1. Check if new install
            if (s.MonitorItems == null || s.MonitorItems.Count == 0)
            {
                s.InitDefaultItems();
                // Ensure TaskbarSortIndex has initial value
                foreach (var item in s.MonitorItems)
                {
                    if (item.TaskbarSortIndex == 0)
                        item.TaskbarSortIndex = item.SortIndex;
                }
            }
            else
            {
                // 2. Version check
                bool isLegacyConfig = s.MonitorItems.All(x => x.TaskbarSortIndex == 0);

                if (isLegacyConfig)
                {
                    s.RebuildAndMigrateSettings();
                }
                else
                {
                    s.CheckAndAppendMissingItems();
                }
            }

            s.SyncToLanguage();
            s.InternAllStrings();
            
            return s;
        }

        public static void Save(this Settings settings)
        {
            if (GlobalBlockSave) return;

            try
            {
                var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(FilePath, json);
            }
            catch { }
        }

        public static void InitDefaultItems(this Settings settings)
        {
            settings.MonitorItems = new List<MonitorItemConfig>
            {
                // Dashboard Items
                new MonitorItemConfig { Key = "DASH.HOST", SortIndex = -100, TaskbarSortIndex = 100, VisibleInPanel = false, TaskbarLabel = " " },
                new MonitorItemConfig { Key = "DASH.Time", SortIndex = -90, TaskbarSortIndex = 200, VisibleInPanel = false, TaskbarLabel = " " },
                new MonitorItemConfig { Key = "DASH.Uptime", SortIndex = -80, TaskbarSortIndex = 300, VisibleInPanel = true, TaskbarLabel = " " },
                new MonitorItemConfig { Key = "DASH.IP",   SortIndex = -70, TaskbarSortIndex = 400, VisibleInPanel = true, TaskbarLabel = " " },
               
                new MonitorItemConfig { Key = "CPU.Load",  SortIndex = 0, VisibleInPanel = true, VisibleInTaskbar = true },
                new MonitorItemConfig { Key = "CPU.Temp",  SortIndex = 1, VisibleInPanel = true, VisibleInTaskbar = true },
                new MonitorItemConfig { Key = "CPU.Clock", SortIndex = 2, VisibleInPanel = false },
                new MonitorItemConfig { Key = "CPU.Power", SortIndex = 3, VisibleInPanel = false },
                new MonitorItemConfig { Key = "CPU.Fan",   SortIndex = 4, VisibleInPanel = false },
                new MonitorItemConfig { Key = "CPU.Pump",  SortIndex = 5, VisibleInPanel = false },

                new MonitorItemConfig { Key = "GPU.Load",  SortIndex = 10, VisibleInPanel = true, VisibleInTaskbar = true },
                new MonitorItemConfig { Key = "GPU.Temp",  SortIndex = 11, VisibleInPanel = true },
                new MonitorItemConfig { Key = "GPU.Clock", SortIndex = 12, VisibleInPanel = false },
                new MonitorItemConfig { Key = "GPU.Power", SortIndex = 13, VisibleInPanel = false },
                new MonitorItemConfig { Key = "GPU.Fan",   SortIndex = 14, VisibleInPanel = false },
                new MonitorItemConfig { Key = "GPU.VRAM",  SortIndex = 15, VisibleInPanel = true },

                new MonitorItemConfig { Key = "MEM.Load",  SortIndex = 20, VisibleInPanel = true, VisibleInTaskbar = true },
                new MonitorItemConfig { Key = "FPS",       SortIndex = 21, VisibleInPanel = false },
                new MonitorItemConfig { Key = "MOBO.Temp", SortIndex = 22, VisibleInPanel = false },
                new MonitorItemConfig { Key = "DISK.Temp", SortIndex = 23, VisibleInPanel = false },
                new MonitorItemConfig { Key = "CASE.Fan",  SortIndex = 24, VisibleInPanel = false },

                new MonitorItemConfig { Key = "DISK.Read", SortIndex = 30, VisibleInPanel = true },
                new MonitorItemConfig { Key = "DISK.Write",SortIndex = 31, VisibleInPanel = true },

                new MonitorItemConfig { Key = "NET.Up",    SortIndex = 40, VisibleInPanel = true, VisibleInTaskbar = true },
                new MonitorItemConfig { Key = "NET.Down",  SortIndex = 41, VisibleInPanel = true, VisibleInTaskbar = true },

                new MonitorItemConfig { Key = "DATA.DayUp",  SortIndex = 50, VisibleInPanel = true },
                new MonitorItemConfig { Key = "DATA.DayDown",SortIndex = 51, VisibleInPanel = true },
            };
        }

        public static void SyncToLanguage(this Settings settings)
        {
            LanguageManager.ClearOverrides();
            if (settings.GroupAliases != null)
            {
                foreach (var kv in settings.GroupAliases)
                    LanguageManager.SetOverride(UIUtils.Intern("Groups." + kv.Key), kv.Value);
            }
            if (settings.MonitorItems != null)
            {
                foreach (var item in settings.MonitorItems)
                {
                    if (!string.IsNullOrEmpty(item.UserLabel))
                        LanguageManager.SetOverride(UIUtils.Intern("Items." + item.Key), item.UserLabel);
                    if (!string.IsNullOrEmpty(item.TaskbarLabel))
                        LanguageManager.SetOverride(UIUtils.Intern("Short." + item.Key), item.TaskbarLabel);
                }
            }
        }

        public static void InternAllStrings(this Settings settings)
        {
            if (settings.MonitorItems != null)
            {
                // [Cleanup] Remove Orphaned Plugin Items
                var keysToRemove = new List<MonitorItemConfig>();
                var activeInstanceIds = settings.PluginInstances.Where(p => p.Enabled).Select(p => p.Id).ToHashSet();
                
                foreach (var item in settings.MonitorItems)
                {
                    if (item != null)
                    {
                        item.Key = UIUtils.Intern(item.Key);
                        
                        // Check Orphans
                        if (item.Key.StartsWith("DASH.") && !item.Key.StartsWith("DASH.HOST") && 
                            !item.Key.StartsWith("DASH.Time") && !item.Key.StartsWith("DASH.IP") && 
                            !item.Key.StartsWith("DASH.Uptime")) 
                        {
                            var parts = item.Key.Split('.');
                            if (parts.Length >= 2)
                            {
                                string instId = parts[1];
                                if (!activeInstanceIds.Contains(instId))
                                {
                                    keysToRemove.Add(item);
                                }
                            }
                        }
                    }
                }
                
                foreach (var orphan in keysToRemove)
                {
                    settings.MonitorItems.Remove(orphan);
                }
            }

            settings.PreferredDisk = UIUtils.Intern(settings.PreferredDisk);
            settings.LastAutoDisk = UIUtils.Intern(settings.LastAutoDisk);
            settings.PreferredNetwork = UIUtils.Intern(settings.PreferredNetwork);
            settings.LastAutoNetwork = UIUtils.Intern(settings.LastAutoNetwork);
            
            settings.PreferredCpuFan = UIUtils.Intern(settings.PreferredCpuFan);
            settings.PreferredCpuPump = UIUtils.Intern(settings.PreferredCpuPump);
            settings.PreferredCaseFan = UIUtils.Intern(settings.PreferredCaseFan);
            settings.PreferredMoboTemp = UIUtils.Intern(settings.PreferredMoboTemp);
            
            settings.TaskbarFontFamily = UIUtils.Intern(settings.TaskbarFontFamily);
        }

        public static void RebuildAndMigrateSettings(this Settings settings)
        {
            var temp = new Settings();
            temp.InitDefaultItems();
            var standardItems = temp.MonitorItems;

            var migratedList = new List<MonitorItemConfig>();

            foreach (var stdItem in standardItems)
            {
                var userOldItem = settings.MonitorItems.FirstOrDefault(x => x.Key.Equals(stdItem.Key, StringComparison.OrdinalIgnoreCase));

                if (userOldItem != null)
                {
                    stdItem.VisibleInPanel = userOldItem.VisibleInPanel;
                    stdItem.VisibleInTaskbar = userOldItem.VisibleInTaskbar;
                    stdItem.UserLabel = userOldItem.UserLabel;
                    stdItem.TaskbarLabel = userOldItem.TaskbarLabel;
                    stdItem.UnitPanel = userOldItem.UnitPanel;
                    stdItem.UnitTaskbar = userOldItem.UnitTaskbar;
                }

                if (stdItem.TaskbarSortIndex == 0) 
                {
                    stdItem.TaskbarSortIndex = stdItem.SortIndex;
                }

                migratedList.Add(stdItem);
            }

            settings.MonitorItems = migratedList;
        }

        public static void CheckAndAppendMissingItems(this Settings settings)
        {
            var temp = new Settings();
            temp.InitDefaultItems();
            
            var newItems = temp.MonitorItems
                .Where(std => !settings.MonitorItems.Any(usr => usr.Key.Equals(std.Key, StringComparison.OrdinalIgnoreCase)))
                .OrderBy(std => std.SortIndex)
                .ToList();

            if (newItems.Count == 0) return;

            bool listChanged = false;

            foreach (var newItem in newItems)
            {
                // Step A: Handle Panel (SortIndex)
                var conflictingPanelItems = settings.MonitorItems.Where(x => x.SortIndex >= newItem.SortIndex).ToList();
                foreach (var item in conflictingPanelItems) item.SortIndex++;

                // Step B: Handle Taskbar (TaskbarSortIndex)
                int targetTaskbarIndex = 0;

                if (newItem.TaskbarSortIndex != 0)
                {
                    targetTaskbarIndex = newItem.TaskbarSortIndex;
                }
                else
                {
                    var predecessor = settings.MonitorItems
                        .Where(x => x.SortIndex < newItem.SortIndex)
                        .OrderByDescending(x => x.SortIndex)
                        .FirstOrDefault();

                    if (predecessor != null)
                    {
                        targetTaskbarIndex = predecessor.TaskbarSortIndex + 1;
                    }
                    else
                    {
                        targetTaskbarIndex = 0;
                    }
                }

                var conflictingTaskbarItems = settings.MonitorItems.Where(x => x.TaskbarSortIndex >= targetTaskbarIndex).ToList();
                foreach (var item in conflictingTaskbarItems) item.TaskbarSortIndex++;

                newItem.TaskbarSortIndex = targetTaskbarIndex;

                settings.MonitorItems.Add(newItem);
                listChanged = true;
            }

            if (listChanged)
            {
                settings.MonitorItems = settings.MonitorItems.OrderBy(x => x.SortIndex).ToList();
            }
        }

        public static Settings.TBStyle GetStyle(this Settings settings)
        {
            if (settings.TaskbarCustomLayout) return new Settings.TBStyle {
                Font = settings.TaskbarFontFamily, Size = settings.TaskbarFontSize, Bold = settings.TaskbarFontBold,
                Gap = settings.TaskbarItemSpacing, Inner = settings.TaskbarInnerSpacing, VOff = settings.TaskbarVerticalPadding
            };
            return new Settings.TBStyle {
                Font = "Microsoft YaHei UI", Size = settings.TaskbarFontBold ? 10f : 9f, Bold = settings.TaskbarFontBold,
                Gap = 6, Inner = settings.TaskbarFontBold ? 10 : 8, VOff = 2 
            };
        }

        public static bool IsAnyEnabled(this Settings settings, string keyPrefix)
        {
            return settings.MonitorItems.Any(x => x.Key.StartsWith(keyPrefix, StringComparison.OrdinalIgnoreCase) && (x.VisibleInPanel || x.VisibleInTaskbar));
        }

        public static void UpdateMaxRecord(this Settings settings, string key, float val)
        {
            bool changed = false;
            if (val <= 0 || float.IsNaN(val) || float.IsInfinity(val)) return;
            
            if (key.Contains("Clock") && val > 10000) return; 
            if (key.Contains("Power") && val > 1000) return;
            if ((key.Contains("Fan") || key.Contains("Pump")) && val > 10000) return;

            if (key == "CPU.Power" && val > settings.RecordedMaxCpuPower) { settings.RecordedMaxCpuPower = val; changed = true; }
            else if (key == "CPU.Clock" && val > settings.RecordedMaxCpuClock) { settings.RecordedMaxCpuClock = val; changed = true; }
            else if (key == "GPU.Power" && val > settings.RecordedMaxGpuPower) { settings.RecordedMaxGpuPower = val; changed = true; }
            else if (key == "GPU.Clock" && val > settings.RecordedMaxGpuClock) { settings.RecordedMaxGpuClock = val; changed = true; }
            
            else if (key == "CPU.Fan" && val > settings.RecordedMaxCpuFan) { settings.RecordedMaxCpuFan = val; changed = true; }
            else if (key == "CPU.Pump" && val > settings.RecordedMaxCpuPump) { settings.RecordedMaxCpuPump = val; changed = true; }
            else if (key == "GPU.Fan" && val > settings.RecordedMaxGpuFan) { settings.RecordedMaxGpuFan = val; changed = true; }
            else if (key == "CASE.Fan" && val > settings.RecordedMaxChassisFan) { settings.RecordedMaxChassisFan = val; changed = true; }
            
            if (changed && (DateTime.Now - settings.LastAutoSaveTime).TotalSeconds > 30)
            {
                settings.Save();
                settings.LastAutoSaveTime = DateTime.Now;
            }
        }
    }
}
