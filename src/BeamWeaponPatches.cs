using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using ZedZone.HordeMode;
using ZedZone.Net;

namespace ZedZone.BetterSpecialWeapons
{
    /// <summary>
    /// 光束类能量武器（特斯拉枪 467 / 采矿激光器 466）状态与公共辅助逻辑：
    /// - 主光束与连锁电弧同频高频脉冲（默认 30Hz），兼顾原版高频手感与严格帧率无关 DPS；
    /// - 电量消耗按真实流逝时间（dt）结算，不受攻速调整或帧率波动影响。
    /// </summary>
    internal static class BeamWeaponState
    {
        internal sealed class BeamTracker
        {
            public float PulseAccumulator;
            public float LastHitTime;
            public float LastEnergyDrainTime;
            public float ChainAccumulator;
            public float LastChainUpdateTime;
            public int LockedFrame = -1;
            public BasicCharacterController LockedVictim;
        }

        private static readonly Dictionary<IntPtr, BeamTracker> Trackers = new Dictionary<IntPtr, BeamTracker>();

        // 线程内把 UpdateChain 触发的真实链式周期传递给紧接着调用的 DealChainDamage
        [ThreadStatic]
        internal static float PendingChainTickSeconds;

        // 在地形拆墙/地下采矿作用域内，维持原版地形破坏速率并允许按配置微调
        [ThreadStatic]
        internal static int TerrainScopeDepth;

        [ThreadStatic]
        internal static int TerrainScopeWeaponId;

        internal static BeamTracker GetOrCreate(BasicRangedWeapon weapon)
        {
            IntPtr key = SpecialWeaponHelper.Ptr(weapon);
            if (!Trackers.TryGetValue(key, out var tracker))
            {
                if (Trackers.Count > 128)
                    Trackers.Clear();
                tracker = new BeamTracker();
                Trackers[key] = tracker;
            }
            return tracker;
        }

        internal static void Reset(BasicRangedWeapon weapon)
        {
            IntPtr key = SpecialWeaponHelper.Ptr(weapon);
            if (key != IntPtr.Zero)
                Trackers.Remove(key);
        }

        /// <summary>
        /// 判定当前渲染帧是否应当触发一次或多次（极低帧率补发）脉冲，返回本帧脉冲倍率：
        /// 停火超过阈值后的首次开火立刻触发首跳，连续扫射切目标时不额外白送免费跳伤，确保 DPS 严格恒定。
        /// </summary>
        internal static bool TryConsumePulse(BasicRangedWeapon weapon, float interval, out float effectiveInterval, out float pulseCountScale)
        {
            effectiveInterval = Mathf.Clamp(interval, 0.005f, 0.25f);
            pulseCountScale = 1f;

            var tracker = GetOrCreate(weapon);
            float now = Time.time;
            float prevHitTime = tracker.LastHitTime;
            float idleGap = now - prevHitTime;
            tracker.LastHitTime = now;

            if (prevHitTime <= 0f || idleGap > Mathf.Max(effectiveInterval * 3f, 0.25f))
            {
                tracker.PulseAccumulator = 0f;
                return true;
            }

            tracker.PulseAccumulator += Mathf.Min(Time.deltaTime, 0.20f);
            if (tracker.PulseAccumulator < effectiveInterval)
                return false;

            // 低帧率（帧间隔 > 脉冲间隔）时按比例合并补足，保证 30 FPS 与 144 FPS 总 DPS 完全一致
            int ticks = Mathf.Clamp((int)(tracker.PulseAccumulator / effectiveInterval), 1, 4);
            tracker.PulseAccumulator -= ticks * effectiveInterval;
            if (tracker.PulseAccumulator > effectiveInterval)
                tracker.PulseAccumulator = 0f;

            pulseCountScale = ticks;
            return true;
        }

        /// <summary>
        /// 按真实流逝时间计算本次 Fire_EnergyWeapon 应当扣除的精确电量。
        /// </summary>
        internal static float ComputeRealTimeEnergyDrain(BasicRangedWeapon weapon, float fallbackInterval)
        {
            var attr = weapon.weaponAttr;
            if (attr == null) return 0f;

            var tracker = GetOrCreate(weapon);
            float now = Time.time;
            float dt = fallbackInterval;
            if (tracker.LastEnergyDrainTime > 0f)
            {
                float raw = now - tracker.LastEnergyDrainTime;
                dt = (raw > 0.0005f && raw <= 0.25f) ? raw : fallbackInterval;
            }
            tracker.LastEnergyDrainTime = now;
            dt = Mathf.Clamp(dt, 0.004f, 0.20f);

            float mult = Plugin.EnergyCostMultiplier != null ? Mathf.Max(0.1f, Plugin.EnergyCostMultiplier.Value) : 1.0f;
            return attr.batteryEnergyPerSec * dt * mult;
        }

        internal static bool CanDamageTarget(BasicCharacterController attacker, BasicCharacterController victim)
        {
            if (victim == null) return false;
            if (BasicCharacterController.IsSameSideForDamage(attacker, victim) &&
                !NetSessionConfig.AllowsFriendlyFire(attacker, victim))
                return false;
            if (NetSessionConfig.BlocksFriendlyFire(attacker, victim))
                return false;
            return true;
        }

