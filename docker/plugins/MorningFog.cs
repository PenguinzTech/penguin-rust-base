// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 PenguinzTech <https://penguintech.io>
using System;
using Oxide.Core;

namespace Oxide.Plugins
{
    [Info("MorningFog", "PenguinzPlays", "1.0.0")]
    [Description("Applies a Gaussian bell-curve fog from 05:00–10:00 server time, peaking at 08:00")]
    class MorningFog : RustPlugin
    {
        private PluginConfig _config;
        private Timer _fogTimer;

        #region Configuration

        private class PluginConfig
        {
            public float WindowStart        = 5.0f;
            public float WindowEnd          = 10.0f;
            public float PeakHour           = 8.0f;
            public float MaxDensity         = 1.0f;
            public float Sigma              = 1.0f;
            public int   CheckInterval      = 60;
            public float RandomFogHour      = 11.0f;
            public float RandomFogMaxDensity = 0.13f;
        }

        protected override void LoadDefaultConfig()
        {
            _config = new PluginConfig();
            SaveConfig();
        }

        protected override void LoadConfig()
        {
            base.LoadConfig();
            _config = Config.ReadObject<PluginConfig>();
        }

        protected override void SaveConfig() => Config.WriteObject(_config);

        #endregion

        void OnServerInitialized()
        {
            Puts("MorningFog active — fog window 05:00–10:00, peak 08:00");
            CheckAndApplyFog();
        }

        void Unload()
        {
            timer.Destroy(ref _fogTimer);
            SetFogDensity(0f);
        }

        private static readonly System.Random _rng = new System.Random();

        private void CheckAndApplyFog()
        {
            float hour = GetCurrentHour();
            float intensity;
            if (hour >= _config.WindowStart && hour <= _config.WindowEnd)
                intensity = ComputeIntensity(hour);
            else if (hour >= _config.RandomFogHour && hour < _config.RandomFogHour + 1f)
                intensity = GetRandomDensity(_config.RandomFogMaxDensity);
            else
                intensity = 0f;
            SetFogDensity(intensity);
            _fogTimer = timer.Once(_config.CheckInterval, CheckAndApplyFog);
        }

        private float ComputeIntensity(float hour)
        {
            float diff = hour - _config.PeakHour;
            return _config.MaxDensity * (float)Math.Exp(-(diff * diff) / (2f * _config.Sigma * _config.Sigma));
        }

        protected virtual float GetRandomDensity(float maxDensity)
            => (float)(_rng.NextDouble() * maxDensity);

        protected virtual float GetCurrentHour()
            => TOD_Sky.Instance.Cycle.Hour;

        protected virtual void SetFogDensity(float density)
            => ConsoleSystem.Run(ConsoleSystem.Option.Server.Quiet(), "weather.fog", density.ToString("F3"));
    }
}
