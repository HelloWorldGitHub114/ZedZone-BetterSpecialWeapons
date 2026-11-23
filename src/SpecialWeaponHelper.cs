using System;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using UnityEngine;

namespace ZedZone.BetterSpecialWeapons
{
    /// <summary>
    /// 特种武器常量、玩家侧安全判定、原版基准值与通用面板（单次伤害 + 攻速）同步工具。
    /// 仅对玩家侧手持特种武器生效，敌方僵尸与机甲武器保持原版行为。
    /// </summary>
    internal static class SpecialWeaponHelper
    {
        internal const int IdMiningLaser = 466;
        internal const int IdTeslaGun = 467;
        internal const int IdFlamethrower = 468;
        internal const int IdWaterGun = 469;

        // 原版地形拆墙/采矿基准伤害（用于保持采矿激光与特斯拉对地形物体的原版破坏速率）
        internal const float VanillaLaserTerrainDps = 600f;
        internal const float VanillaTeslaTerrainDps = 400f;

        internal const float DefaultBeamFireRateHz = 30f;
        internal const float DefaultLiquidInterval = 0.02f;

        internal static IntPtr Ptr(Il2CppObjectBase obj)
        {
            return obj == null ? IntPtr.Zero : IL2CPP.Il2CppObjectBaseToPtr(obj);
        }

        internal static int GetWeaponId(BasicRangedWeapon weapon)
        {
            if (weapon == null) return -1;
            var attr = weapon.weaponAttr;
            if (attr != null) return attr.itemId;
            var data = weapon.weaponData;
            return data != null ? data.itemId : -1;
        }

        internal static float GetTeslaFireInterval()
        {
            float hz = Plugin.TeslaFireRateHz != null
                ? Mathf.Clamp(Plugin.TeslaFireRateHz.Value, 10f, 60f)
                : DefaultBeamFireRateHz;
            return 1f / hz;
        }

        internal static float GetLaserFireInterval()
        {
            float hz = Plugin.LaserFireRateHz != null
                ? Mathf.Clamp(Plugin.LaserFireRateHz.Value, 10f, 60f)
                : DefaultBeamFireRateHz;
            return 1f / hz;
        }

        /// <summary>
        /// 获取涵盖普通角色与尸潮丧尸的统一角色物理层掩码。
        /// </summary>
        internal static int GetAllCharactersLayerMask()
        {
            return GameConfigs.characterLayerMask.value | GameConfigs.hordeZombieLayerMask;
        }

