using System;
using System.Collections.Generic;
using Durango.Network;
using Durango.Utils;
using Messages;
using DurangoServer.Modding;
using Shared.Battle;

namespace DurangoServer.Core;

/// <summary>
/// เฟส C รอบ 2 — ระบบต่อสู้ฝั่งผู้เล่น
///
/// ลำดับตามที่ client คาดหวัง (ไล่จาก offline server เดิมใน client/Durango.Offline/Player.cs):
///   client → <c>UseBattleAction</c> {ActionId, StartAt, TargetEntityId}
///   server → <c>BattleBegun</c> (ครั้งแรกที่เริ่มตีเป้าใหม่) แล้วหน่วงตาม <c>attack_time</c> ของท่า
///   server → <c>Damaged</c> {AttackerId, VictimId, Damage, EventAt} broadcast ให้ทุกคน
///   ถ้าเลือดหมด → <c>EntityDied</c> broadcast
///
/// ดาเมจคิดที่ server ทั้งหมด — client บอกได้แค่ "จะใช้ท่าอะไรกับใคร"
/// ดูรายละเอียดที่ docs/server/Combat.md
/// </summary>
public partial class ServerPlayer
{
    /// <summary>สตามินาขั้นต่ำต่อการโจมตี 1 ครั้ง (ท่าพื้นฐานในข้อมูลเกมเป็น 0 ซึ่งทำให้ตีรัวได้ไม่จำกัด)</summary>
    private const float StaminaCostAttackMin = 3f;

    /// <summary>เผื่อระยะจาก use_range ของท่า เพราะตำแหน่งฝั่ง server คือปลายทางของ Move ล่าสุด</summary>
    private const float AttackRangeSlack = 400f;

    // 🐛 พลังโจมตี/โอกาสคริ เคยเป็น const ตรงนี้ — ย้ายไป `Combat` ใน config.json แล้ว
    //    และที่สำคัญกว่า: อาวุธเคยบวก **+10 เท่ากันหมดทุกชิ้น** (ขวานหิน = ค้อนเหล็ก)
    //    ตอนนี้อ่านค่า attack จริงรายชิ้นจากข้อมูลเกม — ดู ServerPlayer.Abilities.AttackPower()

    /// <summary>คูลดาวน์ต่อท่า (เวลาที่ใช้ได้อีกครั้ง)</summary>
    private readonly Dictionary<string, double> _actionReadyAt = new Dictionary<string, double>();

    /// <summary>เป้าหมายที่กำลังตีอยู่ — ใช้ตัดสินว่าต้องส่ง BattleBegun ไหม</summary>
    private string _battleTarget;

    /// <summary>[บั๊ก #4] หลบได้ถึงเวลานี้ (unix) — ตั้งตอนใช้ท่า dodge</summary>
    private double _dodgeUntil;

    /// <summary>ช่วงเวลาที่หลบพ้น (วินาที) — สั้นพอให้ต้องกดจังหวะ ไม่ใช่กดค้างอมตะ</summary>
    private const double DodgeWindowSeconds = 0.9;

    /// <summary>กำลังอยู่ในจังหวะหลบไหม — สัตว์เช็คก่อนคิดดาเมจ</summary>
    public bool IsDodging => Times.UnixTimeNow() < _dodgeUntil;

    private static readonly Random _combatRng = new Random();

    /// <summary>ตายอยู่หรือเปล่า (ตั้งตอนเลือดหมด ล้างตอนฟื้น)</summary>
    public bool Dead { get; private set; }

    // ───────────────────────── handler ─────────────────────────

    private void HandleGetActions(GetActions msg, PacketHeader header)
    {
        SendActions(header.Seq);
    }

