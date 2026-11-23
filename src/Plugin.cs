using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;

namespace ZedZone.BetterSpecialWeapons
{
    /// <summary>
    /// 更好的特种武器（Better Special Weapons）插件入口：
    /// 修复并重做特斯拉枪（467）、采矿激光器（466）、M2 火焰喷射器（468）与高压喷水枪（469），
    /// 提供通用“单次伤害 + 高频攻速”同步面板、特斯拉微幅电弧磁吸、喷射武器沿途穿透判定与真实时间耗电/耗液修复。
    /// </summary>
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class Plugin : BasePlugin
    {
        public const string PluginGuid = "zedzone.betterspecialweapons";
        public const string PluginName = "Better Special Weapons";
        public const string PluginVersion = "1.3.0";

        internal static ManualLogSource Logs;

        // 通用设置
        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<bool> SyncWeaponPanelStats;
        internal static ConfigEntry<bool> VerboseLog;

        // 特斯拉枪 (467)
        internal static ConfigEntry<bool> EnableTeslaRework;
        internal static ConfigEntry<float> TeslaDamagePerTick;
        internal static ConfigEntry<float> TeslaFireRateHz;
        internal static ConfigEntry<float> TeslaChainFalloff;
        internal static ConfigEntry<float> TeslaArcSnapRadius;
        internal static ConfigEntry<float> TeslaStunChancePerSec;
        internal static ConfigEntry<float> TeslaTerrainDamageMultiplier;

        // 采矿激光器 (466)
        internal static ConfigEntry<bool> EnableLaserRework;
        internal static ConfigEntry<float> LaserDamagePerTick;
        internal static ConfigEntry<float> LaserFireRateHz;
        internal static ConfigEntry<float> LaserDeathMultiplier;
        internal static ConfigEntry<float> LaserMiningMultiplier;

        // M2 火焰喷射器 (468)
        internal static ConfigEntry<bool> EnableFlamethrowerRework;
        internal static ConfigEntry<float> FlamethrowerTargetDps;
        internal static ConfigEntry<bool> FlamethrowerPathPenetration;

        // 高压喷水枪 (469)
        internal static ConfigEntry<bool> EnableWaterGunRework;
        internal static ConfigEntry<float> WaterGunTargetDps;
        internal static ConfigEntry<bool> WaterGunPathPenetration;
        internal static ConfigEntry<float> WaterGunKnockbackStrength;
        internal static ConfigEntry<bool> WaterGunExtinguishFire;
        internal static ConfigEntry<float> WaterGunExtinguishRadius;

        // 喷射类路径穿透半径
        internal static ConfigEntry<float> LiquidPathHitRadius;

        // 能源与燃料消耗修复
        internal static ConfigEntry<bool> FixEnergyDrainBug;
        internal static ConfigEntry<float> EnergyCostMultiplier;
        internal static ConfigEntry<bool> FixLiquidDrainBug;
        internal static ConfigEntry<float> LiquidCostMultiplier;

        private static readonly HashSet<string> LoggedErrorTags = new HashSet<string>(StringComparer.Ordinal);

        public override void Load()
        {
            Logs = Log;

            Enabled = Config.Bind("General", "Enabled", true,
                "启用更好的特种武器模组（多人联机需房主与客户端一致安装）。");
            SyncWeaponPanelStats = Config.Bind("General", "SyncWeaponPanelStats", true,
                "将武器详情面板上的单次伤害与攻速（fireInterval）同步为重做后的真实数值，实现所见即所得。");
            VerboseLog = Config.Bind("General", "VerboseLog", false,
                "输出详细调试日志到 BepInEx\\LogOutput.log。");

            EnableTeslaRework = Config.Bind("TeslaGun_467", "Enabled", true,
                "启用特斯拉枪（467）帧率无关高频脉冲、链式同步与电弧自动索敌。");
            TeslaDamagePerTick = Config.Bind("TeslaGun_467", "DamagePerTick", 36f,
                new ConfigDescription("特斯拉枪每次电击的基础伤害（配合默认 50 次/秒 -> 1,800 基础单体 DPS，3 链导电最高约 4,220 DPS）。",
                    new AcceptableValueRange<float>(5f, 2000f)));
            TeslaFireRateHz = Config.Bind("TeslaGun_467", "FireRateHz", 50f,
                new ConfigDescription("特斯拉枪每秒电击次数（Hz，范围 10 ~ 100，默认 50 即面板 3,000 RPM；主光束与连锁电弧同频触发并同步至面板射速）。",
                    new AcceptableValueRange<float>(10f, 100f)));
            TeslaChainFalloff = Config.Bind("TeslaGun_467", "ChainFalloff", 0.65f,
                new ConfigDescription("特斯拉枪链式传导每一跳的伤害衰减系数（默认 0.65，与原版一致）。",
                    new AcceptableValueRange<float>(0.1f, 1.0f)));
            TeslaArcSnapRadius = Config.Bind("TeslaGun_467", "ArcMagneticSnapRadius", 1.8f,
                new ConfigDescription("高压电弧自动索敌半径（米，范围 0 ~ 4.5，默认 1.8；设 0 关闭。优先沿枪口到鼠标的射线走廊自动锁定前方敌人，其次锁定准星附近目标，视觉电弧直接锁在目标躯干上）。",
                    new AcceptableValueRange<float>(0f, 4.5f)));
            TeslaStunChancePerSec = Config.Bind("TeslaGun_467", "StunChancePerSecond", 0.22f,
                new ConfigDescription("特斯拉枪每秒触发电击硬直麻痹的等效概率（范围 0 ~ 1.0，默认 0.22，保持合理控场节奏）。",
                    new AcceptableValueRange<float>(0f, 1.0f)));
            TeslaTerrainDamageMultiplier = Config.Bind("TeslaGun_467", "TerrainDamageMultiplier", 1.0f,
                new ConfigDescription("特斯拉枪对树木、岩石等环境物体的破坏倍率（默认 1.0 与原版一致，如需减弱可自行调低如 0.25）。",
                    new AcceptableValueRange<float>(0.05f, 5.0f)));

            EnableLaserRework = Config.Bind("MiningLaser_466", "Enabled", true,
                "启用采矿激光器（466）帧率无关高频脉冲重做并保护原版采矿/拆墙效率。");
            LaserDamagePerTick = Config.Bind("MiningLaser_466", "DamagePerTick", 48f,
                new ConfigDescription("采矿激光器每次激光脉冲的基础伤害（配合默认 50 次/秒 -> 2,400 基础单体 DPS，纯单体高伤定位，对单超越特斯拉枪）。",
                    new AcceptableValueRange<float>(5f, 2000f)));
            LaserFireRateHz = Config.Bind("MiningLaser_466", "FireRateHz", 50f,
                new ConfigDescription("采矿激光器每秒伤害脉冲次数（Hz，范围 10 ~ 100，默认 50 即面板 3,000 RPM，并同步至武器面板射速）。",
                    new AcceptableValueRange<float>(10f, 100f)));
            LaserDeathMultiplier = Config.Bind("MiningLaser_466", "DeathMultiplier", 25f,
                new ConfigDescription("采矿激光器命中目标的击杀/穿甲系数（原版误传为 bulletNumber=1，特斯拉为 50）。",
                    new AcceptableValueRange<float>(1f, 100f)));
            LaserMiningMultiplier = Config.Bind("MiningLaser_466", "MiningDamageMultiplier", 1.0f,
                new ConfigDescription("采矿激光器对地形物体与地下矿石的开采速度倍率（1.0 等同原版 600 基准效率）。",
                    new AcceptableValueRange<float>(0.25f, 5.0f)));

            EnableFlamethrowerRework = Config.Bind("Flamethrower_468", "Enabled", true,
                "启用 M2 火焰喷射器（468）帧率无关精准直伤重做（保留原版燃烧 DoT 与火焰视觉）。");
            FlamethrowerTargetDps = Config.Bind("Flamethrower_468", "TargetDirectDps", 1400f,
                new ConfigDescription("M2 火焰喷射器目标直伤 DPS（不含燃烧 DoT；对应 50/s 面板单团伤害 28）。",
                    new AcceptableValueRange<float>(50f, 10000f)));
            FlamethrowerPathPenetration = Config.Bind("Flamethrower_468", "EnablePathPenetration", true,
                "让火焰喷射器沿“枪口 → 落点”的火舌路径也能穿透灼烧并点燃沿途敌人（自动排除落点圈内目标，避免双倍叠伤）。");

            EnableWaterGunRework = Config.Bind("WaterGun_469", "Enabled", true,
                "启用高压喷水枪（469）帧率无关伤害修正与诚实面板（保留击退与灭火功能）。");
            WaterGunTargetDps = Config.Bind("WaterGun_469", "TargetDps", 120f,
                new ConfigDescription("高压喷水枪目标 DPS（默认 120，定位控制/击退/灭火功能枪；对应 50/s 面板单团伤害 2.4）。",
                    new AcceptableValueRange<float>(0f, 5000f)));
            WaterGunPathPenetration = Config.Bind("WaterGun_469", "EnablePathPenetration", false,
                "是否让高压喷水枪穿透首目标影响沿途敌人（默认关闭，保留原版水柱撞击首目标落地的手感）。");
            WaterGunKnockbackStrength = Config.Bind("WaterGun_469", "KnockbackStrength", 1.0f,
                new ConfigDescription("高压喷水枪击退力度倍率（0 关闭，默认 1.0；按目标质量自适应击退普通丧尸，重型 Boss 抗击退更强）。",
                    new AcceptableValueRange<float>(0f, 3.0f)));
            WaterGunExtinguishFire = Config.Bind("WaterGun_469", "ExtinguishFire", true,
                "高压喷水枪自动扑灭水柱沿途与落点区域的地面火焰、燃烧物体、燃烧尸体、体素火以及角色燃烧状态。");
            WaterGunExtinguishRadius = Config.Bind("WaterGun_469", "ExtinguishRadius", 2.2f,
                new ConfigDescription("高压喷水枪落点灭火半径（米，范围 0.8 ~ 5.0，默认 2.2）。",
                    new AcceptableValueRange<float>(0.8f, 5.0f)));

            LiquidPathHitRadius = Config.Bind("LiquidPath", "PathHitRadius", 0.85f,
                new ConfigDescription("火焰喷射器与高压喷水枪沿途穿透判定的走廊半径（米，范围 0.3 ~ 2.0）。",
                    new AcceptableValueRange<float>(0.3f, 2.0f)));

            FixEnergyDrainBug = Config.Bind("Consumption", "FixEnergyDrainBug", true,
                "修复特斯拉枪与采矿激光器原版耗电公式 Bug，严格按真实流逝时间与标称 batteryEnergyPerSec（50/s）消耗电量。");
            EnergyCostMultiplier = Config.Bind("Consumption", "EnergyCostMultiplier", 1.0f,
                new ConfigDescription("能量武器电量消耗倍率（1.0 为修复后的标称 50/s，满电高功率供电模块可持续 100 秒）。",
                    new AcceptableValueRange<float>(0.1f, 5.0f)));
            FixLiquidDrainBug = Config.Bind("Consumption", "FixLiquidDrainBug", true,
                "修复火焰喷射器与高压喷水枪按固定 60 帧乱扣燃料/水量的 Bug，改为严格按真实流逝时间消耗。");
            LiquidCostMultiplier = Config.Bind("Consumption", "LiquidCostMultiplier", 1.0f,
                new ConfigDescription("喷射武器燃料/水量消耗倍率（1.0 为修复后的标称每秒消耗）。",
                    new AcceptableValueRange<float>(0.1f, 5.0f)));

            if (!Enabled.Value)
            {
                Log.LogInfo($"{PluginName} is disabled in config.");
                return;
            }

            var harmony = new Harmony(PluginGuid);
            int applied = 0;
            int skipped = 0;

            Type[] patchTypes =
            {
                // 武器面板属性对齐
                typeof(Patch_ItemManager_Init_SyncWeaponAttrs),
                typeof(Patch_BasicRangedWeapon_Init_SyncWeaponAttrs),

                // 特斯拉枪与采矿激光高频脉冲、电弧自动索敌与视觉锁躯、链式同频、采矿保护与真实时间耗电修复
                typeof(Patch_BasicTeslaWeapon_Human_GetAimingPoint),
                typeof(Patch_BasicTeslaWeapon_UpdateLaserPoint),
                typeof(Patch_BasicTeslaWeapon_HitCharacter),
                typeof(Patch_BasicTeslaWeapon_UpdateChain_SyncRate),
                typeof(Patch_BasicTeslaWeapon_DealChainDamage),
                typeof(Patch_BasicLaserWeapon_HitCharacter),
                typeof(Patch_BasicTeslaWeapon_BeamOff_Reset),
                typeof(Patch_BasicLaserWeapon_BeamOff_Reset),
                typeof(Patch_BasicLaserWeapon_HitTerrainObject_Scope),
                typeof(Patch_BasicLaserWeapon_HitUnderground_Scope),
                typeof(Patch_BasicTeslaWeapon_HitTerrainObject_Scope),
                typeof(Patch_BasicRangedWeapon_GetDamage_TerrainScope),
                typeof(Patch_BasicTeslaWeapon_Human_FireEnergy),
                typeof(Patch_BasicLaserWeapon_Human_FireEnergy),

                // 火焰喷射器与高压喷水枪真实时间耗液、精准落点伤害与沿途穿透判定
                typeof(Patch_FlameThrower_Human_FireLiquid),
                typeof(Patch_FlameThrower_JetOff_Reset),
                typeof(Patch_FlameThrower_CreateFlame_Damage),
            };

            foreach (var patchType in patchTypes)
            {
                if (PatchOne(harmony, patchType))
                    applied++;
                else
                    skipped++;
            }

            int hooked = harmony.GetPatchedMethods().Count();
            Log.LogInfo($"{PluginName} {PluginVersion}: {applied} patch group(s) applied, {skipped} skipped, {hooked} method(s) hooked.");
        }

        private static bool PatchOne(Harmony harmony, Type patchType)
        {
            try
            {
                harmony.PatchAll(patchType);
                return true;
            }
            catch (Exception ex)
            {
                Logs?.LogWarning($"Patch {patchType.Name} skipped: {ex.Message}");
                return false;
            }
        }

        internal static void LogVerbose(string message)
        {
            if (VerboseLog != null && VerboseLog.Value)
                Logs?.LogInfo("[Verbose] " + message);
        }

        internal static void LogOnce(string tag, Exception ex)
        {
            if (!LoggedErrorTags.Add(tag)) return;
            Logs?.LogWarning($"[{tag}] {ex.GetType().Name}: {ex.Message}");
        }
    }
}
