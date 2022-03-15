﻿using NHM.Common;
using NHM.DeviceMonitoring.AMD;
using NHM.DeviceMonitoring.TDP;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace NHM.DeviceMonitoring
{
    internal class DeviceMonitorAMD : DeviceMonitor, IFanSpeedRPM, IGetFanSpeedPercentage, ILoad, IPowerUsage, ITemp, ITDP, IMemoryTimings, IMiningProfile
    {
        public int BusID { get; private set; }

        private static readonly TimeSpan _delayedLogging = TimeSpan.FromMinutes(0.5);

        private string LogTag => $"DeviceMonitorAMD-uuid({UUID})-busid({BusID})";
        private static readonly int NDEF = Int32.MinValue;

        internal DeviceMonitorAMD(string uuid, int busID)
        {
            UUID = uuid;
            BusID = busID;

            try
            {
                // set to high by default
                var defaultLevel = TDPSimpleType.HIGH;
                var success = SetTDPSimple(defaultLevel);
                if (!success)
                {
                    Logger.Info(LogTag, $"Cannot set power target ({defaultLevel}) for device with BusID={BusID}");
                }
            }
            catch (Exception e)
            {
                Logger.Error(LogTag, $"Getting power info failed with message \"{e.Message}\", disabling power setting");
            }
        }

        (bool ok, string driverVer, string catalystVersion, string crimsonVersion, string catalystWebLink) GetAMDVersions()
        {
            AMD_ODN.ADLVersionsInfoX2 versions = new AMD_ODN.ADLVersionsInfoX2(new char[256], new char[256], new char[256], new char[256]);
            int ok = AMD_ODN.nhm_amd_device_get_driver_version(BusID,ref versions);
            if(ok != 0)
            {
                return (false, "", "", "", "");
            }
            return (true, new string(versions.StrDriverVer).Trim('\0'), new string(versions.StrCatalystVersion).Trim('\0'), 
                new string(versions.StrCrimsonVersion).Trim('\0'), new string(versions.StrCatalystWebLink).Trim('\0'));
        }

        (int status, int percentage) IGetFanSpeedPercentage.GetFanSpeedPercentage()
        {
            int percentage = 0;
            int ok = AMD_ODN.nhm_amd_device_get_fan_speed_percentage(BusID, ref percentage);
            if (ok != 0) Logger.InfoDelayed(LogTag, $"nhm_amd_device_get_fan_speed_rpm failed with error code {ok}", _delayedLogging);
            return (ok, percentage);
        }

        public int FanSpeedRPM
        {
            get
            {
                int rpm = 0;
                int ok = AMD_ODN.nhm_amd_device_get_fan_speed_rpm(BusID, ref rpm);
                if (ok == 0) return rpm;
                Logger.InfoDelayed(LogTag, $"nhm_amd_device_get_fan_speed_rpm failed with error code {ok}", _delayedLogging);
                return -1;
            }
        }

        public float Temp
        {
            get
            {
                int temperature = 0;
                int ok = AMD_ODN.nhm_amd_device_get_temperature(BusID, ref temperature);
                if (ok == 0) return temperature;
                Logger.InfoDelayed(LogTag, $"nhm_amd_device_get_temperature failed with error code {ok}", _delayedLogging);
                return -1;
            }
        }

        public float Load
        {
            get
            {
                int load_perc = 0;
                int ok = AMD_ODN.nhm_amd_device_get_load_percentage(BusID, ref load_perc);
                if (ok == 0) return load_perc;
                Logger.InfoDelayed(LogTag, $"nhm_amd_device_get_load_percentage failed with error code {ok}", _delayedLogging);
                return -1;
            }
        }

        public double PowerUsage
        {
            get
            {
                int power_usage = 0;
                int ok = AMD_ODN.nhm_amd_device_get_power_usage(BusID, ref power_usage);
                if (ok == 0) return power_usage;
                Logger.InfoDelayed(LogTag, $"nhm_amd_device_get_power_usage failed with error code {ok}", _delayedLogging);
                return -1;
            }
        }

        // AMD tdpLimit
        private bool SetTdpADL(double percValue)
        {
            int min = 0, max = 0, defaultValue = 0;
            int ok = AMD_ODN.nhm_amd_device_get_tdp_min_max_default(BusID, ref min, ref max, ref defaultValue);
            if (ok != 0)
            {
                Logger.InfoDelayed(LogTag, $"nhm_amd_device_get_tdp_ranges failed with error code {ok}", _delayedLogging);
                return false;
            }

            // We limit 100% to the default as max
            var limit = 0.0d;
            if (percValue > 1)
            {
                limit = RangeCalculator.CalculateValueAMD(percValue - 1, defaultValue, max);
            }
            else
            {
                limit = RangeCalculator.CalculateValueAMD(percValue, min, defaultValue);
            }

            int ok2 = AMD_ODN.nhm_amd_device_set_tdp(BusID, (int)limit);
            if (ok2 != 0)
            {
                Logger.InfoDelayed(LogTag, $"nhm_amd_device_set_tdp failed with error code {ok}", _delayedLogging);
                return false;
            }
            return true;
        }

        #region ITDP
        public TDPSettingType SettingType { get; set; } = TDPSettingType.SIMPLE;

        public double TDPPercentage
        {
            get
            {
                int tdpRaw = 0;
                int ok = AMD_ODN.nhm_amd_device_get_tdp(BusID, ref tdpRaw);
                if (ok != 0)
                {
                    Logger.InfoDelayed(LogTag, $"nhm_amd_device_get_tdp failed with error code {ok}", _delayedLogging);
                    return -1;
                }
                int min = 0, max = 0, defaultValue = 0;
                int ok2 = AMD_ODN.nhm_amd_device_get_tdp_min_max_default(BusID, ref min, ref max, ref defaultValue);
                if (ok2 != 0)
                {
                    Logger.InfoDelayed(LogTag, $"nhm_amd_device_get_tdp_ranges failed with error code {ok}", _delayedLogging);
                    return -1;
                }
                // We limit 100% to the default as max
                var tdpPerc = RangeCalculator.CalculatePercentage(tdpRaw, min, defaultValue);
                return tdpPerc; // 0.0d - 1.0d
            }
        }

        public TDPSimpleType TDPSimple { get; private set; } = TDPSimpleType.HIGH;

        public bool SetTDPPercentage(double percentage)
        {
            if (DeviceMonitorManager.DisableDevicePowerModeSettings)
            {
                Logger.InfoDelayed(LogTag, $"SetTDPPercentage Disabled DeviceMonitorManager.DisableDevicePowerModeSettings==true", TimeSpan.FromSeconds(30));
                return false;
            }
            if (percentage < 0.0d)
            {
                Logger.Error(LogTag, $"SetTDPPercentage {percentage} out of bounds. Setting to 0.0d");
                percentage = 0.0d;
            }
            Logger.Info(LogTag, $"SetTDPPercentage setting to {percentage}.");
            return SetTdpADL(percentage);
        }
        private static double? PowerLevelToTDPPercentage(TDPSimpleType level)
        {
            switch (level)
            {
                case TDPSimpleType.LOW: return 0.6d; // 60%
                case TDPSimpleType.MEDIUM: return 0.8d; // 80%
                case TDPSimpleType.HIGH: return 1.0d; // 100%
                default: return null;
            }
        }
        public bool SetTDPSimple(TDPSimpleType level)
        {
            if (DeviceMonitorManager.DisableDevicePowerModeSettings)
            {
                Logger.InfoDelayed(LogTag, $"SetTDPSimple Disabled DeviceMonitorManager.DisableDevicePowerModeSettings==true", TimeSpan.FromSeconds(30));
                return false;
            }
            var percentage = PowerLevelToTDPPercentage(level);
            if (!percentage.HasValue)
            {
                Logger.Error(LogTag, $"SetTDPSimple unkown PowerLevel {level}. Defaulting to {TDPSimpleType.HIGH}");
                level = TDPSimpleType.HIGH;
                percentage = PowerLevelToTDPPercentage(level);
            }
            Logger.Info(LogTag, $"SetTDPSimple setting PowerLevel to {level}.");
            var execRet = SetTdpADL(percentage.Value);
            if (execRet) TDPSimple = level;
            Logger.Info(LogTag, $"SetTDPSimple {execRet}.");
            return execRet;
        }
        #endregion ITDP

        public bool SetMiningProfile(int dmc, int dcc, int mmc, int mcc, string mt)//todo unused args in amd...
        {
            int currentMC = -1;
            int currentCC = -1;
            List<Action> toRevert = new List<Action>();
            bool failed = false;
            if (mmc != NDEF)
            {
                var okGetMC = AMD_ODN.nhm_amd_device_get_memory_clocks(BusID, ref currentMC);
                var okSetMC = AMD_ODN.nhm_amd_device_set_memory_clocks(BusID, mmc);
                if (okSetMC == 0 && okGetMC == 0) toRevert.Add(() => AMD_ODN.nhm_amd_device_set_memory_clocks(BusID, currentMC));
                else failed = true;
            }
            if (mcc != NDEF && !failed)
            {
                var okGetCC = AMD_ODN.nhm_amd_device_get_core_clocks(BusID, ref currentCC);
                var okSetCC = AMD_ODN.nhm_amd_device_set_core_clocks(BusID, mmc);
                if (okSetCC == 0 && okSetCC == 0) toRevert.Add(() => AMD_ODN.nhm_amd_device_set_core_clocks(BusID, currentCC));
                else failed = true;
            }
            if (mt.Any() && !failed)
            {
                var okSetMT = AMD_ODN.nhm_nvidia_device_set_memory_timings(BusID, mt);
                if (okSetMT >= 0) toRevert.Add(() => AMD_ODN.nhm_nvidia_device_reset_memory_timings(BusID));
                else failed = true;
            }
            if (failed)
            {
                Parallel.Invoke(toRevert.ToArray());
                return false;
            }
            return true;
        }

        public int SetMemoryTimings(string mt)
        {
            return AMD_ODN.nhm_nvidia_device_set_memory_timings(BusID, mt);
        }

        public int ResetMemoryTimings()
        {
            return AMD_ODN.nhm_nvidia_device_reset_memory_timings(BusID);
        }
    }
}
