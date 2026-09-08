using System;
using System.Collections.Generic;
using Durango.Network;
using Durango.Utils;
using Messages;
using Shared.Teleport;

namespace DurangoServer.Core;

/// <summary>
/// รีโมทคุมตัวละครของผู้เล่นคนอื่น (แบบ OpenKore — สั่งด้วย packet ไม่ใช่เมาส์/คีย์บอร์ด)
///
/// ทำไมต้องทำที่ server: ตัวเกมจริง **ไม่ยอมให้ server สั่ง <c>Move</c> ตัวเราเอง**
/// (`PlayerManager.HandleMoveMsg` หา entity ใน `_players` ซึ่งไม่มีตัวเราอยู่ในนั้น)
/// แต่ยอมรับ <c>Teleported</c> เสมอ — `PlayerController.Teleport()` ย้ายตัวละครท้องถิ่นทันที
/// การเดินจึงทำด้วยการ "วาร์ปทีละก้าว" ถี่ ๆ ให้ดูเหมือนเดิน แล้ว broadcast Move ให้คนอื่นเห็นลื่น ๆ
///
/// สั่งผ่าน cheat: <c>control &lt;ชื่อ|entityId&gt; &lt;คำสั่ง&gt;</c> — ดู docs/server/RemoteControl.md
/// </summary>
public partial class ServerPlayer
{
    /// <summary>ก้าวละกี่วินาที (ถี่กว่านี้ client จะกระตุก)</summary>
    private const double StepInterval = 0.35;

    /// <summary>ระยะต่อก้าว (หน่วยโลก) — 1 tile = 200</summary>
    private const float StepDistance = 200f;

    /// <summary>เดินได้สูงสุดกี่ก้าวต่อคำสั่ง (กันสั่งเดินข้ามแมพแล้ว deferred บวม)</summary>
    private const int MaxWalkSteps = 120;

    /// <summary>คิวเดินที่ยังเหลือ (ยกเลิกได้ด้วยคำสั่ง stop)</summary>
    private int _walkToken;

    /// <summary>ย้ายตัวละครไป tile นี้ทันที (client ยอมรับ Teleported เสมอ)</summary>
    public void ControlTeleport(int tileX, int tileY)
    {
        var pos = new WorldPosition(tileX * 200f + 100f, tileY * 200f + 100f);
        Send(new Teleported { Tile = new Point2(tileX, tileY), Type = TeleportType.Unknown });
        StopResting();
        RememberPosition(pos, _lastYaw);
        // คนอื่นไม่ได้รับ Teleported ของเรา ต้องยิง Move ให้เห็นว่าเราย้ายที่
        _world.BroadcastToViewers(EntityId, MakeStepMove(pos, 0.2), except: this);
        Console.WriteLine("[control] {0} → tile {1},{2}", Name, tileX, tileY);
    }

    /// <summary>เดินไป tile ปลายทางทีละก้าว (ยกเลิกด้วย ControlStop)</summary>
    public void ControlWalk(int tileX, int tileY)
    {
        int token = ++_walkToken;
        var dest = new WorldPosition(tileX * 200f + 100f, tileY * 200f + 100f);
        Console.WriteLine("[control] {0} เดินไป tile {1},{2}", Name, tileX, tileY);
        ScheduleStep(token, dest, 0);
    }

    /// <summary>หยุดเดิน</summary>
    public void ControlStop()
    {
        _walkToken++;
        Console.WriteLine("[control] {0} หยุดเดิน", Name);
    }

    private void ScheduleStep(int token, WorldPosition dest, int stepIndex)
    {
        _deferred.Add((Times.UnixTimeNow() + StepInterval, () =>
        {
            if (token != _walkToken || Dead)
            {
                return;                     // มีคำสั่งใหม่มาแทน หรือเราตายไปแล้ว
            }
            WorldPosition me = CurrentPosition;
            float dx = dest.x - me.x;
            float dy = dest.y - me.y;
            float dist = MathF.Sqrt(dx * dx + dy * dy);
            if (dist <= StepDistance || stepIndex >= MaxWalkSteps)
            {
                ControlTeleport((int)(dest.x / 200f), (int)(dest.y / 200f));
                return;
            }
            var next = new WorldPosition(me.x + dx / dist * StepDistance, me.y + dy / dist * StepDistance);
            Send(new Teleported { Tile = new Point2((int)(next.x / 200f), (int)(next.y / 200f)), Type = TeleportType.Unknown });
            RememberPosition(next, MathF.Atan2(dx, dy) * (180f / MathF.PI));
            _world.BroadcastToViewers(EntityId, MakeStepMove(next, StepInterval), except: this);
            ScheduleStep(token, dest, stepIndex + 1);
        }));
    }