    private void HandleUseBattleAction(UseBattleAction msg, PacketHeader header)
    {
        if (!ServerConfig.Current.Features.Combat)
        {
            Console.WriteLine("[feature] ปฏิเสธ {0}: ระบบต่อสู้ปิดอยู่ในรอบนี้ (Features.Combat)", Name);
            Send(new Info { Text = "ระบบต่อสู้ยังไม่เปิดในรอบนี้" }, header.Seq);
            Send(Aborts.Reason(), header.Seq);
            return;
        }
        if (Dead)
        {
            Console.WriteLine("[combat] {0} ตายอยู่ ตีไม่ได้", Name);
            Send(Aborts.Reason(), header.Seq);
            return;
        }
        if (!ActionData.TryGet(msg.ActionId, out ActionData.Action action))
        {
            Console.WriteLine("[combat] ปฏิเสธ {0}: ไม่มีท่า '{1}' ในเกม", Name, msg.ActionId);
            Send(Aborts.Reason(), header.Seq);
            return;
        }
        // ท่าต้องเป็นของอาวุธที่ถืออยู่จริง ไม่งั้นถือมือเปล่าก็ใช้ท่าดาบสองมือได้
        if (Array.IndexOf(ActionData.ForWeaponTag(CurrentWeaponTag()), action.Id) < 0)
        {
            Console.WriteLine("[combat] ปฏิเสธ {0}: ท่า {1} ใช้กับอาวุธที่ถืออยู่ ({2}) ไม่ได้", Name, action.Id, CurrentWeaponTag());
            Send(Aborts.Reason(), header.Seq);
            return;
        }
        // [แก้เอง] 25 ส.ค. 2026 — เจ้าของย้ำ 2 รอบ: "ท่าต่อสู้ก็ต้องยึดจากสกิลที่เรียน"
        // เดิมตรวจแค่ tag อาวุธ ⇒ modded client ใช้ท่าพิเศษ (smash/stab/flurry/aimedshot ฯลฯ)
        // ได้ทุกอย่างโดยไม่เรียนสกิลเลย ตอนนี้เช็ค `UnlockedActions()` เหมือน `UnlockedRecipes`
        // ท่าพื้นฐาน (default/dodge — auto-grant จาก `EnsureAutomaticSkills`) ผ่านเสมอ
        if (!IsActionUnlocked(action.Id))
        {
            Console.WriteLine("[combat] ปฏิเสธ {0}: ยังไม่ได้เรียนสกิลที่ปลดล็อกท่า {1}", Name, action.Id);
            Send(new Info { Text = "ต้องเรียนสกิลก่อนจึงจะใช้ท่านี้ได้" }, header.Seq);
            Send(Aborts.Reason(), header.Seq);
            return;
        }

        double now = Times.UnixTimeNow();

        // [4 ก.ย. 2026] บั๊ก #4 "กดหลบ ยังโดนโจมตี 100%"
        // เดิมท่า dodge มีแค่ในตารางท่า (ActionData) ไม่มีผลอะไรเลย — กดแล้วได้แค่แต้มสกิลป้องกัน
        // ตอนนี้เปิด "หน้าต่างหลบ" ให้ช่วงสั้น ๆ · สัตว์ที่ตีมาระหว่างนี้จะพลาด (DamageResult.Dodged)
        if (action.Id.IndexOf("dodge", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            _dodgeUntil = now + DodgeWindowSeconds;
        }

        if (_actionReadyAt.TryGetValue(action.Id, out double readyAt) && now < readyAt)
        {
            Console.WriteLine("[combat] ปฏิเสธ {0}: ท่า {1} ยังคูลดาวน์อีก {2:F1} วิ", Name, action.Id, readyAt - now);
            Send(Aborts.Reason(), header.Seq);
            return;
        }

        if (!TryFindTarget(msg.TargetEntityId, out WorldPosition targetPos, out bool targetIsAnimal))
        {
            Console.WriteLine("[combat] ปฏิเสธ {0}: ไม่มีเป้าหมาย {1} ในโลก", Name, msg.TargetEntityId);
            Send(Aborts.Reason(), header.Seq);
            return;
        }

        WorldPosition me = CurrentPosition;
        float dx = targetPos.x - me.x;
        float dy = targetPos.y - me.y;
        float dist = MathF.Sqrt(dx * dx + dy * dy);
        float maxRange = action.UseRange + action.Radius + AttackRangeSlack;
        if (dist > maxRange)
        {
            Console.WriteLine("[combat] ปฏิเสธ {0}: เป้าหมายไกลไป ({1:F0} > {2:F0})", Name, dist, maxRange);
            Send(Aborts.Reason(), header.Seq);
            return;
        }
        if (!targetIsAnimal)
        {
            ServerPlayer targetPlayer = _world.FindPlayer(msg.TargetEntityId);
            if (!ServerConfig.Current.Features.Pvp)
            {
                RejectFeatureDisabled("Pvp", "UseBattleAction", "PvP ยังไม่เปิดในรอบนี้", header);
                return;
            }
            if (Level < 20 || targetPlayer == null || targetPlayer.Level < 20)
            {
                Send(new Info { Text = "PvP เปิดสำหรับผู้เล่นเลเวล 20 ขึ้นไปเท่านั้น" }, header.Seq);
                Send(Aborts.Reason(), header.Seq);
                return;
            }
        }

        if (_deferred.Count >= MaxPendingActions)
        {
            Console.WriteLine("[combat] ปฏิเสธ {0}: คิวการกระทำเต็ม ({1})", Name, _deferred.Count);
            Send(Aborts.Reason(), header.Seq);
            return;
        }

        IModEventContext? beforeAttack = PluginManager.Instance?.FireEvent("combat.before_attack", this, true, false,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["target_id"] = msg.TargetEntityId ?? "", ["action_id"] = action.Id.ToString() });
        if (beforeAttack?.IsCancelled == true)
        { Send(new Info { Text = beforeAttack.CancelReason ?? "การโจมตีถูกยกเลิกโดยม็อด" }, header.Seq); Send(Aborts.Reason(), header.Seq); return; }

        float staminaCost = Math.Max(action.Stamina, StaminaCostAttackMin);
        if (!TrySpendStamina(staminaCost, ActionKind.Combat))
        {
            Console.WriteLine("[combat] {0} สตามินาไม่พอ ({1:F0})", Name, staminaCost);
            Send(Aborts.Reason(), header.Seq);
            return;
        }

        bool rangedAttack = IsRangedWeapon();
        if (rangedAttack && !TryConsumeArrow(header.Seq))
        {
            // The stamina gauge was already settled, refund this rejected shot.
            RestoreStamina(staminaCost, 0f);
            return;
        }

        _actionReadyAt[action.Id] = now + Math.Max(action.Cooltime, action.AttackTime);

        // client จะเข้าโหมดต่อสู้เมื่อได้ BattleBegun — ส่งครั้งแรกที่เปลี่ยนเป้า (อ้างอิงจาก offline server เดิม)
        if (_battleTarget != msg.TargetEntityId)
        {
            _battleTarget = msg.TargetEntityId;
            Send(new BattleBegun
            {
                EntityId = EntityId,
                EventAt = now,
                EnemyId = msg.TargetEntityId,
                StartDamaged = false
            });
        }

        Send(default(OK), header.Seq);

        double hitAt = now + action.AttackTime;
        PluginManager.Instance?.FireEvent("combat.attack", this, false, true);
        string victimId = msg.TargetEntityId;
        _deferred.Add((hitAt, () => ResolveHit(victimId, targetIsAnimal, action, hitAt, rangedAttack)));
    }