        /// <summary>
        /// 计算当前单次脉冲伤害，完整继承武器词条、耐久与角色通用枪伤乘区，并始终以配置值为基准。
        /// </summary>
        internal static float ComputePulseDamage(BasicRangedWeapon weapon, float configuredTickDamage)
        {
            var attr = weapon.weaponAttr;
            float baseDamage = weapon.damage;
            if (attr != null && attr.damage > 0.01f && Mathf.Abs(attr.damage - configuredTickDamage) > 0.01f)
            {
                baseDamage = baseDamage * (configuredTickDamage / attr.damage);
            }
            return baseDamage;
        }

        internal static bool TryReadBatteryEnergy(BasicRangedWeapon weapon, out float energy)
        {
            energy = 0f;
            var props = weapon?.weaponData?.properties;
            if (props == null || props.Length <= 2) return false;
            energy = props[2];
            return true;
        }

        internal static void WriteBatteryEnergy(BasicRangedWeapon weapon, float energy)
        {
            var props = weapon?.weaponData?.properties;
            if (props == null || props.Length <= 2) return;
            props[2] = Mathf.Max(0f, energy);
        }
    }

    /// <summary>
    /// 特斯拉枪（467）高压电弧自动索敌与目标锁躯：
    /// 复用原版 UpdateFiring 的真实地面投影射线起点与 bulletHitLayermask 校验，
    /// 优先锁定鼠标准星附近无墙遮挡的敌人，其次沿射线走廊自动锁定前方最近目标，
    /// 确保电弧精准锁死目标而不空电地板穿透伤害。
    /// </summary>
    [HarmonyPatch(typeof(BasicTeslaWeapon_Human), nameof(BasicTeslaWeapon_Human.GetAimingPoint))]
    internal static class Patch_BasicTeslaWeapon_Human_GetAimingPoint
    {
        private const int CharacterLayer = 9;