        /// <summary>
        /// 判断该武器是否由玩家或友方人类角色持有，避免改动影响敌方僵尸或机甲。
        /// </summary>
        internal static bool IsPlayerSideHumanWeapon(BasicRangedWeapon weapon)
        {
            if (weapon == null || weapon.character == null) return false;
            var attr = weapon.weaponAttr;
            if (attr != null && attr.isMechWeapon) return false;

            var localPlayer = GameController.instance?.playerCharacter;
            if (localPlayer != null && Ptr(weapon.character) == Ptr(localPlayer))
                return true;

            try
            {
                return BasicCharacterController.IsPlayerSideCharacter(weapon.character);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 按 60Hz 基准将每帧概率折算为固定时间间隔下的等效触发概率，确保感电/点燃不受帧率影响。
        /// </summary>
        internal static float ScalePerFrameChanceToInterval(float perFrameChance, float intervalSeconds)
        {
            if (perFrameChance <= 0f) return 0f;
            if (perFrameChance >= 1f) return 1f;
            float frames = Mathf.Clamp(intervalSeconds * 60f, 0.25f, 30f);
            return 1f - Mathf.Pow(1f - perFrameChance, frames);
        }

        /// <summary>
        /// 将“每秒触发概率”精确折算为当前脉冲间隔内的单次触发概率。
        /// </summary>
        internal static float ScalePerSecondChanceToInterval(float chancePerSecond, float intervalSeconds)
        {
            if (chancePerSecond <= 0f) return 0f;
            if (chancePerSecond >= 0.999f) return 1f;
            float dt = Mathf.Clamp(intervalSeconds, 0.005f, 0.5f);
            return 1f - Mathf.Pow(1f - chancePerSecond, dt);
        }

        /// <summary>
        /// 同步 4 把手持特种武器的 ItemAttr_RangedWeapon 面板属性（单次伤害与攻速间隔），实现所见即所得。
        /// </summary>
        internal static void SyncAllWeaponAttrs()
        {
            if (Plugin.SyncWeaponPanelStats == null || !Plugin.SyncWeaponPanelStats.Value)
                return;

            var mgr = ItemManager.instance;
            if (mgr == null) return;

            if (Plugin.EnableTeslaRework.Value)
                SyncBeamAttr(mgr, IdTeslaGun, Plugin.TeslaDamagePerTick.Value, GetTeslaFireInterval());

            if (Plugin.EnableLaserRework.Value)
                SyncBeamAttr(mgr, IdMiningLaser, Plugin.LaserDamagePerTick.Value, GetLaserFireInterval());

            if (Plugin.EnableFlamethrowerRework.Value)
                SyncLiquidAttr(mgr, IdFlamethrower, Plugin.FlamethrowerTargetDps.Value);

            if (Plugin.EnableWaterGunRework.Value)
                SyncLiquidAttr(mgr, IdWaterGun, Plugin.WaterGunTargetDps.Value);
        }

        internal static void SyncWeaponAttrInstance(ItemAttr_RangedWeapon attr)
        {
            if (attr == null || attr.isMechWeapon) return;
            if (Plugin.SyncWeaponPanelStats == null || !Plugin.SyncWeaponPanelStats.Value)
                return;

            switch (attr.itemId)
            {
                case IdTeslaGun when Plugin.EnableTeslaRework.Value:
                    attr.damage = Plugin.TeslaDamagePerTick.Value;
                    attr.fireInterval = GetTeslaFireInterval();
                    break;
                case IdMiningLaser when Plugin.EnableLaserRework.Value:
                    attr.damage = Plugin.LaserDamagePerTick.Value;
                    attr.fireInterval = GetLaserFireInterval();
                    break;
                case IdFlamethrower when Plugin.EnableFlamethrowerRework.Value:
                    attr.damage = ComputePerShotDisplayDamage(Plugin.FlamethrowerTargetDps.Value, attr.fireInterval);
                    break;
                case IdWaterGun when Plugin.EnableWaterGunRework.Value:
                    attr.damage = ComputePerShotDisplayDamage(Plugin.WaterGunTargetDps.Value, attr.fireInterval);
                    break;
            }
        }

        private static void SyncBeamAttr(ItemManager mgr, int itemId, float damagePerTick, float fireInterval)
        {
            var baseAttr = mgr.FindItemAttrById(itemId);
            if (baseAttr == null) return;
            var rangedAttr = baseAttr.TryCast<ItemAttr_RangedWeapon>();
            if (rangedAttr == null || rangedAttr.isMechWeapon) return;

            rangedAttr.damage = damagePerTick;
            rangedAttr.fireInterval = fireInterval;
            Plugin.LogVerbose($"Synced beam weapon #{itemId}: damage={rangedAttr.damage:0.##}, fireInterval={rangedAttr.fireInterval:0.####} ({1f / rangedAttr.fireInterval:0.#}/s)");
        }

        private static void SyncLiquidAttr(ItemManager mgr, int itemId, float targetDps)
        {
            var baseAttr = mgr.FindItemAttrById(itemId);
            if (baseAttr == null) return;
            var rangedAttr = baseAttr.TryCast<ItemAttr_RangedWeapon>();
            if (rangedAttr == null || rangedAttr.isMechWeapon) return;

            rangedAttr.damage = ComputePerShotDisplayDamage(targetDps, rangedAttr.fireInterval);
            Plugin.LogVerbose($"Synced liquid weapon #{itemId}: damage={rangedAttr.damage:0.##} (interval={rangedAttr.fireInterval:0.###})");
        }

        private static float ComputePerShotDisplayDamage(float targetDps, float fireInterval)
        {
            float interval = fireInterval > 0.001f ? fireInterval : DefaultLiquidInterval;
            return Mathf.Max(0f, targetDps * interval);
        }
    }

    [HarmonyPatch(typeof(ItemManager), nameof(ItemManager.Init))]
    internal static class Patch_ItemManager_Init_SyncWeaponAttrs
    {
        [HarmonyPostfix]
        private static void Postfix()
        {
            try
            {
                SpecialWeaponHelper.SyncAllWeaponAttrs();
            }
            catch (Exception ex)
            {
                Plugin.LogOnce("sync_attrs_init", ex);
            }
        }
    }

    /// <summary>
    /// 在 BasicRangedWeapon.Init 完成 weaponAttr 赋值后同步一次面板属性，确保动态实例化或热重载后的武器实例属性一致。
    /// </summary>
    [HarmonyPatch(typeof(BasicRangedWeapon), nameof(BasicRangedWeapon.Init))]
    internal static class Patch_BasicRangedWeapon_Init_SyncWeaponAttrs
    {
        [HarmonyPostfix]
        private static void Postfix(BasicRangedWeapon __instance)
        {
            if (__instance == null) return;
            try
            {
                if (__instance.weaponAttr != null)
                    SpecialWeaponHelper.SyncWeaponAttrInstance(__instance.weaponAttr);
            }
            catch (Exception ex)
            {
                Plugin.LogOnce("sync_attrs_weapon", ex);
            }
        }
    }
}
