using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using ZedZone.HordeMode;

namespace ZedZone.BetterSpecialWeapons
{
    /// <summary>
    /// 喷射类液体武器（M2 火焰喷射器 468 / 高压喷水枪 469）精准 DPS、真实时间耗液与沿途穿透判定。
    /// 仅针对玩家侧 468 / 469 生效，不波及手雷、燃烧瓶、榴弹及敌方喷吐/喷火丧尸。
    /// </summary>
    internal static class LiquidWeaponState
    {
        internal static readonly Dictionary<IntPtr, float> LastDrainTime = new Dictionary<IntPtr, float>();
        internal static readonly Dictionary<IntPtr, float> LastJetTime = new Dictionary<IntPtr, float>();
        internal static readonly Dictionary<IntPtr, float> LastExtinguishTime = new Dictionary<IntPtr, float>();
        internal static readonly Dictionary<IntPtr, float> LastExtinguishSoundTime = new Dictionary<IntPtr, float>();

        internal static float ConsumeElapsedSeconds(Dictionary<IntPtr, float> table, BasicRangedWeapon weapon, float fallbackInterval)
        {
            IntPtr key = SpecialWeaponHelper.Ptr(weapon);
            float now = Time.time;
            float dt = fallbackInterval;

            if (table.TryGetValue(key, out float prev))
            {
                float raw = now - prev;
                // 停火超过 0.25s 视为新开火周期，按单次标称间隔结算
                dt = (raw > 0.0005f && raw <= 0.25f) ? raw : fallbackInterval;
            }
            else if (table.Count > 128)
            {
                table.Clear();
            }

            table[key] = now;
            return Mathf.Clamp(dt, 0.004f, 0.15f);
        }

        internal static void Reset(BasicRangedWeapon weapon)
        {
            IntPtr key = SpecialWeaponHelper.Ptr(weapon);
            if (key == IntPtr.Zero) return;
            LastDrainTime.Remove(key);
            LastJetTime.Remove(key);
            LastExtinguishTime.Remove(key);
            LastExtinguishSoundTime.Remove(key);
        }

        internal static bool TryGetTargetDps(BasicRangedWeapon weapon, out float targetDps, out bool isFlame)
        {
            targetDps = 0f;
            isFlame = false;
            int id = SpecialWeaponHelper.GetWeaponId(weapon);
            if (id == SpecialWeaponHelper.IdFlamethrower &&
                Plugin.EnableFlamethrowerRework != null &&
                Plugin.EnableFlamethrowerRework.Value)
            {
                targetDps = Plugin.FlamethrowerTargetDps.Value;
                isFlame = true;
                return true;
            }

            if (id == SpecialWeaponHelper.IdWaterGun &&
                Plugin.EnableWaterGunRework != null &&
                Plugin.EnableWaterGunRework.Value)
            {
                targetDps = Plugin.WaterGunTargetDps.Value;
                isFlame = false;
                return true;
            }

            return false;
        }
    }

    /// <summary>
    /// 修复 M2 火焰喷射器与高压喷水枪原版按固定 60 帧乱扣燃料/水量的 Bug，改为按真实流逝时间精准扣除。
    /// </summary>
    [HarmonyPatch(typeof(BasicRangedWeapon_FlameThrower_Human), nameof(BasicRangedWeapon_FlameThrower_Human.Fire_LiquidWeapon))]
    internal static class Patch_FlameThrower_Human_FireLiquid
    {
        [HarmonyPrefix]
        private static void Prefix(BasicRangedWeapon_FlameThrower_Human __instance, out float __state)
        {
            __state = -1f;
            if (Plugin.FixLiquidDrainBug == null || !Plugin.FixLiquidDrainBug.Value) return;
            if (__instance?.weaponData == null) return;
            __state = __instance.weaponData.rangedWeaponLiquidVolume;
        }

        [HarmonyPostfix]
        private static void Postfix(BasicRangedWeapon_FlameThrower_Human __instance, float __state)
        {
            if (__state < 0f || __instance?.weaponData == null) return;

            try
            {
                float fallback = __instance.fireInterval > 0.005f
                    ? __instance.fireInterval
                    : SpecialWeaponHelper.DefaultLiquidInterval;
                float dt = LiquidWeaponState.ConsumeElapsedSeconds(LiquidWeaponState.LastDrainTime, __instance, fallback);

                float mult = Plugin.LiquidCostMultiplier != null ? Mathf.Max(0.1f, Plugin.LiquidCostMultiplier.Value) : 1.0f;
                float exactCost = __instance.liquidConsumptionPerSec * dt * mult;
                __instance.weaponData.rangedWeaponLiquidVolume = Mathf.Max(0f, __state - exactCost);
            }
            catch (Exception ex)
            {
                Plugin.LogOnce("liquid_drain_fix", ex);
            }
        }
    }