        [HarmonyPostfix]
        private static void Postfix(BasicTeslaWeapon_Human __instance, ref Vector2 __result)
        {
            if (Plugin.EnableTeslaRework == null || !Plugin.EnableTeslaRework.Value) return;
            if (__instance == null || SpecialWeaponHelper.GetWeaponId(__instance) != SpecialWeaponHelper.IdTeslaGun)
                return;
            if (!SpecialWeaponHelper.IsPlayerSideHumanWeapon(__instance))
                return;

            var tracker = BeamWeaponState.GetOrCreate(__instance);
            tracker.LockedVictim = null;
            tracker.LockedFrame = -1;

            float snapRadius = Plugin.TeslaArcSnapRadius != null
                ? Mathf.Clamp(Plugin.TeslaArcSnapRadius.Value, 0f, 4.5f)
                : 1.8f;
            if (snapRadius <= 0.05f) return;

            try
            {
                Transform muzzle = __instance.gunPoint != null ? __instance.gunPoint : __instance.transform;
                Vector3 gunPos3 = muzzle.position;
                var attacker = __instance.character;
                float standHeight = attacker != null ? attacker.standVisualHeight : 0f;

                // 与原版 BasicTeslaWeapon.UpdateFiring 保持完全一致的射线起点（扣除角色站立高度投影）
                Vector2 rayOrigin = new Vector2(gunPos3.x, gunPos3.y - standHeight);
                Vector2 gunPos = new Vector2(gunPos3.x, gunPos3.y);

                Vector2 seg = __result - rayOrigin;
                float segLen = seg.magnitude;
                if (segLen < 0.15f) return;

                Vector2 dir = seg / segLen;
                int bulletMask = GameConfigs.bulletHitLayermask.value;
                if (bulletMask == 0) return;

                float maxWeaponRange = __instance.fireRange > 1f ? __instance.fireRange : 14f;
                float searchAnchorDist = Mathf.Min(segLen, maxWeaponRange);
                Vector2 cursorAnchor = rayOrigin + dir * searchAnchorDist;

                // 仅扫描与原版 UpdateFiring 一致的 9 号角色受击层碰撞体，确保选中的目标必然可被 HitCharacter 命中
                int charHitMask = bulletMask & (1 << CharacterLayer);
                if (charHitMask == 0) charHitMask = 1 << CharacterLayer;

                Transform attackerTransform = attacker != null ? attacker.transform : null;
                IntPtr attackerPtr = SpecialWeaponHelper.Ptr(attacker);
                Vector2 bestPos = __result;
                BasicCharacterController bestVictim = null;

                // 优先级 1：沿“枪口/投影 → 准星”走廊扫掠，始终优先锁定路线前方最近且无墙体遮挡的敌人（避免跨怪电地板）
                Vector2 visSeg = cursorAnchor - gunPos;
                float visLen = visSeg.magnitude;
                Vector2 visDir = visLen > 0.05f ? visSeg / visLen : dir;

                var corridorHits = Physics2D.CircleCastAll(rayOrigin, snapRadius, dir, searchAnchorDist, charHitMask);
                if (corridorHits != null && corridorHits.Length > 0)
                {
                    float bestAlong = float.PositiveInfinity;
                    for (int i = 0; i < corridorHits.Length; i++)
                    {
                        var col = corridorHits[i].collider;
                        if (col == null) continue;
                        if (attackerTransform != null && col.transform.IsChildOf(attackerTransform))
                            continue;

                        var victim = BasicCharacterController.GetCharacterFromCollider(col);
                        if (victim == null || victim.deadFlag) continue;
                        IntPtr victimPtr = SpecialWeaponHelper.Ptr(victim);
                        if (victimPtr == IntPtr.Zero || victimPtr == attackerPtr) continue;
                        if (!BeamWeaponState.CanDamageTarget(attacker, victim))
                            continue;

                        Bounds bounds = col.bounds;
                        Vector2 colCenter = new Vector2(bounds.center.x, bounds.center.y);
                        Vector2 rel = colCenter - rayOrigin;

                        float along = Vector2.Dot(rel, dir);
                        if (along < 0.08f || along > searchAnchorDist + 0.35f)
                            continue;

                        float perpGround = Mathf.Abs(rel.x * dir.y - rel.y * dir.x);
                        Vector2 relVis = colCenter - gunPos;
                        float alongVis = Vector2.Dot(relVis, visDir);
                        float perpVis = (alongVis >= -0.25f && alongVis <= visLen + 0.5f)
                            ? Mathf.Abs(relVis.x * visDir.y - relVis.y * visDir.x)
                            : float.PositiveInfinity;
                        float minPerp = Mathf.Min(perpGround, perpVis);

                        float colAllowance = Mathf.Min(0.45f, Mathf.Max(bounds.extents.x, bounds.extents.y));
                        if (minPerp > snapRadius + colAllowance)
                            continue;

                        if (along >= bestAlong)
                            continue;

                        if (!VerifyBeamHitsVictim(rayOrigin, colCenter, attacker, attackerTransform, victimPtr, bulletMask))
                            continue;

                        bestAlong = along;
                        bestPos = colCenter;
                        bestVictim = victim;
                    }
                }

                // 优先级 2：走廊内无目标时，搜索准星周围 snapRadius 半径内最近的可命中敌人
                if (bestVictim == null)
                {
                    var nearHits = Physics2D.OverlapCircleAll(cursorAnchor, snapRadius, charHitMask);
                    if (nearHits != null && nearHits.Length > 0)
                    {
                        float bestCursorDistSq = float.PositiveInfinity;
                        for (int i = 0; i < nearHits.Length; i++)
                        {
                            var col = nearHits[i];
                            if (col == null) continue;
                            if (attackerTransform != null && col.transform.IsChildOf(attackerTransform))
                                continue;

                            var victim = BasicCharacterController.GetCharacterFromCollider(col);
                            if (victim == null || victim.deadFlag) continue;
                            IntPtr victimPtr = SpecialWeaponHelper.Ptr(victim);
                            if (victimPtr == IntPtr.Zero || victimPtr == attackerPtr) continue;
                            if (!BeamWeaponState.CanDamageTarget(attacker, victim))
                                continue;

                            Bounds bounds = col.bounds;
                            Vector2 colCenter = new Vector2(bounds.center.x, bounds.center.y);
                            float distFromOrigin = Vector2.Distance(rayOrigin, colCenter);
                            if (distFromOrigin < 0.08f || distFromOrigin > maxWeaponRange + 0.5f)
                                continue;

                            float cursorDistSq = (colCenter - cursorAnchor).sqrMagnitude;
                            if (cursorDistSq >= bestCursorDistSq)
                                continue;

                            if (!VerifyBeamHitsVictim(rayOrigin, colCenter, attacker, attackerTransform, victimPtr, bulletMask))
                                continue;

                            bestCursorDistSq = cursorDistSq;
                            bestPos = colCenter;
                            bestVictim = victim;
                        }
                    }
                }

                if (bestVictim != null)
                {
                    __result = bestPos;
                    tracker.LockedVictim = bestVictim;
                    tracker.LockedFrame = Time.frameCount;
                }
            }
            catch (Exception ex)
            {
                Plugin.LogOnce("tesla_arc_snap", ex);
            }
        }

        private static bool VerifyBeamHitsVictim(
            Vector2 rayOrigin,
            Vector2 targetPoint,
            BasicCharacterController attacker,
            Transform attackerTransform,
            IntPtr expectedVictimPtr,
            int bulletMask)
        {
            Vector2 delta = targetPoint - rayOrigin;
            float dist = delta.magnitude;
            if (dist < 0.05f) return false;

            var rayHits = Physics2D.RaycastAll(rayOrigin, delta / dist, dist + 0.5f, bulletMask);
            if (rayHits == null || rayHits.Length == 0) return false;

            float closestSq = float.PositiveInfinity;
            Collider2D closestCol = null;

            for (int i = 0; i < rayHits.Length; i++)
            {
                var col = rayHits[i].collider;
                if (col == null) continue;
                if (attackerTransform != null && col.transform.IsChildOf(attackerTransform))
                    continue;

                if (col.gameObject.layer == CharacterLayer)
                {
                    var ch = BasicCharacterController.GetCharacterFromCollider(col);
                    if (ch == null || !BeamWeaponState.CanDamageTarget(attacker, ch))
                        continue;
                }

                Vector2 d = rayHits[i].point - rayOrigin;
                float sq = d.sqrMagnitude;
                if (sq < closestSq)
                {
                    closestSq = sq;
                    closestCol = col;
                }
            }

            if (closestCol == null || closestCol.gameObject.layer != CharacterLayer)
                return false;

            var hitChar = BasicCharacterController.GetCharacterFromCollider(closestCol);
            return hitChar != null && SpecialWeaponHelper.Ptr(hitChar) == expectedVictimPtr;
        }
    }