    /// <summary>ถึงเวลาที่ดาเมจเข้าจริง — คิดตัวเลขแล้ว broadcast ให้ทุกคนเห็นพร้อมกัน</summary>
    private void ResolveHit(string victimId, bool victimIsAnimal, ActionData.Action action, double eventAt, bool rangedAttack)
    {
        float damage = RollDamage(action, rangedAttack, out bool crit);
        // [TodoList/05] เกราะสัตว์รายชนิดตามข้อมูลเกม — สูตรเดียวกับเกราะผู้เล่น (ArmorScaleFor)
        if (victimIsAnimal && _world.Animals.TryGet(victimId, out ServerAnimal armored))
        {
            float def = SpawnTable.DefenseFor(armored.EntityType, armored.Level);
            if (def > 0f)
            {
                damage = Math.Max(1f, damage * ArmorScaleFor(def));
            }
        }

        var dmg = new Damage
        {
            Result = DamageResult.Hit,
            Value = (int)MathF.Round(damage),
            Part = BodyPart.Body,
            Direction = DamageDirection.Front,
            AttackType = CurrentAttackType(),
            Effects = crit ? DamageEffects.Critical : DamageEffects.None
        };

        IModEventContext? beforeDamage = PluginManager.Instance?.FireEvent("combat.before_damage", this, true, false,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["attacker_id"] = EntityId, ["victim_id"] = victimId, ["damage"] = dmg.Value.ToString(), ["critical"] = crit.ToString() });
        if (beforeDamage?.IsCancelled == true)
        { Console.WriteLine("[combat] damage cancelled by mod: " + (beforeDamage.CancelReason ?? "cancelled")); return; }