    /// <summary>packet Move สำหรับให้ "คนอื่น" เห็นเราเคลื่อนที่ (ตัวเราเองใช้ Teleported)</summary>
    private Move MakeStepMove(WorldPosition to, double seconds)
    {
        double now = Times.UnixTimeNow();
        return new Move
        {
            EntityId = EntityId,
            Movements = new[]
            {
                new Movement
                {
                    MotionName = "Barehand_Walk",
                    MotionOption = 5,
                    PlaybackRate = 1f,
                    RotSpeed = 540f,
                    Path = new[]
                    {
                        new Location { Position = CurrentPosition, Yaw = _lastYaw, Time = now, Floor = 0, Height = 0f },
                        new Location { Position = to, Yaw = _lastYaw, Time = now + seconds, Floor = 0, Height = 0f }
                    }
                }
            }
        };
    }

    /// <summary>แตะของธรรมชาติที่ใกล้ที่สุดที่เอื้อมถึง แล้วเก็บ 1 ครั้ง</summary>
    public string ControlGather()
    {
        if (!_world.Terrain.TryFindNaturalNear(CurrentPosition, MaxReachTiles, out Point2 tile, out ushort type))
        {
            return "Tidak ada sumber daya alam dalam jangkauan.";
        }
        var touch = new Touch
        {
            EntityId = $"natural_{tile.x}_{tile.y}",
            EntityType = type,
            Tile = tile
        };
        HandleTouch(touch, default);
        Generator[] gens = _world.PeekGenerators(touch.EntityId);
        if (gens == null || gens.Length == 0)
        {
            return $"Menyentuh tile {tile.x},{tile.y} tetapi tidak ada yang bisa diambil.";
        }
        HandleCollect(new Collect { EntityId = touch.EntityId, GeneratorId = gens[0].Id, Tile = tile }, default);
        return $"Mengambil {gens[0].Name} di tile {tile.x},{tile.y}";
    }

    /// <summary>ตีสัตว์ที่ใกล้ที่สุดด้วยท่าแรกของอาวุธที่ถืออยู่</summary>
    public string ControlAttackNearest()
    {
        ServerAnimal[] animals = _world.Animals.Snapshot();
        ServerAnimal best = null;
        float bestDist = float.MaxValue;
        WorldPosition me = CurrentPosition;
        for (int i = 0; i < animals.Length; i++)
        {
            if (!animals[i].IsAlive)
            {
                continue;
            }
            float dx = animals[i].Position.x - me.x;
            float dy = animals[i].Position.y - me.y;
            float d = MathF.Sqrt(dx * dx + dy * dy);
            if (d < bestDist)
            {
                bestDist = d;
                best = animals[i];
            }
        }
        if (best == null)
        {
            return "Tidak ada dinosaurus atau hewan di sekitar.";
        }
        string[] actions = ActionData.ForWeaponTag(CurrentWeaponTag());
        if (actions.Length == 0)
        {
            return "Tidak ada skill serangan.";
        }
        HandleUseBattleAction(new UseBattleAction
        {
            ActionId = actions[0],
            StartAt = Times.UnixTimeNow(),
            TargetEntityId = best.EntityId,
            TargetTile = new Point2((int)(best.Position.x / 200f), (int)(best.Position.y / 200f))
        }, default);
        return $"Menyerang {best.EntityId} (type {best.EntityType} Lv.{best.Level}, jarak {bestDist / 200f:F1} tile)";
    }

    /// <summary>
    /// เรียกสัตว์มาเกิด "ข้างตัวผู้เล่นคนนั้น" (ไม่ใช่ข้างตัวคนสั่ง)
    /// เอาไว้เทสตอนตัวเองยืนอยู่ในเกม แล้วสั่งจากเครื่องมือข้างนอกให้สัตว์โผล่ตรงหน้า
    /// </summary>
    public string ControlSpawn(ushort entityType)
    {
        ServerAnimal born = _world.Animals.SpawnAt(CurrentPosition, entityType, CurrentHeight);
        SpawnTable.Entry e = SpawnTable.Find(born.EntityType);
        return $"Memanggil {e?.Name ?? ("type " + born.EntityType)} Lv.{born.Level} di dekat {Name}";
    }