    /// <summary>
    /// 本帧锁定或命中敌人时，把视觉电弧终点无条件锁在目标躯干中心，避免落到脚底或擦边地面导致穿模错觉。
    /// </summary>
    [HarmonyPatch(typeof(BasicTeslaWeapon), nameof(BasicTeslaWeapon.UpdateLaserPoint), typeof(Vector3), typeof(Vector3))]
    internal static class Patch_BasicTeslaWeapon_UpdateLaserPoint
    {
        private const int CharacterLayer = 9;

        [HarmonyPrefix]
        private static void Prefix(BasicTeslaWeapon __instance, ref Vector3 endPoint)
        {
            if (Plugin.EnableTeslaRework == null || !Plugin.EnableTeslaRework.Value) return;
            if (__instance == null || SpecialWeaponHelper.GetWeaponId(__instance) != SpecialWeaponHelper.IdTeslaGun)
                return;
            if (!SpecialWeaponHelper.IsPlayerSideHumanWeapon(__instance))
                return;

            try
            {
                var tracker = BeamWeaponState.GetOrCreate(__instance);
                var victim = tracker.LockedVictim;
                if (tracker.LockedFrame == Time.frameCount && victim != null && !victim.deadFlag)
                {
                    Vector3 torso = victim.GetTorsePositionOrRoot();
                    endPoint = new Vector3(torso.x, torso.y, endPoint.z);
                    return;
                }

                // 若未经磁吸而由原版射线直接命中的敌人，按命中点兜底反查角色并锁到躯干
                var attacker = __instance.character;
                Transform attackerTransform = attacker != null ? attacker.transform : null;
                IntPtr attackerPtr = SpecialWeaponHelper.Ptr(attacker);
                Vector2 end2 = new Vector2(endPoint.x, endPoint.y);
                var hits = Physics2D.OverlapCircleAll(end2, 0.25f, 1 << CharacterLayer);
                if (hits == null || hits.Length == 0) return;

                BasicCharacterController best = null;
                float bestSq = float.PositiveInfinity;
                for (int i = 0; i < hits.Length; i++)
                {
                    var col = hits[i];
                    if (col == null) continue;
                    if (attackerTransform != null && col.transform.IsChildOf(attackerTransform))
                        continue;
                    var ch = BasicCharacterController.GetCharacterFromCollider(col);
                    if (ch == null || ch.deadFlag) continue;
                    IntPtr ptr = SpecialWeaponHelper.Ptr(ch);
                    if (ptr == IntPtr.Zero || ptr == attackerPtr) continue;
                    if (!BeamWeaponState.CanDamageTarget(attacker, ch)) continue;

                    Bounds b = col.bounds;
                    float sq = (new Vector2(b.center.x, b.center.y) - end2).sqrMagnitude;
                    if (sq < bestSq)
                    {
                        bestSq = sq;
                        best = ch;
                    }
                }

                if (best != null)
                {
                    Vector3 torso = best.GetTorsePositionOrRoot();
                    endPoint = new Vector3(torso.x, torso.y, endPoint.z);
                }
            }
            catch (Exception ex)
            {
                Plugin.LogOnce("tesla_update_laser_point", ex);
            }
        }
    }