        _world.BroadcastToViewers(victimId, new Damaged
        {
            AttackerId = EntityId,
            VictimId = victimId,
            Damage = dmg,
            EventAt = eventAt
        });

        if (victimIsAnimal)
        {
            WearCombatEquipment(wearWeapon: true, wearArmor: false);
            // อ่านเลเวลไว้ก่อนตี — ตายแล้วซากอาจถูกลบออกจากโลกก่อนที่จะได้อ่าน
            int victimLevel = _world.Animals.TryGet(victimId, out ServerAnimal prey) ? prey.Level : 1;
            if (_world.Animals.Damage(victimId, damage, EntityId))
            {
                GainExpForKill(victimLevel, rangedAttack
                    ? Shared.Skill.Category.RangedCombat
                    : Shared.Skill.Category.MeleeCombat);
                if (victimId == _battleTarget)
                {
                    EndBattle();   // เป้าตายแล้ว — พาผู้เล่นออกจากโหมดต่อสู้ให้เอง
                }
            }
            return;
        }

        ServerPlayer victim = _world.FindPlayer(victimId);
        if (victim == null)
        {
            return;
        }
        Console.WriteLine("[combat] {0} ตี {1} {2} หน่วย{3}", Name, victim.Name, dmg.Value, crit ? " (คริ!)" : "");
        PluginManager.Instance?.FireEvent("combat.damage", this, false, true,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["attacker_id"] = EntityId,
                ["victim_id"] = victimId,
                ["damage"] = dmg.Value.ToString(),
                ["critical"] = crit.ToString()
            });
        WearCombatEquipment(wearWeapon: true, wearArmor: false);
        victim.WearCombatEquipment(wearWeapon: false, wearArmor: true);
        if (victim.ApplyDamage(damage))
        {
            victim.Die();
        }
    }

    private float RollDamage(ActionData.Action action, bool rangedAttack, out bool crit)
    {
        // มือเปล่า + เลเวล + พลัง(Strength) + ค่า attack จริงของอาวุธชิ้นที่ถืออยู่
        float atk = AttackPower();
        float ratio = action.RatioSum <= 0f ? 1f : action.RatioSum;
        float damage = atk * action.DamageBonus * ratio;
        damage *= 0.85f + (float)_combatRng.NextDouble() * 0.3f;      // ±15%
        crit = _combatRng.NextDouble() < CritChanceValue();
        if (crit)
        {
            damage *= CombatRates.CritMultiplier;
        }
        damage *= rangedAttack ? RangedDamageScale() : MeleeDamageScale();
        return Math.Max(1f, damage);
    }

    // ───────────────────────── ตาย / ฟื้น ─────────────────────────

    /// <summary>
    /// ออกจากโหมดต่อสู้ — ต้องส่ง <c>BattleEnded</c> เท่านั้น client ถึงจะออกให้
    /// (`CombatSystem.OnBattleEnded` เป็นตัวปลดล็อกกล้อง/ปุ่มโจมตี)
    /// </summary>
    public void EndBattle()
    {
        _battleTarget = null;
        Send(new BattleEnded { EntityId = EntityId, EventAt = Times.UnixTimeNow() });
    }

    /// <summary>
    /// H-8: เตะออกจากเกม — เซฟก่อนแล้วค่อยปิดการเชื่อมต่อ
    /// ใช้ตอนมีคนเข้าเกมด้วยตัวละครเดียวกันซ้อนกัน (กันของก๊อป)
    /// </summary>
    public void Kick(string reason)
    {
        try
        {
            Send(new Info { Text = reason });
            Save();
        }
        catch (Exception e)
        {
            Console.WriteLine($"[save] เซฟตอนเตะ {EntityId} ไม่สำเร็จ: {e.Message}");
        }
        _conn.Close();
    }

    /// <summary>เลือดหมด — บอกทุกคนว่าล้มแล้ว จากนั้นทำอะไรไม่ได้จนกว่าจะสั่ง Revive</summary>
    public void Die()
    {
        if (Dead)
        {
            return;
        }
        Dead = true;
        // [แก้เอง] 26 ส.ค. 2026 — ถ้าตายตอนกำลังนั่งพัก ต้องเลิกพักให้เรียบร้อยก่อน
        // ไม่งั้น _resting ค้าง true + บัพ away_from_keyboard ค้าง + หลังฟื้น client ยังแตะ
        // กองไฟเดิมซ้ำ → เริ่มพักเองวน ๆ (server log เห็น "[rest] ... ล้า 0/1/3" หลังฟื้น)
        StopResting();
        // [แก้เอง] 26 ส.ค. 2026 — ตายแล้วหลอดเลือดต้องค้างที่ 0 จริง ไม่ให้ velocity regen
        // ดันกลับขึ้นมาเป็น ~0.5 (ทำให้ IsDead == CurrentLife <= 0 เป็น false ทั้งที่ Dead เป็น true)
        EnsureSurvival();
        double now = Times.UnixTimeNow();
        _life.Settle(now);
        _life.Value = 0f;
        _life.Velocity = 0f;
        RememberDeathPoint();
        ApplyDeathPenalty(now);        // [TodoList/07] นับครั้ง + ของหล่นลงกล่องที่จุดตาย
        WearEquippedOnDeath();
        EndBattle();               // ตายแล้วต้องออกจากโหมดต่อสู้ ไม่งั้น client ค้างในโหมดนั้น
        Console.WriteLine("[combat] ☠ {0} ตายแล้ว", Name);
        _world.BroadcastToViewers(EntityId, new EntityDied { EntityId = EntityId, At = Times.UnixTimeNow() });
        // [แก้เอง] 27 ส.ค. 2026 — V1.1 mod-sdk: แจ้ง mod ที่ลงทะเบียน OnPlayerDied (ท้ายสุดให้ state
        // ภายในตัวละคร set ครบก่อน — Dead=true, เลือด=0, battle จบแล้ว)
        PluginManager.Instance?.FirePlayerDied(this);
    }

    private void HandleRevive(Revive msg, PacketHeader header)
    {
        if (!Dead)
        {
            // ถ้าไม่ตายอยู่แล้ว (เช่น เพิ่ง /heal ไป) ส่ง Revived ปิด UI ให้เลย ไม่ส่ง Abort
            Send(default(Revived), header.Seq);
            return;
        }
        ReviveAtSpawn();
        Send(default(Revived), header.Seq);     // ปิด UI "รอฟื้น" ของคนที่กดปุ่มเอง
        QuestProgress(QuestData.Goal.Revive);
    }

    /// <summary>
    /// ฟื้นที่จุดเกิด — ใช้กับการกดปุ่ม "귀환 후 부활" (Revive at Returning Point)
    /// </summary>
    public void ReviveAtSpawn()
    {
        Dead = false;
        _hasDeathPoint = false;
        MarkDirty();
        RestoreOnRevive();             // [TodoList/07] 60/40/20/10% ตามจำนวนครั้งที่ตายติดกัน

        // แจ้ง EntityRevived ให้ Client ทราบก่อน เพื่อให้ IsAlive = true และยกเลิกสถานะตาย/หน้าจอมืด
        _world.BroadcastToViewers(EntityId, new EntityRevived { EntityId = EntityId, At = Times.UnixTimeNow() });

        WorldPosition spawn = _returningPoint.HasValue
            ? new WorldPosition(_returningPoint.Value.x * 200f, _returningPoint.Value.y * 200f)
            : _world.GetEntryPosition();

        // ใช้ WarpBack แทน Revive เพื่อให้ Client เล่นท่า Warp_End และปิด loading curtain สมบูรณ์
        SendTeleport(spawn, Shared.Teleport.TeleportType.WarpBack);
        RememberPosition(spawn, 0f);
        PushGauges("life", "stamina", "fatigue");
        Console.WriteLine("[combat] {0} ฟื้นที่จุดเกิด ({1},{2})", Name, (int)(spawn.x / 200f), (int)(spawn.y / 200f));
        SendSurvivalPublic();
        Send(BuildPoints());
        PluginManager.Instance?.FireEvent("player.revived", this, false, true);
    }

    /// <summary>
    /// ฟื้นชีพ ณ จุดเดิมทันที (In-place Revive) โดยไม่วาร์ป ไม่เกิด TeleportLoadingCurtain ค้าง
    /// ใช้สำหรับคำสั่ง /heal, /revive, cheat, หรือ admin command
    /// </summary>
    public void ReviveHere(bool fullHeal = true)
    {
        bool wasDead = Dead;
        Dead = false;
        _hasDeathPoint = false;
        MarkDirty();

        if (fullHeal)
        {
            RestoreSurvival(clearFatigue: true);
        }
        else
        {
            RestoreOnRevive();
        }

        WorldPosition pos = CurrentPosition;
        RememberPosition(pos, 0f);

        if (wasDead)
        {
            // 1. แจ้งให้ Client ปลดสถานะตายทันที (IsAlive = true)
            //    สิ่งนี้จะสั่ง UIManager.MessageBox.Hide(), คืนค่าสี NightEffect, ปิดเอฟเฟกต์ตาย
            _world.BroadcastToViewers(EntityId, new EntityRevived { EntityId = EntityId, At = Times.UnixTimeNow() });

            // 2. ส่ง Move เพื่อให้ตัวละครลุกขึ้นยืน (Barehand_Stand) ทันที
            Move standMove = new Move
            {
                EntityId = EntityId,
                Movements = new[]
                {
                    new Movement
                    {
                        MotionName = "Barehand_Stand",
                        MotionOption = 34,
                        PlaybackRate = 1f,
                        RotSpeed = 540f,
                        Path = new[]
                        {
                            new Location
                            {
                                Position = pos,
                                Yaw = 0f,
                                Time = Times.UnixTimeNow(),
                                Floor = 0,
                                Height = 0f
                            }
                        }
                    }
                }
            };
            Send(standMove);
            _world.BroadcastToViewers(EntityId, standMove, except: this);

            // 3. ส่ง Points อัปเดตเพื่อลบจุดหัวกะโหลกบนแผนที่
            Send(BuildPoints());

            Console.WriteLine("[combat] {0} ฟื้นชีพ ณ จุดเดิม ({1},{2})", Name, (int)(pos.x / 200f), (int)(pos.y / 200f));
            PluginManager.Instance?.FireEvent("player.revived", this, false, true);
        }

        PushGauges("life", "stamina", "fatigue");
        SendSurvivalPublic();
    }

    /// <summary>ส่งค่าสถานะให้ตัวเองและคนอื่น (ใช้หลังฟื้น เพื่อให้หลอดเลือดของทุกฝ่ายตรงกัน)</summary>
    private void SendSurvivalPublic()
    {
        Survival survival = new Survival
        {
            EntityId = EntityId,
            Life = BuildLifeGauge()
        };
        _world.BroadcastToViewers(EntityId, survival);
    }

    // ───────────────────────── helper ─────────────────────────

    /// <summary>tag ของอาวุธที่ถืออยู่ (bare_hands ถ้าไม่ได้ถืออะไร)</summary>
    public string CurrentWeaponTag()
    {
        // ดูทั้งช่อง main และ both — อาวุธสองมือ 121 ชิ้นอยู่ช่อง "both"
        if (TryGetWeaponItem(out _, out EquipData.WeaponInfo info) && !string.IsNullOrEmpty(info.Framework))
        {
            string fw = info.Framework.ToLowerInvariant();
            string at = info.AttackType?.ToLowerInvariant() ?? "";

            if (fw == "bow") return "bow";
            if (fw == "crossbow") return "crossbow";
            if (fw == "lance" || at == "spear" || at == "lance") return "lance_twohand";

            if (!string.IsNullOrEmpty(at) && !string.IsNullOrEmpty(fw))
            {
                string combo = $"{at}_{fw}";
                if (ActionData.WeaponActions.ContainsKey(combo))
                {
                    return combo;
                }
            }
            if (fw == "onehand")
            {
                if (at == "axe") return "axe_onehand";
                if (at == "blunt") return "blunt_onehand";
                return "sword_onehand";
            }
            if (fw == "twohand")
            {
                if (at == "axe") return "axe_twohand";
                if (at == "blunt") return "blunt_twohand";
                return "sword_twohand";
            }
            return fw;
        }
        return "bare_hands";
    }

    /// <summary>ส่งรายการท่าต่อสู้ของอาวุธปัจจุบันให้ client (push อัตโนมัติเมื่อใส่/ถอดอาวุธหรือเปลี่ยนสกิล)</summary>
    public void SendActions(uint replySeq = 0)
    {
        string weaponTag = CurrentWeaponTag();
        string[] ids = ActionData.ForWeaponTag(weaponTag);
        HashSet<string> unlocked = UnlockedActions();
        var list = new List<ActionStatus>(ids.Length);
        for (int i = 0; i < ids.Length; i++)
        {
            if (ActionData.TryGet(ids[i], out ActionData.Action a) && unlocked.Contains(a.Id))
            {
                list.Add(new ActionStatus
                {
                    Id = a.Id,
                    Stamina = a.Stamina,
                    Cooltime = a.Cooltime
                });
            }
        }
        Console.WriteLine("[combat] {0} ส่งรายการท่า {1} ท่า (อาวุธ: {2})", Name, list.Count, weaponTag);
        Actions pkt = new Actions { BattleActions = list.ToArray() };
        if (replySeq > 0)
        {
            Send(pkt, replySeq);
        }
        else
        {
            Send(pkt);
        }
    }

    private bool IsRangedWeapon()
    {
        string tag = CurrentWeaponTag();
        return tag == "bow" || tag == "crossbow";
    }

    private bool TryConsumeArrow(uint replyOf)
    {
        Item arrow;
        lock (_inventory)
        {
            int index = _inventory.FindIndex(x => string.Equals(x.Prototype, "gunpowder_arrow", StringComparison.Ordinal));
            if (index < 0)
            {
                Send(new Info { Text = "ไม่มีกระสุน ต้องคราฟต์ลูกธนูก่อน" }, replyOf);
                Send(Aborts.Reason(), replyOf);
                return false;
            }
            arrow = _inventory[index];
            _inventory.RemoveAt(index);
            ForgetInventoryItem(arrow.Id);
        }
        MarkDirty();
        Send(new InventoryUpdated { EntityId = EntityId, RemovedItemIds = new[] { arrow.Id } });
        SendInventory();
        return true;
    }

    private bool HasWeaponEquipped()
    {
        return TryGetWeaponItem(out _, out _);
    }

    /// <summary>
    /// ชนิดการโจมตีที่ client เอาไปเลือกเอฟเฟกต์/เสียงตอนโดน
    ///
    /// 🐛 เดิมเดาจาก **คำขึ้นต้นของ prototype** (axe/sword/lance/hammer/bow)
    /// ⇒ มีดหิน (`blade_*`) หอก (`spear_*`) ธนูหน้าไม้ (`crossbow_*`) ตกหล่นหมด กลายเป็นมือเปล่า
    /// ตอนนี้อ่าน `attack_type` จริงของอาวุธชิ้นนั้นจากข้อมูลเกม
    /// </summary>
    private AttackType CurrentAttackType()
    {
        if (!TryGetWeaponItem(out _, out EquipData.WeaponInfo info))
        {
            return AttackType.BareHands;
        }
        switch (info.AttackType)
        {
            case "axe": return AttackType.Axe;
            case "sword": return AttackType.Sword;
            case "spear": return AttackType.Spear;
            case "blunt": return AttackType.Blunt;
            case "arrow": return AttackType.Arrow;
            case "stone": return AttackType.Stone;
            default: return AttackType.BareHands;
        }
    }

    /// <summary>prototype ของไอเทมที่ใส่อยู่ในช่องนี้ (null ถ้าไม่มี)</summary>
    private string EquippedPrototype(string slot)
    {
        if (!_equippedItems.TryGetValue(slot, out string itemId) || string.IsNullOrEmpty(itemId))
        {
            return null;
        }
        lock (_inventory)
        {
            int idx = _inventory.FindIndex(it => it.Id == itemId);
            return idx >= 0 ? _inventory[idx].Prototype : null;
        }
    }

    private bool TryFindTarget(string entityId, out WorldPosition pos, out bool isAnimal)
    {
        pos = default;
        isAnimal = false;
        if (string.IsNullOrEmpty(entityId))
        {
            return false;
        }
        if (_world.Animals.TryGet(entityId, out ServerAnimal animal) && animal.IsAlive)
        {
            pos = animal.Position;
            isAnimal = true;
            return true;
        }
        ServerPlayer player = _world.FindPlayer(entityId);
        if (player != null && !player.Dead)
        {
            pos = player.CurrentPosition;
            return true;
        }
        return false;
    }
}