    /// <summary>ฆ่าสัตว์ตัวที่ใกล้ผู้เล่นคนนั้นที่สุด — ได้ซากไว้เทสการแล่เนื้อทันที</summary>
    public string ControlKillNearest()
    {
        ServerAnimal[] animals = _world.Animals.Snapshot();
        ServerAnimal best = null;
        float bestDist = float.MaxValue;
        WorldPosition me = CurrentPosition;
        for (int i = 0; i < animals.Length; i++)
        {
            if (!animals[i].IsAlive)
            {
                continue;
            }
            float dx = animals[i].Position.x - me.x;
            float dy = animals[i].Position.y - me.y;
            float d = MathF.Sqrt(dx * dx + dy * dy);
            if (d < bestDist)
            {
                bestDist = d;
                best = animals[i];
            }
        }
        if (best == null)
        {
            return "Tidak ada dinosaurus atau hewan hidup di sekitar.";
        }
        // ให้เครดิตคนที่ถูกสั่ง ไม่ใช่ admin — ซากจะได้เรืองแสงให้คนที่ยืนอยู่ตรงนั้น
        _world.Animals.Damage(best.EntityId, best.LifeMax * 2f, EntityId);
        return $"Berhasil membunuh {best.EntityId} (type {best.EntityType} Lv.{best.Level}) jarak {bestDist / 200f:F1} tile — sentuh bangkai untuk butchering";
    }

    /// <summary>เติมเลือด/สตามินาให้เต็ม + ล้างความล้า (ฟื้นให้ด้วยถ้าตายอยู่)</summary>
    public string ControlHeal()
    {
        bool wasDead = Dead;
        if (wasDead)
        {
            ReviveAtSpawn();
        }
        else
        {
            RestoreSurvival(clearFatigue: true);
        }
        return wasDead ? $"Membangkitkan {Name} (teleport ke titik spawn)" : $"Memulihkan darah/stamina/menghapus kelelahan {Name}";
    }

    /// <summary>เสกของทดสอบให้ผู้เล่นคนนั้น</summary>
    public string ControlGive(string what, int count = 1)
    {
        switch (what)
        {
            case "axe":
                GiveEquipTestItem("axe_onehand_stone_01", "Kapak Batu", "weapon_axe_onehand_stone_2", 0);
                return $"Memberikan Kapak Batu kepada {Name}";
            case "clothes":
                GiveEquipTestItem("clothes_builder_01", "Pakaian Tukang", "clothes_builder_01", 0);
                return $"Memberikan Pakaian Tukang kepada {Name}";
            case "bonfire":
                lock (_inventory)
                {
                    _inventory.Add(MakeCapsuleItem("capsulated_bonfire", "Api Unggun", "furniture_workbench_bonfire"));
                }
                MarkDirty();
                SendInventory();
                return $"Memberikan Api Unggun kepada {Name}";
            case "tent":
                lock (_inventory)
                {
                    _inventory.Add(MakeCapsuleItem("capsulated_tent", "Tenda", "building_house_tent"));
                }
                MarkDirty();
                SendInventory();
                return $"Memberikan Tenda kepada {Name}";
            case "temptent":
            case "temp tent":
                lock (_inventory)
                {
                    _inventory.Add(MakeCapsuleItem("capsulated_temptent", "Tenda Darurat", "building_house_temp"));
                }
                MarkDirty();
                SendInventory();
                return $"Memberikan Tenda Darurat kepada {Name}";
            case "worktable":
            case "fur_table":
                lock (_inventory)
                {
                    _inventory.Add(MakeCapsuleItem("capsulated_fur_table", "Meja Kerja", "furniture_fur_table_01"));
                }
                MarkDirty();
                SendInventory();
                return $"Memberikan Meja Kerja kepada {Name}";
            case "box":
                lock (_inventory)
                {
                    _inventory.Add(MakeCapsuleItem("capsulated_fur_box_03_leaf", "Kotak Daun", "furniture_box"));
                }
                MarkDirty();
                SendInventory();
                return $"Memberikan Kotak Daun kepada {Name}";
            // วัตถุดิบพื้นฐาน — ไว้เทสสายคราฟต์ในเกมจริง (หิน 5 ก้อน = คราฟต์มีดหินได้เลย)
            case "stone":
                for (int i = 0; i < 5; i++)
                {
                    GiveEquipTestItem("stone", "Batu", "icon_nat_stone", 0);
                }
                return $"Memberikan 5 Batu kepada {Name} (Bisa langsung craft pisau batu)";
            case "knife":
                GiveEquipTestItem("blade_stone", "Pisau Batu", "icon_nat_blade_stone", 0);
                return $"Memberikan Pisau Batu kepada {Name}";
            // ชุดทำอาหารครบเซ็ต — ไว้เทสเช็คลิสต์ทำอาหารโดยไม่ต้องออกไปล่า/ขุดดินเอง
            case "cook":
            case "cookkit":
                GiveByPrototype("meat", 3, out _);
                GiveByPrototype("wood_bough", 2, out _);
                GiveByPrototype("water", 2, out _);
                GiveByPrototype("pot_01", 1, out _);
                GiveByPrototype("grill_stone", 1, out _);
                lock (_inventory)
                {
                    _inventory.Add(MakeCapsuleItem("capsulated_bonfire", "Api Unggun", "furniture_workbench_bonfire"));
                    _inventory.Add(MakeCapsuleItem("capsulated_bonfire_01", "Api Unggun Besar", "furniture_workbench_bonfire_01"));
                }
                MarkDirty();
                SendInventory();
                return $"Memberikan perlengkapan memasak kepada {Name} — Daging 3 · Ranting 2 · Air 2 · Panci · Panggangan · Api Unggun";
            default:
                // ชื่อ prototype ตรง ๆ ก็ให้ได้ (`control <ชื่อ> give meat`) — เทสสูตรไหนก็เสกของนั้น
                if (GiveByPrototype(what, count, out int given))
                {
                    string msg = $"Memberikan {ItemNameData.NameOf(what, what)} x{given} kepada {Name}";
                    if (given < count)
                    {
                        msg += $" (Meminta {count} tetapi tas hanya tersisa {given} slot)";
                    }
                    return msg;
                }
                return "Dapat diberikan: axe · clothes · bonfire · box · stone · knife · cook atau nama prototype langsung";
        }
    }