    /// <summary>
    /// 特斯拉枪（467）主目标命中改为可配置高频脉冲（默认 30Hz），保持超高攻速手感且严格帧率无关。
    /// </summary>
    [HarmonyPatch(typeof(BasicTeslaWeapon), nameof(BasicTeslaWeapon.HitCharacter), typeof(RaycastHit2D))]
    internal static class Patch_BasicTeslaWeapon_HitCharacter
    {
        [HarmonyPrefix]
        private static bool Prefix(BasicTeslaWeapon __instance, RaycastHit2D hit2D)
        {
            if (Plugin.EnableTeslaRework == null || !Plugin.EnableTeslaRework.Value) return true;
            if (__instance == null || SpecialWeaponHelper.GetWeaponId(__instance) != SpecialWeaponHelper.IdTeslaGun)
                return true;
            if (!SpecialWeaponHelper.IsPlayerSideHumanWeapon(__instance))
                return true;

            try
            {
                var collider = hit2D.collider;
                var victim = BasicCharacterController.GetCharacterFromCollider(collider);
                if (victim == null) return false;
                if (!BeamWeaponState.CanDamageTarget(__instance.character, victim))
                    return false;

                // 每帧维持主目标指针，确保原版链式传导 UpdateChain 电弧不断链，同时同步供 UpdateLaserPoint 锁躯
                __instance.m_beamCharacter = victim;
                var hitTracker = BeamWeaponState.GetOrCreate(__instance);
                hitTracker.LockedVictim = victim;
                hitTracker.LockedFrame = Time.frameCount;

                float pulseInterval = SpecialWeaponHelper.GetTeslaFireInterval();
                if (!BeamWeaponState.TryConsumePulse(__instance, pulseInterval, out float effectiveInterval, out float pulseCountScale))
                    return false;

                float totalInterval = effectiveInterval * pulseCountScale;

                // 折算为帧率无关的感电（buffId=3）与校准后的电击硬直麻痹概率
                float shockChance = SpecialWeaponHelper.ScalePerFrameChanceToInterval(__instance.burnRate, totalInterval);
                if (UnityEngine.Random.value < shockChance)
                {
                    var gd = GameController.instance?.gameData;
                    if (gd != null)
                        victim.AddBuff(3, gd.inGameTime + __instance.burnTime);
                }

                float stunPerSec = Plugin.TeslaStunChancePerSec != null
                    ? Mathf.Clamp01(Plugin.TeslaStunChancePerSec.Value)
                    : 0.22f;
                if (stunPerSec > 0.001f)
                {
                    float stunChance = SpecialWeaponHelper.ScalePerSecondChanceToInterval(stunPerSec, totalInterval);
                    if (UnityEngine.Random.value < stunChance)
                        victim.ChangeAnimationScaleForTime(0.05f, 0.45f);
                }

                float pulseDamage = BeamWeaponState.ComputePulseDamage(__instance, Plugin.TeslaDamagePerTick.Value);
                float finalDamage = pulseDamage * pulseCountScale * BasicCharacterController.GetEnemyDamageFloat(__instance.character);

                Vector2 hitPoint = hit2D.point;
                float facingSign = __instance.character?.spriteParent != null
                    ? __instance.character.spriteParent.localScale.x
                    : 1f;
                Vector3 right = __instance.transform.right;
                float bulletForce = __instance.weaponAttr != null ? __instance.weaponAttr.bulletForce : 0f;
                Vector2 force = new Vector2(right.x * facingSign * bulletForce, right.y * facingSign * bulletForce);

                var scope = CharacterHitContext.AttackerScope(__instance.character);
                try
                {
                    victim.OnHit(finalDamage, hitPoint, force, null, 50f, true, true, true, true);
                }
                finally
                {
                    scope.Dispose();
                }

                HordeScoreTracker.NoteDamage(victim, __instance.character, finalDamage);
                return false;
            }
            catch (Exception ex)
            {
                Plugin.LogOnce("tesla_hit_char", ex);
                return true;
            }
        }
    }

    /// <summary>
    /// 让原版 UpdateChain 触发 DealChainDamage 的节拍与主光束 FireRateHz 保持同频，
    /// 避免“主光束 30Hz 跳 60、连锁 10Hz 跳 117”的视觉倒挂。
    /// </summary>
    [HarmonyPatch(typeof(BasicTeslaWeapon), nameof(BasicTeslaWeapon.UpdateChain), typeof(Vector2), typeof(bool))]
    internal static class Patch_BasicTeslaWeapon_UpdateChain_SyncRate
    {
        [HarmonyPrefix]
        private static void Prefix(BasicTeslaWeapon __instance, bool authority)
        {
            BeamWeaponState.PendingChainTickSeconds = 0f;
            if (!authority) return;
            if (Plugin.EnableTeslaRework == null || !Plugin.EnableTeslaRework.Value) return;
            if (__instance == null || SpecialWeaponHelper.GetWeaponId(__instance) != SpecialWeaponHelper.IdTeslaGun)
                return;
            if (!SpecialWeaponHelper.IsPlayerSideHumanWeapon(__instance))
                return;

            try
            {
                float pulseInterval = SpecialWeaponHelper.GetTeslaFireInterval();
                var tracker = BeamWeaponState.GetOrCreate(__instance);
                float now = Time.time;
                float dt = (tracker.LastChainUpdateTime > 0f && (now - tracker.LastChainUpdateTime) <= 0.25f)
                    ? Time.deltaTime
                    : pulseInterval;
                tracker.LastChainUpdateTime = now;
                tracker.ChainAccumulator += Mathf.Min(dt, 0.20f);

                if (tracker.ChainAccumulator >= pulseInterval)
                {
                    float consumed = Mathf.Min(tracker.ChainAccumulator, pulseInterval * 4f);
                    tracker.ChainAccumulator -= consumed;
                    if (tracker.ChainAccumulator > pulseInterval)
                        tracker.ChainAccumulator = 0f;

                    BeamWeaponState.PendingChainTickSeconds = consumed;
                    // 推动原版 0.1s 阈值立即在本次 UpdateChain 中调用 DealChainDamage
                    __instance.m_chainDamageAccum = 0.11f;
                }
                else
                {
                    // 未到下一次脉冲时刻，压住原版 0.1s 累积器不提前触发
                    __instance.m_chainDamageAccum = 0f;
                }
            }
            catch (Exception ex)
            {
                Plugin.LogOnce("tesla_chain_sync", ex);
            }
        }