    [HarmonyPatch(typeof(BasicRangedWeapon_FlameThrower), nameof(BasicRangedWeapon_FlameThrower.JetOff))]
    internal static class Patch_FlameThrower_JetOff_Reset
    {
        [HarmonyPostfix]
        private static void Postfix(BasicRangedWeapon_FlameThrower __instance) => LiquidWeaponState.Reset(__instance);
    }

    /// <summary>
    /// 同步调整玩家侧火焰喷射器/高压喷水枪每次 CreateFlame 的落点与沿途判定：
    /// - 火焰喷射器（468）开启路径穿透时，在 Prefix 中把被前排丧尸提前截断的 endPoint 还原至“鼠标/最大射程或首个实心墙体”，
    ///   防止贴脸丧尸阻挡火舌并触发近身 AOE 烧伤自己；
    /// - 沿整个喷射走廊执行穿透判定，并按原版 BasicExplosion 的真实生效判据（角色根节点到落点距离 < explodeRange）精确去重：
    ///   既避免普通目标叠出双倍伤害，又修复大型多部位 Boss（如变异巨蜥）命中头尾时因根节点距落点过远而偶发 0 伤害的问题；
    /// - 高压喷水枪（469）默认保留撞击首目标落地的原版手感。
    /// </summary>
    [HarmonyPatch(typeof(BasicRangedWeapon_FlameThrower), nameof(BasicRangedWeapon_FlameThrower.CreateFlame),
        typeof(Vector2), typeof(Vector2))]
    internal static class Patch_FlameThrower_CreateFlame_Damage
    {
        private static readonly HashSet<IntPtr> PathHitDedup = new HashSet<IntPtr>();
        private static readonly HashSet<IntPtr> KnockbackDedup = new HashSet<IntPtr>();
        private static readonly HashSet<IntPtr> ExtinguishCharDedup = new HashSet<IntPtr>();
        private static readonly HashSet<IntPtr> ExtinguishObjDedup = new HashSet<IntPtr>();
        private static readonly HashSet<long> ExtinguishTileDedup = new HashSet<long>();