    /// <summary>ช่องกระเป๋าที่ยังว่างอยู่</summary>
    public int FreeInventorySlots()
    {
        lock (_inventory)
        {
            return Math.Max(0, PlayerInventoryMaxSize - _inventory.Count);
        }
    }

    /// <summary>
    /// เสกไอเทมตามชื่อ prototype — คืน false ถ้าไม่มีของชิ้นนั้นในเกม
    ///
    /// [แก้เอง] 3 ก.ย. 2026 — เดิมผู้เรียก clamp จำนวนไว้ที่ 50 ตายตัว ซึ่งเป็นเลขเดียวกับ
    /// ขนาดกระเป๋าทั้งใบ ⇒ ถ้ามีของอยู่แล้วจะล้นเงียบ ๆ · และมาโครของเกมที่สั่ง `it ... 60`
    /// ก็ได้ไม่ครบโดยไม่บอกสาเหตุ  ตอนนี้ตัดตามช่องที่เหลือจริงแล้ว *บอกกลับ* ว่าให้ได้เท่าไร
    /// </summary>
    private bool GiveByPrototype(string prototype, int count, out int given)
    {
        given = 0;
        if (string.IsNullOrEmpty(prototype) || !ItemNameData.Map.ContainsKey(prototype))
        {
            return false;
        }
        int room = FreeInventorySlots();
        given = Math.Clamp(count, 0, room);
        for (int i = 0; i < given; i++)
        {
            GiveEquipTestItem(prototype, ItemNameData.NameOf(prototype, prototype), ItemNameData.IconOf(prototype, string.Empty), 0);
        }
        return true;
    }

    /// <summary>พูดในช่องรวมแทนผู้เล่นคนนั้น</summary>
    public void ControlSay(string text)
    {
        _world.Broadcast(new SayInExclusiveChannel
        {
            Message = StampSpeaker(new Message_ { EntityId = EntityId, Body = text, Time = Times.UnixTimeNow() })
        });
    }

    /// <summary>สรุปสถานะสั้น ๆ ไว้ตอบกลับคนสั่ง</summary>
    public string ControlStatus()
    {
        WorldPosition p = CurrentPosition;
        int items;
        lock (_inventory)
        {
            items = _inventory.Count;
        }
        return $"{Name} tile {p.x / 200f:F0},{p.y / 200f:F0} สูง {CurrentHeight:F0} ชั้น {CurrentFloor} เลือด {CurrentLife:F0} ของ {items} ชิ้น{(Dead ? " ☠" : "")}";
    }
}