        [HarmonyFinalizer]
        private static void Finalizer()
        {
            BeamWeaponState.PendingChainTickSeconds = 0f;
        }
    }

    /// <summary>
    /// 特斯拉枪链式传导同步脉冲伤害：与主光束同频并按 ChainFalloff 逐跳单调衰减。
    /// </summary>
    [HarmonyPatch(typeof(BasicTeslaWeapon), nameof(BasicTeslaWeapon.DealChainDamage), typeof(float))]
    internal static class Patch_BasicTeslaWeapon_DealChainDamage
    {
        [HarmonyPrefix]
        private static bool Prefix(BasicTeslaWeapon __instance, float tickSeconds)
        {
            if (Plugin.EnableTeslaRework == null || !Plugin.EnableTeslaRework.Value) return true;
            if (__instance == null || SpecialWeaponHelper.GetWeaponId(__instance) != SpecialWeaponHelper.IdTeslaGun)
                return true;
            if (!SpecialWeaponHelper.IsPlayerSideHumanWeapon(__instance))
                return true;

            try
            {
                var chain = __instance.m_chain;
                if (chain == null || chain.Count <= 1) return false;

                float pulseInterval = SpecialWeaponHelper.GetTeslaFireInterval();
                float realTick = BeamWeaponState.PendingChainTickSeconds > 0.001f
                    ? BeamWeaponState.PendingChainTickSeconds
                    : tickSeconds;
                float pulseScale = Mathf.Clamp(realTick / pulseInterval, 0.25f, 4.0f);

                float pulseDamage = BeamWeaponState.ComputePulseDamage(__instance, Plugin.TeslaDamagePerTick.Value);
                float chainDamage = pulseDamage * pulseScale * BasicCharacterController.GetEnemyDamageFloat(__instance.character);
                float falloff = Plugin.TeslaChainFalloff != null
                    ? Mathf.Clamp(Plugin.TeslaChainFalloff.Value, 0.1f, 1.0f)
                    : 0.65f;

                var gd = GameController.instance?.gameData;
                float buffEndTime = gd != null ? gd.inGameTime + __instance.burnTime : 0f;
                float bulletForce = __instance.weaponAttr != null ? __instance.weaponAttr.bulletForce : 0f;

                var scope = CharacterHitContext.AttackerScope(__instance.character);
                try
                {
                    for (int i = 1; i < chain.Count; i++)
                    {
                        chainDamage *= falloff;
                        if (!__instance.ChainLinkValid(i)) continue;

                        var victim = chain[i];
                        if (victim == null) continue;

                        Vector3 torse = victim.GetTorsePositionOrRoot();
                        Vector2 victimPos = new Vector2(torse.x, torse.y);
                        Vector2 prevPos = __instance.LinkPoint(i - 1, victimPos);
                        Vector2 diff = victimPos - prevPos;
                        Vector2 dir = diff.sqrMagnitude > 0.0001f ? diff.normalized : Vector2.right;
                        Vector2 force = dir * bulletForce;

                        victim.OnHit(chainDamage, victimPos, force, null, 50f, true, true, true, true);
                        HordeScoreTracker.NoteDamage(victim, __instance.character, chainDamage);

                        if (!victim.deadFlag && gd != null)
                            victim.AddBuff(3, buffEndTime);
                    }
                }
                finally
                {
                    scope.Dispose();
                }

                return false;
            }
            catch (Exception ex)
            {
                Plugin.LogOnce("tesla_chain", ex);
                return true;
            }
        }
    }