        [HarmonyPrefix]
        private static void Prefix(
            BasicRangedWeapon_FlameThrower __instance,
            Vector2 startPoint,
            ref Vector2 endPoint,
            out int __state)
        {
            __state = -1;
            if (__instance == null || !SpecialWeaponHelper.IsPlayerSideHumanWeapon(__instance))
                return;
            if (!LiquidWeaponState.TryGetTargetDps(__instance, out _, out bool isFlame))
                return;

            // 火焰喷射器启用穿透时，忽略沿途角色遮挡，仅在实心墙体/硬掩体处截断落点
            if (isFlame && Plugin.FlamethrowerPathPenetration != null && Plugin.FlamethrowerPathPenetration.Value)
            {
                try
                {
                    Vector2 aim = __instance.GetAimingPoint();
                    if (aim.sqrMagnitude > 0.0001f)
                    {
                        Vector2 aimDelta = aim - startPoint;
                        float aimDist = aimDelta.magnitude;
                        float maxRange = __instance.fireRange > 0.5f ? __instance.fireRange : 10f;
                        float targetDist = Mathf.Min(aimDist, maxRange);

                        if (targetDist > 0.15f)
                        {
                            Vector2 dir = aimDelta / aimDist;
                            int wallOnlyMask = GameConfigs.bulletHitLayermask.value & ~SpecialWeaponHelper.GetAllCharactersLayerMask();
                            if (wallOnlyMask == 0)
                                wallOnlyMask = GameConfigs.explodeOccLayerMask.value;

                            Vector2 resolvedEnd = startPoint + dir * targetDist;
                            if (wallOnlyMask != 0)
                            {
                                RaycastHit2D wallHit = Physics2D.Raycast(startPoint, dir, targetDist, wallOnlyMask);
                                if (wallHit.collider != null && wallHit.distance < targetDist)
                                    resolvedEnd = wallHit.point;
                            }

                            endPoint = resolvedEnd;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Plugin.LogOnce("flame_pierce_endpoint", ex);
                }
            }

            var parent = GameController.instance?.bulletParent;
            if (parent != null)
                __state = parent.childCount;
        }

        [HarmonyPostfix]
        private static void Postfix(
            BasicRangedWeapon_FlameThrower __instance,
            Vector2 startPoint,
            Vector2 endPoint,
            int __state)
        {
            if (__state < 0 || __instance == null) return;
            if (!LiquidWeaponState.TryGetTargetDps(__instance, out float targetDps, out bool isFlame)) return;

            try
            {
                var parent = GameController.instance?.bulletParent;
                if (parent == null || parent.childCount <= __state) return;

                var spawned = parent.GetChild(parent.childCount - 1);
                if (spawned == null) return;

                var vomit = spawned.GetComponent<TentacleController_Vomit>();
                if (vomit == null) return;

                // 仅调整属于本次发射角色的喷射流
                if (vomit.scoreAttacker != null &&
                    SpecialWeaponHelper.Ptr(vomit.scoreAttacker) != SpecialWeaponHelper.Ptr(__instance.character))
                    return;

                var explosionPrefab = vomit.vomitExplosionEffectPrefab;
                if (explosionPrefab == null) return;

                var explosion = explosionPrefab.GetComponent<BasicExplosion>();
                if (explosion == null || explosion.explodeDamage <= 0.001f) return;

                float fallback = __instance.fireInterval > 0.005f
                    ? __instance.fireInterval
                    : SpecialWeaponHelper.DefaultLiquidInterval;
                float dt = LiquidWeaponState.ConsumeElapsedSeconds(LiquidWeaponState.LastJetTime, __instance, fallback);

                // 继承角色通用枪伤增益与武器耐久系数
                float damageScale = 1.0f;
                var attr = __instance.weaponAttr;
                if (attr != null && attr.damage > 0.01f)
                    damageScale = Mathf.Max(0.1f, __instance.damage / attr.damage);

                float enemyScale = BasicCharacterController.GetEnemyDamageFloat(__instance.character);
                float desiredJetDamage = Mathf.Max(0f, targetDps * dt * damageScale * enemyScale);
                vomit.damageBalanceFloat = desiredJetDamage / explosion.explodeDamage;

                // 对喷射走廊执行沿途穿透与大型 Boss 部位补伤判定
                bool doPath = isFlame
                    ? (Plugin.FlamethrowerPathPenetration != null && Plugin.FlamethrowerPathPenetration.Value)
                    : (Plugin.WaterGunPathPenetration != null && Plugin.WaterGunPathPenetration.Value);

                if (doPath)
                    ApplyPathPenetration(__instance, startPoint, endPoint, explosion, desiredJetDamage, dt, isFlame);

                if (!isFlame)
                {
                    if (Plugin.WaterGunKnockbackStrength != null && Plugin.WaterGunKnockbackStrength.Value > 0.01f)
                        ApplyWaterGunKnockback(__instance, startPoint, endPoint, explosion, dt);

                    if (Plugin.WaterGunExtinguishFire != null && Plugin.WaterGunExtinguishFire.Value)
                        ApplyWaterGunExtinguish(__instance, startPoint, endPoint);
                }
            }
            catch (Exception ex)
            {
                Plugin.LogOnce("liquid_create_damage", ex);
            }
        }

        private static int ResolveWallMask()
        {
            int mask = GameConfigs.explodeOccLayerMask.value;
            if (mask == 0)
                mask = GameConfigs.bulletHitLayermask.value & ~SpecialWeaponHelper.GetAllCharactersLayerMask();
            return mask;
        }

        /// <summary>
        /// 将偏前偏高的 gunPoint 起点向角色脚底原点回退投影（若无墙体阻挡），消除贴脸敌人的近身判定盲区。
        /// </summary>
        private static Vector2 ResolveSweepOrigin(BasicCharacterController attacker, Vector2 startPoint, int occMask)
        {
            if (attacker == null)
                return startPoint;

            float standH = attacker.standVisualHeight;
            Vector2 groundMuzzle = standH > 0.001f ? new Vector2(startPoint.x, startPoint.y - standH) : startPoint;
            Vector3 p3 = attacker.transform.position;
            Vector2 bodyOrigin = new Vector2(p3.x, p3.y);

            if ((groundMuzzle - bodyOrigin).sqrMagnitude < 0.0025f)
                return bodyOrigin;

            if (occMask != 0 && Physics2D.Linecast(bodyOrigin, groundMuzzle, occMask).collider != null)
                return groundMuzzle;

            return bodyOrigin;
        }

        private static bool HasClearLineOfSight(Vector2 origin, Vector2 targetPos, int occMask)
        {
            if (occMask == 0) return true;
            return Physics2D.Linecast(origin, targetPos, occMask).collider == null;
        }

        private static void ApplyPathPenetration(
            BasicRangedWeapon_FlameThrower weapon,
            Vector2 startPoint,
            Vector2 endPoint,
            BasicExplosion explosion,
            float jetDamage,
            float dt,
            bool isFlame)
        {
            var attacker = weapon.character;
            int occMask = ResolveWallMask();
            Vector2 sweepOrigin = ResolveSweepOrigin(attacker, startPoint, occMask);
            Vector2 bodyOrigin = attacker != null
                ? new Vector2(attacker.transform.position.x, attacker.transform.position.y)
                : sweepOrigin;

            Vector2 seg = endPoint - sweepOrigin;
            float segLen = seg.magnitude;
            if (segLen < 0.15f)
                return;

            float splashRadius = explosion.explodeRange > 0.25f ? explosion.explodeRange : 1.6f;
            Vector2 dir = seg / segLen;
            float maxPathDist = segLen;

            // 沿途遇到实心障碍/墙体时截断穿透，不隔墙喷烧
            if (occMask != 0)
            {
                RaycastHit2D wallHit = Physics2D.Raycast(sweepOrigin, dir, maxPathDist, occMask);
                if (wallHit.collider != null && wallHit.distance < maxPathDist)
                    maxPathDist = wallHit.distance;
            }

            if (maxPathDist < 0.15f)
                return;

            float pathRadius = Plugin.LiquidPathHitRadius != null
                ? Mathf.Clamp(Plugin.LiquidPathHitRadius.Value, 0.3f, 2.0f)
                : 0.85f;

            int charMask = SpecialWeaponHelper.GetAllCharactersLayerMask();
            var hits = Physics2D.CircleCastAll(sweepOrigin, pathRadius, dir, maxPathDist, charMask);
            var closeCols = Physics2D.OverlapCircleAll(sweepOrigin + dir * 0.45f, Mathf.Max(pathRadius, 1.15f), charMask);

            bool hasSweepHits = hits != null && hits.Length > 0;
            bool hasCloseCols = closeCols != null && closeCols.Length > 0;
            if (!hasSweepHits && !hasCloseCols)
                return;

            IntPtr attackerPtr = SpecialWeaponHelper.Ptr(attacker);
            var gd = GameController.instance?.gameData;
            float burnChance = isFlame
                ? SpecialWeaponHelper.ScalePerFrameChanceToInterval(weapon.burnRate > 0f ? weapon.burnRate : 0.12f, dt)
                : 0f;
            float burnEndTime = (gd != null && weapon.burnTime > 0f) ? gd.inGameTime + weapon.burnTime : 0f;

            float pushMag = isFlame
                ? Mathf.Clamp(explosion.explositionForce * 0.08f, 2f, 12f)
                : Mathf.Clamp(explosion.explositionForce * 0.22f, 10f, 45f);
            Vector2 pushForce = dir * pushMag;

            // 与原版 BasicExplosion 判定保持一致：只有角色根节点处于落点爆炸圈内时，落点爆炸才会出伤
            float explosionRootSafeRadius = splashRadius * 0.92f;

            PathHitDedup.Clear();
            var scope = CharacterHitContext.AttackerScope(attacker);
            try
            {
                void ProcessCandidate(Collider2D col, Vector2 hitPoint)
                {
                    if (col == null) return;

                    var victim = BasicCharacterController.GetCharacterFromCollider(col);
                    if (victim == null || victim.deadFlag) return;

                    IntPtr vPtr = SpecialWeaponHelper.Ptr(victim);
                    if (vPtr == IntPtr.Zero || vPtr == attackerPtr || !PathHitDedup.Add(vPtr))
                        return;

                    // 依据原版 BasicExplosion 实际测距基准（transform.position）去重：
                    // 普通目标在落点圈内由落点爆炸结算；大型多部位 Boss 若根节点距落点 >= explodeRange 则由本路径精准出伤
                    Vector3 root3 = victim.transform.position;
                    Vector2 rootPos = new Vector2(root3.x, root3.y);
                    if (Vector2.Distance(rootPos, endPoint) < explosionRootSafeRadius)
                        return;

                    // 高压喷水枪沿途为所有经过的角色熄灭火焰并施加潮湿（含友方）
                    if (!isFlame)
                    {
                        victim.RemoveBuff(1);
                        if (gd != null)
                            victim.AddBuff(8, gd.inGameTime + 6f);
                    }

                    if (!BeamWeaponState.CanDamageTarget(attacker, victim))
                        return;

                    if (jetDamage > 0.01f)
                    {
                        Vector2 resolvedHit = hitPoint;
                        if (resolvedHit.sqrMagnitude < 0.0001f)
                        {
                            Vector3 t3 = victim.GetTorsePositionOrRoot();
                            resolvedHit = new Vector2(t3.x, t3.y);
                        }
                        var bodyPart = col.GetComponent<CharacterBodyPart>();
                        victim.OnHit(jetDamage, resolvedHit, pushForce, bodyPart, 1f, true, true, true, true);
                        HordeScoreTracker.NoteDamage(victim, attacker, jetDamage);
                    }

                    if (isFlame && !victim.deadFlag && gd != null && burnEndTime > 0f && UnityEngine.Random.value < burnChance)
                        victim.AddBuff(1, burnEndTime);
                }

                if (hasSweepHits)
                {
                    for (int i = 0; i < hits.Length; i++)
                        ProcessCandidate(hits[i].collider, hits[i].point);
                }

                if (hasCloseCols)
                {
                    for (int i = 0; i < closeCols.Length; i++)
                    {
                        var col = closeCols[i];
                        if (col == null) continue;
                        Bounds cb = col.bounds;
                        Vector2 colPos = new Vector2(cb.center.x, cb.center.y);
                        if (Vector2.Dot(colPos - bodyOrigin, dir) < -0.35f)
                            continue;
                        if (!HasClearLineOfSight(bodyOrigin, colPos, occMask))
                            continue;

                        ProcessCandidate(col, colPos);
                    }
                }
            }
            finally
            {
                scope.Dispose();
                PathHitDedup.Clear();
            }
        }

        private static void ApplyWaterGunKnockback(
            BasicRangedWeapon_FlameThrower weapon,
            Vector2 startPoint,
            Vector2 endPoint,
            BasicExplosion explosion,
            float dt)
        {
            var attacker = weapon.character;
            int occMask = ResolveWallMask();
            Vector2 sweepOrigin = ResolveSweepOrigin(attacker, startPoint, occMask);
            Vector2 seg = endPoint - sweepOrigin;
            float segLen = seg.magnitude;
            if (segLen < 0.15f)
                return;

            Vector2 sweepDir = seg / segLen;
            float maxPathDist = segLen;
            if (occMask != 0)
            {
                RaycastHit2D wallHit = Physics2D.Raycast(sweepOrigin, sweepDir, maxPathDist, occMask);
                if (wallHit.collider != null && wallHit.distance < maxPathDist)
                    maxPathDist = wallHit.distance;
            }

            float pathRadius = Plugin.LiquidPathHitRadius != null
                ? Mathf.Clamp(Plugin.LiquidPathHitRadius.Value, 0.3f, 2.0f)
                : 0.85f;
            float splashRadius = Mathf.Max(explosion != null && explosion.explodeRange > 0.25f ? explosion.explodeRange : 1.6f, 1.9f);
            int charMask = SpecialWeaponHelper.GetAllCharactersLayerMask();

            var streamHits = Physics2D.CircleCastAll(sweepOrigin, Mathf.Max(pathRadius, 0.9f), sweepDir, maxPathDist, charMask);
            var endCols = Physics2D.OverlapCircleAll(endPoint, splashRadius, charMask);

            IntPtr attackerPtr = SpecialWeaponHelper.Ptr(attacker);
            float refMass = (attacker?.m_rigidbody != null && attacker.m_rigidbody.mass > 0.01f) ? attacker.m_rigidbody.mass : 1f;
            float strength = Mathf.Clamp(Plugin.WaterGunKnockbackStrength.Value, 0f, 3f);

            KnockbackDedup.Clear();
            try
            {
                void PushCollider(Collider2D col)
                {
                    if (col == null) return;
                    var victim = BasicCharacterController.GetCharacterFromCollider(col);
                    if (victim == null || victim.deadFlag) return;

                    IntPtr vPtr = SpecialWeaponHelper.Ptr(victim);
                    if (vPtr == IntPtr.Zero || vPtr == attackerPtr || !KnockbackDedup.Add(vPtr))
                        return;
                    if (!BeamWeaponState.CanDamageTarget(attacker, victim))
                        return;

                    Vector3 vp3 = victim.transform.position;
                    Vector2 vPos = new Vector2(vp3.x, vp3.y);
                    if (!HasClearLineOfSight(sweepOrigin, vPos, occMask))
                        return;

                    float victimMass = (victim.m_rigidbody != null && victim.m_rigidbody.mass > 0.01f) ? victim.m_rigidbody.mass : refMass;
                    float massRatio = Mathf.Clamp(refMass / victimMass, 0.12f, 1.4f);

                    float pushAccel = 22f * strength * massRatio;
                    Vector2 curExtra = victim.extraVelocity;
                    Vector2 nextExtra = curExtra + sweepDir * (pushAccel * dt);
                    float maxSpeed = Mathf.Max(curExtra.magnitude, 5.5f * Mathf.Clamp(strength, 0.5f, 2.0f) * Mathf.Sqrt(massRatio));
                    float nextMag = nextExtra.magnitude;
                    if (nextMag > maxSpeed && nextMag > 0.001f)
                        nextExtra = (nextExtra / nextMag) * maxSpeed;

                    victim.extraVelocity = nextExtra;
                }

                if (streamHits != null)
                {
                    for (int i = 0; i < streamHits.Length; i++)
                        PushCollider(streamHits[i].collider);
                }

                if (endCols != null)
                {
                    for (int i = 0; i < endCols.Length; i++)
                        PushCollider(endCols[i]);
                }
            }
            finally
            {
                KnockbackDedup.Clear();
            }
        }

        private static void ApplyWaterGunExtinguish(
            BasicRangedWeapon_FlameThrower weapon,
            Vector2 startPoint,
            Vector2 endPoint)
        {
            IntPtr weaponPtr = SpecialWeaponHelper.Ptr(weapon);
            float now = Time.time;
            if (LiquidWeaponState.LastExtinguishTime.TryGetValue(weaponPtr, out float prevTime) &&
                now - prevTime < 0.08f)
                return;

            if (LiquidWeaponState.LastExtinguishTime.Count > 128)
                LiquidWeaponState.LastExtinguishTime.Clear();
            LiquidWeaponState.LastExtinguishTime[weaponPtr] = now;

            var attacker = weapon.character;
            int occMask = ResolveWallMask();
            Vector2 sweepOrigin = ResolveSweepOrigin(attacker, startPoint, occMask);
            Vector2 bodyOrigin = attacker != null
                ? new Vector2(attacker.transform.position.x, attacker.transform.position.y)
                : sweepOrigin;

            Vector2 seg = endPoint - sweepOrigin;
            float segLen = seg.magnitude;
            Vector2 dir = segLen > 0.05f ? seg / segLen : Vector2.right;
            float maxPathDist = segLen;
            if (segLen > 0.15f && occMask != 0)
            {
                RaycastHit2D wallHit = Physics2D.Raycast(sweepOrigin, dir, maxPathDist, occMask);
                if (wallHit.collider != null && wallHit.distance < maxPathDist)
                    maxPathDist = wallHit.distance;
            }

            float extRadius = Plugin.WaterGunExtinguishRadius != null
                ? Mathf.Clamp(Plugin.WaterGunExtinguishRadius.Value, 0.8f, 5.0f)
                : 2.2f;

            var gd = GameController.instance?.gameData;
            float wetEndTime = gd != null ? gd.inGameTime + 6f : 0f;
            bool extinguishedAny = false;

            ExtinguishCharDedup.Clear();
            ExtinguishObjDedup.Clear();
            ExtinguishTileDedup.Clear();

            try
            {
                // 1. 熄灭角色身上的火焰 Buff（含友方、宠物、僵尸，以及朝自身脚下 1.8m 内喷水时的自己）
                int charMask = SpecialWeaponHelper.GetAllCharactersLayerMask();
                var charCols = Physics2D.OverlapCircleAll(endPoint, extRadius, charMask);
                var streamHits = maxPathDist > 0.15f
                    ? Physics2D.CircleCastAll(sweepOrigin, Mathf.Min(extRadius * 0.6f, 1.1f), dir, maxPathDist, charMask)
                    : null;

                void ExtinguishCharacter(BasicCharacterController ch)
                {
                    if (ch == null) return;
                    IntPtr cPtr = SpecialWeaponHelper.Ptr(ch);
                    if (cPtr == IntPtr.Zero || !ExtinguishCharDedup.Add(cPtr))
                        return;

                    ch.RemoveBuff(1);
                    if (gd != null)
                        ch.AddBuff(8, wetEndTime);
                }

                if (charCols != null)
                {
                    for (int i = 0; i < charCols.Length; i++)
                        ExtinguishCharacter(BasicCharacterController.GetCharacterFromCollider(charCols[i]));
                }

                if (streamHits != null)
                {
                    for (int i = 0; i < streamHits.Length; i++)
                        ExtinguishCharacter(BasicCharacterController.GetCharacterFromCollider(streamHits[i].collider));
                }

                if (attacker != null && Vector2.Distance(bodyOrigin, endPoint) <= 1.8f)
                    ExtinguishCharacter(attacker);

                // 2. & 4. 熄灭地面火（ChunkData TileState.Fire）与体素火（VoxelFireSystem），并留下短暂水渍抑制复燃
                var mapCtrl = MapController.instance;
                var gc = GameController.instance;
                if (mapCtrl != null)
                {
                    sbyte zLevel = mapCtrl.currentZLevel;

                    void ProcessTile(int tx, int ty)
                    {
                        long key = ((long)tx << 32) | (uint)ty;
                        if (!ExtinguishTileDedup.Add(key))
                            return;

                        var coord = new Vector2Int(tx, ty);
                        bool hadFire = false;

                        Vector2Int chunkCoord = mapCtrl.GetChunkCoordinateByPosition(new Vector2(tx + 0.5f, ty + 0.5f));
                        BasicChunk chunk = mapCtrl.GetChunkByCoordinate(chunkCoord);
                        ChunkData chunkData = chunk?.chunkData;

                        if (chunkData != null)
                        {
                            M_TileStatusData status = chunkData.GetTileStatus(coord) ?? chunkData.FindTileStatusData(coord, zLevel);
                            var stateList = status?.tileStateDataList;
                            if (stateList != null)
                            {
                                for (int s = 0; s < stateList.Count; s++)
                                {
                                    var sd = stateList[s];
                                    if (sd != null && sd.tileState == TileState.Fire)
                                    {
                                        chunkData.RemoveTileState(coord, TileState.Fire, "WaterGun");
                                        hadFire = true;
                                        extinguishedAny = true;
                                        break;
                                    }
                                }
                            }
                        }

                        if (VoxelFireSystem.TileHasLiveFire(coord))
                        {
                            var flames = VoxelFireSystem.s_flames;
                            if (flames != null && flames.TryGetValue(coord, out var topFlame))
                            {
                                if (topFlame != null)
                                    VoxelFireSystem.ExtinguishFlame(topFlame);
                                flames.Remove(coord);
                            }
                            chunkData?.RemoveTileState(coord, TileState.Fire, "WaterGun_Voxel");
                            hadFire = true;
                            extinguishedAny = true;
                        }

                        if (hadFire && gc != null)
                            gc.AddTileState(coord, TileState.Water, VoxelFireSystem.SecondsToDays(15f), zLevel);
                    }

                    // 沿水柱每 0.9m 采样格子
                    int steps = Mathf.Max(1, Mathf.CeilToInt(maxPathDist / 0.9f));
                    for (int s = 0; s <= steps; s++)
                    {
                        Vector2 p = sweepOrigin + dir * (maxPathDist * (s / (float)steps));
                        ProcessTile(Mathf.FloorToInt(p.x), Mathf.FloorToInt(p.y));
                    }

                    // 覆盖落点灭火半径圆内所有整格
                    int minX = Mathf.FloorToInt(endPoint.x - extRadius);
                    int maxX = Mathf.FloorToInt(endPoint.x + extRadius);
                    int minY = Mathf.FloorToInt(endPoint.y - extRadius);
                    int maxY = Mathf.FloorToInt(endPoint.y + extRadius);
                    float extRadiusSq = extRadius * extRadius;
                    for (int tx = minX; tx <= maxX; tx++)
                    {
                        for (int ty = minY; ty <= maxY; ty++)
                        {
                            float dx = (tx + 0.5f) - endPoint.x;
                            float dy = (ty + 0.5f) - endPoint.y;
                            if (dx * dx + dy * dy <= extRadiusSq)
                                ProcessTile(tx, ty);
                        }
                    }
                }

                // 3. 熄灭燃烧地形物体与燃烧尸体（TerrainObject / TerrainObject_Corpse）
                int terrainMask = GameConfigs.terrainObjectLayerMask.value;
                if (terrainMask != 0)
                {
                    void CheckTerrainAt(Vector2 center, float r)
                    {
                        var cols = Physics2D.OverlapCircleAll(center, r, terrainMask);
                        if (cols == null) return;

                        for (int i = 0; i < cols.Length; i++)
                        {
                            var col = cols[i];
                            if (col == null) continue;

                            var corpse = col.GetComponentInParent<TerrainObject_Corpse>();
                            if (corpse != null)
                            {
                                IntPtr cPtr = SpecialWeaponHelper.Ptr(corpse);
                                if (cPtr != IntPtr.Zero && ExtinguishObjDedup.Add(cPtr))
                                {
                                    var cData = corpse.objectData;
                                    bool hadCorpseFire = false;
                                    if (cData != null)
                                    {
                                        if (cData.fireBurningProcess > 0f)
                                        {
                                            cData.fireBurningProcess = 0f;
                                            hadCorpseFire = true;
                                        }
                                        if (RemoveFireStateFromObjectData(cData))
                                            hadCorpseFire = true;
                                    }
                                    corpse.ExtinguishCorpse();
                                    if (hadCorpseFire) extinguishedAny = true;
                                }
                                continue;
                            }

                            var obj = col.GetComponentInParent<TerrainObject>();
                            if (obj != null)
                            {
                                IntPtr oPtr = SpecialWeaponHelper.Ptr(obj);
                                if (oPtr != IntPtr.Zero && ExtinguishObjDedup.Add(oPtr))
                                {
                                    var oData = obj.objectData;
                                    bool hadObjFire = false;
                                    if (oData != null)
                                    {
                                        if (oData.fireBurningProcess > 0f)
                                        {
                                            oData.fireBurningProcess = 0f;
                                            hadObjFire = true;
                                        }
                                        if (RemoveFireStateFromObjectData(oData))
                                            hadObjFire = true;
                                    }
                                    if (hadObjFire)
                                    {
                                        obj.RemoveTileStateEffect(TileState.Fire);
                                        extinguishedAny = true;
                                    }
                                }
                            }
                        }
                    }

                    CheckTerrainAt(endPoint, extRadius);
                    if (maxPathDist > 2.5f)
                        CheckTerrainAt(sweepOrigin + dir * (maxPathDist * 0.5f), Mathf.Min(extRadius, 1.4f));
                }

                // 灭火成功时播放柔和的扑灭火声（0.35s 冷却防刷屏）
                if (extinguishedAny)
                {
                    bool canPlay = !LiquidWeaponState.LastExtinguishSoundTime.TryGetValue(weaponPtr, out float lastSnd) ||
                                   (now - lastSnd >= 0.35f);
                    if (canPlay)
                    {
                        var ac = AudioController.instance;
                        var clip = ac?.putoutFireClip;
                        if (ac != null && clip != null)
                        {
                            ac.PlayAudioclip3D(clip, new Vector3(endPoint.x, endPoint.y, 0f), 0f, 0.7f);
                            LiquidWeaponState.LastExtinguishSoundTime[weaponPtr] = now;
                        }
                    }
                }
            }
            finally
            {
                ExtinguishCharDedup.Clear();
                ExtinguishObjDedup.Clear();
                ExtinguishTileDedup.Clear();
            }
        }

        private static bool RemoveFireStateFromObjectData(TerrainObjectData data)
        {
            var list = data?.objectStateDataList;
            if (list == null || list.Count == 0) return false;

            bool removed = false;
            for (int i = list.Count - 1; i >= 0; i--)
            {
                var st = list[i];
                if (st != null && st.tileState == TileState.Fire)
                {
                    list.RemoveAt(i);
                    removed = true;
                }
            }
            return removed;
        }
    }
}