    /// <summary>
    /// 采矿激光器（466）命中角色改为可配置高频脉冲（默认 50Hz），并可调整击杀瞬间的物理击飞倍率（原版误用 bulletNumber=1）。
    /// </summary>
    [HarmonyPatch(typeof(BasicLaserWeapon), nameof(BasicLaserWeapon.HitCharacter), typeof(RaycastHit2D))]
    internal static class Patch_BasicLaserWeapon_HitCharacter
    {
        [HarmonyPrefix]
        private static bool Prefix(BasicLaserWeapon __instance, RaycastHit2D hit2D)
        {
            if (Plugin.EnableLaserRework == null || !Plugin.EnableLaserRework.Value) return true;
            if (__instance == null || SpecialWeaponHelper.GetWeaponId(__instance) != SpecialWeaponHelper.IdMiningLaser)
                return true;
            if (!SpecialWeaponHelper.IsPlayerSideHumanWeapon(__instance))
                return true;

            try
            {
                var collider = hit2D.collider;
                var victim = BasicCharacterController.GetCharacterFromCollider(collider);
                if (victim == null) return false;
                if (!BeamWeaponState.CanDamageTarget(__instance.character, victim))
                    return false;

                float pulseInterval = SpecialWeaponHelper.GetLaserFireInterval();
                if (!BeamWeaponState.TryConsumePulse(__instance, pulseInterval, out float effectiveInterval, out float pulseCountScale))
                    return false;

                float totalInterval = effectiveInterval * pulseCountScale;

                // 折算为帧率无关的点燃（buffId=1）概率
                float burnChance = SpecialWeaponHelper.ScalePerFrameChanceToInterval(__instance.burnRate, totalInterval);
                if (UnityEngine.Random.value < burnChance)
                {
                    var gd = GameController.instance?.gameData;
                    if (gd != null)
                        victim.AddBuff(1, gd.inGameTime + __instance.burnTime);
                }

                float pulseDamage = BeamWeaponState.ComputePulseDamage(__instance, Plugin.LaserDamagePerTick.Value);
                float finalDamage = pulseDamage * pulseCountScale * BasicCharacterController.GetEnemyDamageFloat(__instance.character);

                Vector2 hitPoint = hit2D.point;
                Vector3 wp = __instance.transform.position;
                Vector2 diff = new Vector2(hitPoint.x - wp.x, hitPoint.y - wp.y);
                Vector2 dir = diff.sqrMagnitude > 0.0001f ? diff.normalized : Vector2.right;
                float bulletForce = __instance.weaponAttr != null ? __instance.weaponAttr.bulletForce : 0f;
                Vector2 force = dir * bulletForce;

                float deathMult = Plugin.LaserDeathMultiplier != null
                    ? Mathf.Max(1f, Plugin.LaserDeathMultiplier.Value)
                    : 25f;

                var scope = CharacterHitContext.AttackerScope(__instance.character);
                try
                {
                    victim.OnHit(finalDamage, hitPoint, force, null, deathMult, true, true, true, true);
                }
                finally
                {
                    scope.Dispose();
                }

                HordeScoreTracker.NoteDamage(victim, __instance.character, finalDamage);
                return false;
            }
            catch (Exception ex)
            {
                Plugin.LogOnce("laser_hit_char", ex);
                return true;
            }
        }
    }

    /// <summary>
    /// 停火关闭光束时重置脉冲与耗电计时器，保证下一次点射即刻出首跳。
    /// </summary>
    [HarmonyPatch(typeof(BasicTeslaWeapon), nameof(BasicTeslaWeapon.BeamOff))]
    internal static class Patch_BasicTeslaWeapon_BeamOff_Reset
    {
        [HarmonyPostfix]
        private static void Postfix(BasicTeslaWeapon __instance) => BeamWeaponState.Reset(__instance);
    }

    [HarmonyPatch(typeof(BasicLaserWeapon), nameof(BasicLaserWeapon.BeamOff))]
    internal static class Patch_BasicLaserWeapon_BeamOff_Reset
    {
        [HarmonyPostfix]
        private static void Postfix(BasicLaserWeapon __instance) => BeamWeaponState.Reset(__instance);
    }

    /// <summary>
    /// 地形拆墙/地下采矿作用域保护：确保面板改为单次脉冲伤害后，采矿激光与特斯拉拆障碍效率严格按原版基准及配置倍率运行。
    /// </summary>
    [HarmonyPatch(typeof(BasicLaserWeapon), nameof(BasicLaserWeapon.HitTerrainObject), typeof(RaycastHit2D))]
    internal static class Patch_BasicLaserWeapon_HitTerrainObject_Scope
    {
        [HarmonyPrefix]
        private static void Prefix(BasicLaserWeapon __instance, ref bool __state)
        {
            if (Plugin.EnableLaserRework == null || !Plugin.EnableLaserRework.Value) return;
            if (__instance == null || SpecialWeaponHelper.GetWeaponId(__instance) != SpecialWeaponHelper.IdMiningLaser) return;
            BeamWeaponState.TerrainScopeDepth++;
            BeamWeaponState.TerrainScopeWeaponId = SpecialWeaponHelper.IdMiningLaser;
            __state = true;
        }

        [HarmonyFinalizer]
        private static void Finalizer(bool __state)
        {
            if (__state && BeamWeaponState.TerrainScopeDepth > 0)
                BeamWeaponState.TerrainScopeDepth--;
        }
    }

    [HarmonyPatch(typeof(BasicLaserWeapon), nameof(BasicLaserWeapon.HitUnderground), typeof(RaycastHit2D), typeof(Vector2))]
    internal static class Patch_BasicLaserWeapon_HitUnderground_Scope
    {
        [HarmonyPrefix]
        private static void Prefix(BasicLaserWeapon __instance, ref bool __state)
        {
            if (Plugin.EnableLaserRework == null || !Plugin.EnableLaserRework.Value) return;
            if (__instance == null || SpecialWeaponHelper.GetWeaponId(__instance) != SpecialWeaponHelper.IdMiningLaser) return;
            BeamWeaponState.TerrainScopeDepth++;
            BeamWeaponState.TerrainScopeWeaponId = SpecialWeaponHelper.IdMiningLaser;
            __state = true;
        }

        [HarmonyFinalizer]
        private static void Finalizer(bool __state)
        {
            if (__state && BeamWeaponState.TerrainScopeDepth > 0)
                BeamWeaponState.TerrainScopeDepth--;
        }
    }

    [HarmonyPatch(typeof(BasicTeslaWeapon), nameof(BasicTeslaWeapon.HitTerrainObject), typeof(RaycastHit2D))]
    internal static class Patch_BasicTeslaWeapon_HitTerrainObject_Scope
    {
        [HarmonyPrefix]
        private static void Prefix(BasicTeslaWeapon __instance, ref bool __state)
        {
            if (Plugin.EnableTeslaRework == null || !Plugin.EnableTeslaRework.Value) return;
            if (__instance == null || SpecialWeaponHelper.GetWeaponId(__instance) != SpecialWeaponHelper.IdTeslaGun) return;
            BeamWeaponState.TerrainScopeDepth++;
            BeamWeaponState.TerrainScopeWeaponId = SpecialWeaponHelper.IdTeslaGun;
            __state = true;
        }

        [HarmonyFinalizer]
        private static void Finalizer(bool __state)
        {
            if (__state && BeamWeaponState.TerrainScopeDepth > 0)
                BeamWeaponState.TerrainScopeDepth--;
        }
    }

    [HarmonyPatch]
    internal static class Patch_BasicRangedWeapon_GetDamage_TerrainScope
    {
        [HarmonyTargetMethod]
        private static MethodBase TargetMethod()
        {
            return AccessTools.PropertyGetter(typeof(BasicRangedWeapon), nameof(BasicRangedWeapon.damage));
        }

        [HarmonyPostfix]
        private static void Postfix(BasicRangedWeapon __instance, ref float __result)
        {
            if (BeamWeaponState.TerrainScopeDepth <= 0 || __instance == null) return;
            var attr = __instance.weaponAttr;
            if (attr == null || attr.damage <= 0.01f) return;

            if (attr.itemId == SpecialWeaponHelper.IdMiningLaser &&
                BeamWeaponState.TerrainScopeWeaponId == SpecialWeaponHelper.IdMiningLaser)
            {
                float mult = Plugin.LaserMiningMultiplier != null ? Plugin.LaserMiningMultiplier.Value : 1.0f;
                __result = __result * (SpecialWeaponHelper.VanillaLaserTerrainDps / attr.damage) * mult;
            }
            else if (attr.itemId == SpecialWeaponHelper.IdTeslaGun &&
                     BeamWeaponState.TerrainScopeWeaponId == SpecialWeaponHelper.IdTeslaGun)
            {
                float mult = Plugin.TeslaTerrainDamageMultiplier != null ? Plugin.TeslaTerrainDamageMultiplier.Value : 1.0f;
                __result = __result * (SpecialWeaponHelper.VanillaTeslaTerrainDps / attr.damage) * mult;
            }
        }
    }

    /// <summary>
    /// 修复特斯拉枪原版耗电公式 Bug：按真实流逝时间与标称 batteryEnergyPerSec 精确扣除电量。
    /// </summary>
    [HarmonyPatch(typeof(BasicTeslaWeapon_Human), nameof(BasicTeslaWeapon_Human.Fire_EnergyWeapon))]
    internal static class Patch_BasicTeslaWeapon_Human_FireEnergy
    {
        [HarmonyPrefix]
        private static void Prefix(BasicTeslaWeapon_Human __instance, out float __state)
        {
            __state = -1f;
            if (Plugin.FixEnergyDrainBug == null || !Plugin.FixEnergyDrainBug.Value) return;
            if (BeamWeaponState.TryReadBatteryEnergy(__instance, out float before))
                __state = before;
        }

        [HarmonyPostfix]
        private static void Postfix(BasicTeslaWeapon_Human __instance, float __state)
        {
            if (__state < 0f || __instance == null) return;
            try
            {
                float exactCost = BeamWeaponState.ComputeRealTimeEnergyDrain(__instance, SpecialWeaponHelper.GetTeslaFireInterval());
                BeamWeaponState.WriteBatteryEnergy(__instance, __state - exactCost);
            }
            catch (Exception ex)
            {
                Plugin.LogOnce("tesla_energy_fix", ex);
            }
        }
    }

    /// <summary>
    /// 修复采矿激光器原版耗电公式 Bug。
    /// </summary>
    [HarmonyPatch(typeof(BasicLaserWeapon_Human), nameof(BasicLaserWeapon_Human.Fire_EnergyWeapon))]
    internal static class Patch_BasicLaserWeapon_Human_FireEnergy
    {
        [HarmonyPrefix]
        private static void Prefix(BasicLaserWeapon_Human __instance, out float __state)
        {
            __state = -1f;
            if (Plugin.FixEnergyDrainBug == null || !Plugin.FixEnergyDrainBug.Value) return;
            if (BeamWeaponState.TryReadBatteryEnergy(__instance, out float before))
                __state = before;
        }

        [HarmonyPostfix]
        private static void Postfix(BasicLaserWeapon_Human __instance, float __state)
        {
            if (__state < 0f || __instance == null) return;
            try
            {
                float exactCost = BeamWeaponState.ComputeRealTimeEnergyDrain(__instance, SpecialWeaponHelper.GetLaserFireInterval());
                BeamWeaponState.WriteBatteryEnergy(__instance, __state - exactCost);
            }
            catch (Exception ex)
            {
                Plugin.LogOnce("laser_energy_fix", ex);
            }
        }
    }
}
