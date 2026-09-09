using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using Durango.Network;
using Durango.Offline;
using Durango.Utils;
using Messages;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Shared.Item;
using Shared.Region;
using Shared.Economy;
using Shared.Faction;
using Shared.Skill;
using Shared.Social;
using Shared.Building;
using Shared.Etc;
using Shared.Animal;

namespace DurangoServer.Core;

// ============================================================================
// DurangoServer — ไฟล์หลักของ server
// ประกอบด้วย: ServerWorld (โลก), ServerPlayer (ผู้เล่น + handler เกมเพลย์),
// GameServer (TCP 8191), Gateway (HTTP 8190 + UDP knock), RadiotowerServer (แชท 8192)
// โปรโตคอล: MsgPack + Snappy, header 24 ไบต์ (time/seq/replyOf/typeCode/size)
// ============================================================================

// ServerPlayer.Cheat — ดูรายละเอียดที่ docs/server/ServerPlayer.Cheat.md

public partial class ServerPlayer
{

    /// <summary>คนสั่งเป็น admin ไหม (H-2) — คำสั่งที่ยุ่งกับผู้เล่นคนอื่นต้องผ่านด่านนี้</summary>
    private bool IsAdmin => GameServer.IsAdmin(EntityId, Name);

    /// <summary>
    /// admin web panel (Gateway /admin/cheat) เรียกทางนี้เพื่อสั่งคำสั่งทดสอบตัวเดียวกับที่พิมพ์ในแชท
    /// "ในนามของ" ผู้เล่นคนนี้ (มีผลกับตัวละครนี้ เช่น spawn/heal/tp ที่ตำแหน่งของมัน)
    /// ผลลัพธ์ที่เป็นข้อความจะถูกส่งกลับไปที่ตัวเกมของผู้เล่นคนนั้นด้วย (Info packet) เหมือนพิมพ์เอง
    /// ไม่ต้องมี PacketHeader จริงเพราะไม่มี client ฝั่งนี้ส่งคำขอมา — ใช้ header ว่าง (Seq=0) แทน
    /// </summary>
    public void RunAdminCheat(string rawCommand)
    {
        HandleCheat(new Cheat { _Cheat = rawCommand ?? "" }, default);
    }

    private static readonly Dictionary<string, ushort[]> DinoPacks = new(StringComparer.OrdinalIgnoreCase)
    {
        { "apex", new ushort[] { 2005, 2021, 2086, 2082 } },
        { "carnivores", new ushort[] { 2005, 2021, 2086, 2082 } },
        { "carnivore", new ushort[] { 2005, 2021, 2086, 2082 } },

        { "raptors", new ushort[] { 2001, 2002, 2023, 2029, 2024 } },
        { "raptor", new ushort[] { 2001, 2002, 2023, 2029, 2024 } },

        { "ceratops", new ushort[] { 2003, 2019, 2027, 2041, 2017 } },
        { "ceratopsians", new ushort[] { 2003, 2019, 2027, 2041, 2017 } },
        { "tricera", new ushort[] { 2003, 2019, 2027, 2041, 2017 } },

        { "sauropods", new ushort[] { 2004, 2133, 2179 } },
        { "sauropod", new ushort[] { 2004, 2133, 2179 } },
        { "brachio", new ushort[] { 2004, 2133, 2179 } },

        { "armored", new ushort[] { 2000, 2010, 2011, 2083, 2054 } },
        { "stego", new ushort[] { 2000, 2010, 2011, 2083, 2054 } },
        { "ankylo", new ushort[] { 2000, 2010, 2011, 2083, 2054 } },

        { "mammals", new ushort[] { 2008, 2007, 2020, 2013, 2012 } },
        { "mammal", new ushort[] { 2008, 2007, 2020, 2013, 2012 } },
        { "mammoth", new ushort[] { 2008, 2007, 2020, 2013, 2012 } },

        { "hadrosaurs", new ushort[] { 2009, 2048, 2030, 2177 } },
        { "hadrosaur", new ushort[] { 2009, 2048, 2030, 2177 } },
        { "parasau", new ushort[] { 2009, 2048, 2030, 2177 } },

        { "small", new ushort[] { 2015, 2025, 2022, 2034, 2033 } },
        { "compso", new ushort[] { 2015, 2025, 2022, 2034, 2033 } },

        { "bosses", new ushort[] { 2114, 2110, 2098, 2112, 2124, 2109 } },
        { "alpha", new ushort[] { 2114, 2110, 2098, 2112, 2124, 2109 } },
        { "boss", new ushort[] { 2114, 2110, 2098, 2112, 2124, 2109 } },

        { "events", new ushort[] { 2053, 2050, 2049, 2156, 2182, 2131 } },
        { "costumes", new ushort[] { 2053, 2050, 2049, 2156, 2182, 2131 } },
    };

    private static readonly Dictionary<string, ushort> DinoNames = new(StringComparer.OrdinalIgnoreCase)
    {
        { "trex", 2005 },
        { "t-rex", 2005 },
        { "tyrannosaurus", 2005 },
        { "alphatrex", 2114 },
        { "alpha_trex", 2114 },
        { "allosaurus", 2021 },
        { "tarbosaurus", 2086 },
        { "ceratosaurus", 2082 },
        { "oviraptor", 2002 },
        { "utahraptor", 2023 },
        { "deinonychus", 2029 },
        { "dilophosaurus", 2024 },
        { "coelophysis", 2016 },
        { "triceratops", 2003 },
        { "zebraceratops", 2027 },
        { "styracosaurus", 2019 },
        { "centrosaurus", 2041 },
        { "chasmosaurus", 2018 },
        { "protoceratops", 2017 },
        { "brachiosaurus", 2004 },
        { "apatosaurus", 2133 },
        { "amargasaurus", 2179 },
        { "stegosaurus", 2000 },
        { "ankylosaurus", 2010 },
        { "euoplocephalus", 2011 },
        { "kentrosaurus", 2083 },
        { "sabertooth", 2007 },
        { "smilodon", 2007 },
        { "direwolf", 2020 },
        { "wolf", 2020 },
        { "megaloceros", 2013 },
        { "macrauchenia", 2012 },
        { "parasaurolophus", 2009 },
        { "iguanodon", 2048 },
        { "corythosaurus", 2030 },
        { "compsognathus", 2015 },
        { "gallimimus", 2025 },
        { "pachycephalosaurus", 2022 },
        { "pachy", 2022 },
        { "dimetrodon", 2034 },
        { "dodo", 2033 },
        { "labrador", 2131 },
        { "dog", 2131 },
    };

    private void SendCheatReply(string text, PacketHeader header = default)
    {
        if (string.IsNullOrEmpty(text)) return;
        Send(new Info { Text = text }, header.Seq);
        if (IsAndroid)
        {
            SendNotice(text, "Sistem");
        }
        SendSystemChat(text, "Sistem");
    }

    private void HandleCheat(Cheat msg, PacketHeader header)
    {
        string raw = (msg._Cheat ?? "").Trim();
        if (raw.StartsWith("/"))
        {
            raw = raw.Substring(1).TrimStart();
        }
        string cmd = raw.ToLower();

        // H-2: ปิดคำสั่งทดสอบเป็นค่าเริ่มต้น — เดิมใครก็เสกของ/ฟื้นเลือด/เรียกสัตว์/ลากตัวคนอื่นได้
        if (!GameServer.CheatsEnabled)
        {
            Console.WriteLine($"[cheat] Ditolak {Name} ({EntityId}): '{cmd}' — Fitur cheat nonaktif");
            SendCheatReply("Fitur cheat sedang nonaktif. Jalankan server dengan parameter --enable-cheat untuk mengaktifkannya.", header);
            return;
        }
        Console.WriteLine($"[cheat] {EntityId}: {cmd}");

        if (cmd.Equals("help", StringComparison.Ordinal) || cmd.Equals("cheat", StringComparison.Ordinal) || cmd.Equals("?", StringComparison.Ordinal) || cmd.Equals("menu", StringComparison.Ordinal))
        {
            string helpText =
                "=== DAFTAR PERINTAH CHEAT IN-GAME ===\n" +
                "• /heal - Pulihkan darah & stamina penuh, hilangkan lelah\n" +
                "• /god - Mode kebal (tidak bisa mati / kebal serangan)\n" +
                "• /peace - Mode damai (dinosaurus tidak akan menyerang)\n" +
                "• /instantcraft - Mode craft instan (munculkan meja serbaguna & bebas bahan)\n" +
                "• /bench - Munculkan Meja Serbaguna (All-Round Workbench Lv. 60)\n" +
                "• /mats - Isi tas dengan alat & bahan craft lengkap untuk Auto Fill di UI\n" +
                "• /craft <nama> [jumlah] [level] - Langsung buat item apapun tanpa UI\n" +
                "• /craft all - Buka seluruh resep crafting & blueprint bangunan\n" +
                "• /sp <jumlah> - Tambahkan poin skill (SP)\n" +
                "• /maxskills - Buka semua skill dan maksimalkan level & profisiensi (Lv. 60)\n" +
                "• /maxprof - Maksimalkan seluruh profisiensi kategori ke Lv. 60\n" +
                "• /tame - Menjinakkan dinosaurus terdekat (radius 25 tile)\n" +
                "• /pet <list|spawn|return|mount|unmount|stay|follow|feed|add>\n" +
                "• /mount & /unmount - Menaiki / turun dari pet aktif\n" +
                "• /stay & /follow - Perintahkan pet diam / ikuti pemain\n" +
                "• /give <nama_item> [jumlah] [level] - Dapatkan item apapun\n" +
                "• /level <1-60> - Atur level karakter langsung\n" +
                "• /money <jumlah> - Tambahkan DurangoCoin\n" +
                "• /clearbag - Kosongkan seluruh tas inventory\n" +
                "• /spawn <trex/raptor/pack/id> - Munculkan dinosaurus\n" +
                "• /kill - Kalahkan dinosaurus terdekat\n" +
                "• /tp <x> <y> /tp spawn /tp center - Teleportasi posisi\n" +
                "• /islands & /travel <id> - Informasi dan perjalanan antar pulau\n" +
                "• /survival - Periksa status darah, stamina, lelah\n" +
                "• /save - Simpan progres dunia dan karakter";
            SendCheatReply(helpText, header);
            return;
        }

        if (cmd.StartsWith("level ", StringComparison.Ordinal) || cmd.StartsWith("setlevel ", StringComparison.Ordinal))
        {
            string[] parts = cmd.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && int.TryParse(parts[1], out int lv))
            {
                Level = Math.Clamp(lv, 1, MaxSkillLevel);
                SyncExpToLevel();
                MarkDirty();
                SendStatistics();
                SendCheatReply($"Level karakter berhasil diatur ke {Level}!", header);
                return;
            }
        }

        if (cmd.StartsWith("sp ", StringComparison.Ordinal) || cmd.StartsWith("skillpoint ", StringComparison.Ordinal))
        {
            string[] parts = cmd.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && int.TryParse(parts[1], out int spAdd))
            {
                _skillPoints = Math.Max(0, _skillPoints + spAdd);
                MarkDirty();
                SendSkills();
                SendCheatReply($"Berhasil menambahkan {spAdd} Poin Skill (SP)! (Total SP saat ini: {_skillPoints})", header);
                return;
            }
        }

        if (cmd.StartsWith("money ", StringComparison.Ordinal) || cmd.StartsWith("coin ", StringComparison.Ordinal))
        {
            string[] parts = cmd.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && long.TryParse(parts[1], out long amt))
            {
                if (_walletPaid == null) _walletPaid = new Dictionary<Currency, long>();
                long cur = _walletPaid.TryGetValue(Currency.PcCoin, out long c) ? c : 0;
                _walletPaid[Currency.PcCoin] = Math.Max(0, cur + amt);
                MarkDirty();
                SendWalletUpdated();
                SendCheatReply($"Berhasil menambahkan {amt} DurangoCoin! (Total: {_walletPaid[Currency.PcCoin]})", header);
                return;
            }
        }

        if (cmd.StartsWith("item ", StringComparison.Ordinal))
        {
            cmd = "give " + cmd.Substring(5);
            raw = "give " + raw.Substring(5);
        }

        if (cmd.StartsWith("craft ", StringComparison.Ordinal))
        {
            string sub = cmd.Substring(6).Trim();
            if (sub == "all" || sub == "unlock")
            {
                Send(new Recipes { Ids = RecipeData.AllRecipeIds });
                Send(new ArtifactBlueprints { Ids = RecipeData.AllBlueprintIds });
                SendCheatReply($"Berhasil membuka seluruh ({RecipeData.AllRecipeIds.Length}) resep crafting & ({RecipeData.AllBlueprintIds.Length}) blueprint bangunan!", header);
                return;
            }
            HandleDirectCraft(raw.Substring(raw.IndexOf(' ') + 1).Trim(), header);
            return;
        }

        if (cmd == "craft")
        {
            SendCheatReply("Gunakan: /craft <nama_resep|prototype> [jumlah] [level]\nContoh: /craft blade_bone 1 60\nAtau: /craft all (buka semua resep)", header);
            return;
        }

        if (cmd == "kill")
        {
            cmd = "kill animal";
        }

        // รีโมทคุมตัวละครคนอื่น: control <ชื่อ|entityId> <คำสั่ง> [args]
        // (ไม่เข้า switch เพราะต้องเก็บตัวพิมพ์ใหญ่-เล็กของชื่อไว้)
        if (cmd.StartsWith("control ", StringComparison.Ordinal))
        {
            HandleControl(raw, header);
            return;
        }

        // spawn [ชนิด/ชื่อ/หมวด]            — เกิดตรงที่ยืนอยู่ (ชนิด 2000-2999, ไม่ใส่ = สุ่ม)
        // spawn <tileX> <tileY> [ความสูง]  — เกิดที่พิกัดที่ระบุ
        if (cmd.StartsWith("spawn ", StringComparison.Ordinal))
        {
            string[] sp = cmd.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (sp.Length == 2)
            {
                if (ushort.TryParse(sp[1], out ushort wantType) && wantType >= 2000)
                {
                    ServerAnimal one = _world.Animals.SpawnAt(CurrentPosition, wantType, CurrentHeight);
                    string known = AnimalData.TryGet(wantType, out AnimalData.AnimalInfo ai) ? ai.ModelPath : "(tidak dikenal)";
                    SendCheatReply($"Muncul hewan type {one.EntityType} lv{one.Level} [id={one.EntityId}] — Model: {known}", header);
                    return;
                }
                if (DinoPacks.TryGetValue(sp[1], out ushort[] pack))
                {
                    WorldPosition basePos = CurrentPosition;
                    float radius = 250f;
                    for (int i = 0; i < pack.Length; i++)
                    {
                        float angle = (float)(i * (2.0 * Math.PI / pack.Length));
                        WorldPosition spawnPos = new WorldPosition(basePos.x + MathF.Cos(angle) * radius, basePos.y + MathF.Sin(angle) * radius);
                        _world.Animals.SpawnAt(spawnPos, pack[i], CurrentHeight);
                    }
                    SendCheatReply($"Muncul 1 paket dinosaurus '{sp[1]}' ({pack.Length} ekor) melingkari posisimu!", header);
                    return;
                }
                if (DinoNames.TryGetValue(sp[1], out ushort namedType))
                {
                    ServerAnimal one = _world.Animals.SpawnAt(CurrentPosition, namedType, CurrentHeight);
                    string known = AnimalData.TryGet(namedType, out AnimalData.AnimalInfo ai) ? ai.ModelPath : sp[1];
                    SendCheatReply($"Muncul {sp[1]} (type {one.EntityType} lv{one.Level}) di sampingmu! — Model: {known}", header);
                    return;
                }
            }
            if (sp.Length >= 3 && int.TryParse(sp[1], out int sx) && int.TryParse(sp[2], out int sy))
            {
                float height = CurrentHeight;
                if (sp.Length >= 4 && float.TryParse(sp[3], out float h))
                {
                    height = h;
                }
                ServerAnimal at = _world.Animals.SpawnAt(new WorldPosition(sx * 200f + 100f, sy * 200f + 100f), 0, height);
                SendCheatReply($"Muncul dinosaurus type {at.EntityType} Lv.{at.Level} di tile {sx},{sy} ketinggian {height:F0}", header);
                return;
            }
        }

        // tame — จับไดโนเสาร์ตัวที่อยู่ใกล้ที่สุดในระยะ 25 tile
        if (cmd.Equals("tame", StringComparison.Ordinal) || cmd.StartsWith("tame ", StringComparison.Ordinal))
        {
            ServerAnimal nearest = null;
            float minDistanceSq = 25f * 25f * 200f * 200f;
            foreach (var a in _world.Animals.Snapshot())
            {
                if (a != null && a.IsAlive)
                {
                    float dSq = DistanceSqTo(a.Position);
                    if (dSq < minDistanceSq)
                    {
                        minDistanceSq = dSq;
                        nearest = a;
                    }
                }
            }

            if (nearest == null)
            {
                SendCheatReply("Tidak ada dinosaurus dalam jarak 25 tile untuk di-tame.", header);
                return;
            }

            TameAnimal(nearest);
            SendCheatReply($"Berhasil menjinakkan dinosaurus type {nearest.EntityType} (Lv.{nearest.Level})!", header);
            return;
        }

        // pet <list | spawn | return | add>
        if (cmd.StartsWith("pet", StringComparison.Ordinal))
        {
            string[] parts = raw.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            string sub = parts.Length > 1 ? parts[1].ToLowerInvariant() : "list";

            if (sub == "list")
            {
                if (_pets.Count == 0)
                {
                    SendCheatReply("Kamu belum memiliki pet apapun. Gunakan /tame atau /pet add <nama/id>.", header);
                    return;
                }

                var sb = new StringBuilder("Daftar Pet Kamu:\n");
                for (int i = 0; i < _pets.Count; i++)
                {
                    var p = _pets[i];
                    string status = p.IsSpawned ? (p.IsBoarding ? "[Ditunggangi]" : "[Aktif]") : "[Istirahat]";
                    sb.AppendLine($"{i + 1}. {p.Name} (Lv.{p.Level} Rank {((PetRank)p.Rank)}) {status} - ID: {p.EntityId}");
                }
                SendCheatReply(sb.ToString().TrimEnd(), header);
                return;
            }

            if (sub == "spawn")
            {
                if (parts.Length < 3)
                {
                    if (_pets.Count > 0)
                    {
                        SpawnPetInternal(_pets[0]);
                        SendCheatReply($"Memanggil {_pets[0].Name}!", header);
                    }
                    else
                    {
                        SendCheatReply("Gunakan: /pet spawn <nama/nomor/id>", header);
                    }
                    return;
                }

                string target = parts[2];
                PetSave found = null;
                if (int.TryParse(target, out int idx) && idx >= 1 && idx <= _pets.Count)
                {
                    found = _pets[idx - 1];
                }
                else
                {
                    found = _pets.FirstOrDefault(p =>
                        string.Equals(p.EntityId, target, StringComparison.OrdinalIgnoreCase) ||
                        p.Name.Contains(target, StringComparison.OrdinalIgnoreCase) ||
                        (PetData.FindByEntityType(p.EntityType) is PetTemplate tpl && (
                            tpl.Species.Contains(target, StringComparison.OrdinalIgnoreCase) ||
                            tpl.TypeName.Contains(target, StringComparison.OrdinalIgnoreCase) ||
                            tpl.Name.Contains(target, StringComparison.OrdinalIgnoreCase))));
                }

                if (found == null)
                {
                    SendCheatReply($"Pet '{target}' tidak ditemukan.", header);
                    return;
                }

                SpawnPetInternal(found);
                SendCheatReply($"Memanggil {found.Name}!", header);
                return;
            }

            if (sub == "return" || sub == "dismiss" || sub == "despawn")
            {
                if (_spawnedPet == null)
                {
                    SendCheatReply("Tidak ada pet yang sedang aktif/dipanggil.", header);
                    return;
                }

                string pName = _spawnedPet.Name;
                ReturnPetInternal(_spawnedPet);
                SendCheatReply($"Pet {pName} telah dikembalikan/istirahat.", header);
                return;
            }

            if (sub == "add")
            {
                if (parts.Length < 3)
                {
                    SendCheatReply("Gunakan: /pet add <spesies/id> [level] (contoh: /pet add trex, /pet add raptor 60)", header);
                    return;
                }

                string query = parts[2];
                int level = 1;
                if (parts.Length >= 4 && int.TryParse(parts[3], out int lv))
                {
                    level = Math.Clamp(lv, 1, 60);
                }

                PetTemplate tmpl = PetData.FindByQuery(query);
                ushort entityType = tmpl?.EntityType ?? (ushort.TryParse(query, out ushort qid) ? qid : (ushort)3001);
                string petName = tmpl?.Name ?? query;
                var created = AddPetDirect(entityType, petName, level);
                SendCheatReply($"Berhasil menambahkan pet {created.Name} (Lv.{created.Level})!", header);
                return;
            }

            if (sub == "mount" || sub == "ride")
            {
                if (_spawnedPet == null)
                {
                    SendCheatReply("Tidak ada pet yang aktif. Panggil pet dulu dengan /pet spawn.", header);
                    return;
                }
                MountPetInternal();
                SendCheatReply($"Menaiki pet {_spawnedPet.Name}!", header);
                return;
            }

            if (sub == "unmount" || sub == "dismount")
            {
                if (_spawnedPet == null || !_spawnedPet.IsBoarding)
                {
                    SendCheatReply("Kamu sedang tidak menaiki pet.", header);
                    return;
                }
                UnmountPetInternal();
                SendCheatReply($"Turun dari pet {_spawnedPet.Name}.", header);
                return;
            }

            if (sub == "stay" || sub == "stop")
            {
                if (_spawnedPet == null)
                {
                    SendCheatReply("Tidak ada pet yang aktif. Panggil pet dulu dengan /pet spawn.", header);
                    return;
                }
                SetPetStay(true);
                SendCheatReply($"Pet {_spawnedPet.Name} diperintahkan untuk diam di tempat (Stay).", header);
                return;
            }

            if (sub == "follow" || sub == "come")
            {
                if (_spawnedPet == null)
                {
                    SendCheatReply("Tidak ada pet yang aktif. Panggil pet dulu dengan /pet spawn.", header);
                    return;
                }
                SetPetStay(false);
                SendCheatReply($"Pet {_spawnedPet.Name} diperintahkan untuk mengikuti kamu (Follow).", header);
                return;
            }

            if (sub == "feed")
            {
                if (_spawnedPet == null)
                {
                    SendCheatReply("Tidak ada pet yang aktif. Panggil pet dulu dengan /pet spawn.", header);
                    return;
                }

                Item food = default;
                bool foundFood = false;
                lock (_inventory)
                {
                    int idx = _inventory.FindIndex(it => IsEdible(it) || it.Prototype.Contains("meat") || it.Prototype.Contains("fruit") || it.Prototype.Contains("bread"));
                    if (idx >= 0)
                    {
                        food = _inventory[idx];
                        foundFood = true;
                        _inventory.RemoveAt(idx);
                    }
                }

                if (foundFood)
                {
                    FeedPet(_spawnedPet, food);
                    Send(new InventoryUpdated { EntityId = EntityId, RemovedItemIds = new[] { food.Id } });
                    SendInventory();
                    SendCheatReply($"Memberi makan {_spawnedPet.Name} dengan {food.Name}! (Hungry: {_spawnedPet.Hungry:F0}/{_spawnedPet.HungryMax:F0})", header);
                }
                else
                {
                    SendCheatReply("Tidak ada makanan di tas untuk diberikan ke pet.", header);
                }
                return;
            }

            if (sub == "speed")
            {
                if (_spawnedPet == null)
                {
                    SendCheatReply("Tidak ada pet yang aktif. Panggil pet dulu dengan /pet spawn.", header);
                    return;
                }
                if (parts.Length < 3 || !float.TryParse(parts[2], out float spd))
                {
                    SendCheatReply($"Kecepatan pet {_spawnedPet.Name} saat ini: {_spawnedPet.Speed:F0}. Gunakan: /pet speed <nilai> (contoh: /pet speed 800)", header);
                    return;
                }
                _spawnedPet.Speed = Math.Clamp(spd, 100f, 2500f);
                var petMsg = ConvertToPetMessage(_spawnedPet);
                Send(petMsg);
                _world.BroadcastToViewers(EntityId, petMsg, except: this);
                SendCheatReply($"Kecepatan pet {_spawnedPet.Name} diubah menjadi {_spawnedPet.Speed:F0}!", header);
                MarkDirty();
                return;
            }

            SendCheatReply("Perintah pet: /pet list, /pet spawn <nama/nomor>, /pet return, /pet add <spesies> [level], /pet mount, /pet unmount, /pet stay, /pet follow, /pet feed, /pet speed <nilai>", header);
            return;
        }

        if (cmd.Equals("mount", StringComparison.Ordinal) || cmd.Equals("ride", StringComparison.Ordinal))
        {
            if (_spawnedPet == null)
            {
                SendCheatReply("Tidak ada pet yang aktif. Panggil pet dulu dengan /pet spawn.", header);
                return;
            }
            MountPetInternal();
            SendCheatReply($"Menaiki pet {_spawnedPet.Name}!", header);
            return;
        }

        if (cmd.Equals("unmount", StringComparison.Ordinal) || cmd.Equals("dismount", StringComparison.Ordinal))
        {
            if (_spawnedPet == null || !_spawnedPet.IsBoarding)
            {
                SendCheatReply("Kamu sedang tidak menaiki pet.", header);
                return;
            }
            UnmountPetInternal();
            SendCheatReply($"Turun dari pet {_spawnedPet.Name}.", header);
            return;
        }

        if (cmd.Equals("stay", StringComparison.Ordinal) || cmd.Equals("stop", StringComparison.Ordinal))
        {
            if (_spawnedPet == null)
            {
                SendCheatReply("Tidak ada pet yang aktif. Panggil pet dulu dengan /pet spawn.", header);
                return;
            }
            SetPetStay(true);
            SendCheatReply($"Pet {_spawnedPet.Name} diperintahkan untuk diam di tempat (Stay).", header);
            return;
        }

        if (cmd.Equals("follow", StringComparison.Ordinal) || cmd.Equals("come", StringComparison.Ordinal))
        {
            if (_spawnedPet == null)
            {
                SendCheatReply("Tidak ada pet yang aktif. Panggil pet dulu dengan /pet spawn.", header);
                return;
            }
            SetPetStay(false);
            SendCheatReply($"Pet {_spawnedPet.Name} diperintahkan untuk mengikuti kamu (Follow).", header);
            return;
        }

        // give <prototype> [จำนวน] — เสกไอเทมอะไรก็ได้ที่มีอยู่ในเกม (ชื่อ/ไอคอน/tag มาจากข้อมูลจริง)
        // มีไว้เทสสูตรคราฟต์/ทำอาหาร: `give meat 3` แล้วเอาไปย่างได้เลย ไม่ต้องออกไปล่าจริง
        if (cmd.StartsWith("give ", StringComparison.Ordinal))
        {
            string[] g = raw.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (g.Length < 2)
            {
                SendCheatReply("Gunakan: /give <prototype> [jumlah] [level] (contoh: /give meat 10)", header);
                return;
            }
            string proto = g[1];
            int want = 1;
            if (g.Length >= 3 && int.TryParse(g[2], out int n))
            {
                want = Math.Clamp(n, 1, 999);
            }
            // [TodoList/02] give <proto> <จำนวน> [เลเวล] — เสกวัสดุเลเวลสูงไว้เทสเลเวลผลลัพธ์คราฟต์
            int giveLevel = 0;
            if (g.Length >= 4 && int.TryParse(g[3], out int lv))
            {
                giveLevel = Math.Clamp(lv, 1, 80);
            }
            if (!ItemNameData.Map.ContainsKey(proto))
            {
                // ไม่ใช่ชื่อ prototype — ลองเป็นชุดของสำเร็จรูปแทน (axe · bonfire · cook ฯลฯ)
                // จะได้ใช้คำสั่งเดียวกันทั้งจากคอนโซลและจากกล่องเครื่องมือ (control <ชื่อ> give ...)
                SendCheatReply(ControlGive(proto, want), header);
                return;
            }
            // [แก้เอง] 3 ก.ย. 2026 — เดิมตัดที่ 20 ตายตัวแล้ว **ใส่เข้ากระเป๋าโดยไม่เช็คช่องว่างเลย**
            // ⇒ ถ้ากระเป๋าเกือบเต็มอยู่แล้ว จำนวนของจะทะลุ PlayerInventoryMaxSize (50)
            // ตอนนี้ตัดตามช่องที่เหลือจริง แล้วบอกกลับว่าให้ได้เท่าไร
            int room = FreeInventorySlots();
            int give = Math.Clamp(want, 0, room);
            for (int i = 0; i < give; i++)
            {
                Item made = MakeGatheredItem(new Generator
                {
                    Id = proto,
                    Name = ItemNameData.NameOf(proto, proto),
                    Icon = ItemNameData.IconOf(proto, string.Empty),
                    Level = giveLevel
                });
                lock (_inventory)
                {
                    _inventory.Add(made);
                }
            }
            MarkDirty();
            SendInventory();
            string reply = $"Mendapatkan {ItemNameData.NameOf(proto, proto)} x{give} (prototype={proto}{(giveLevel > 0 ? $" Lv.{giveLevel}" : "")})";
            if (give < want)
            {
                reply += $" — Meminta {want} tetapi tas hanya tersisa {room} slot";
            }
            SendCheatReply(reply, header);
            return;
        }

        // shutdown — test-only graceful stop สำหรับ restart acceptance harness
        if (cmd == "shutdown")
        {
            _world.SaveAll(force: true);
            SendCheatReply("Penyimpanan selesai. Menutup server...", header);
            Environment.Exit(0);
            return;
        }

        // architect add <artifactId> <entityId> — test-only grant สำหรับตรวจ shared storage contention
        if (cmd.StartsWith("architect add ", StringComparison.Ordinal))
        {
            string[] parts = raw.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 4 && _world.TryAddArtifactArchitect(parts[2], parts[3], EntityId, out AppearArtifact updated))
            {
                _world.AnnounceArtifact(updated);
                SendCheatReply($"Menambahkan architect {parts[3]} untuk {parts[2]}.", header);
            }
            else
            {
                SendCheatReply("Gunakan: /cheat architect add <artifactId> <entityId> (harus pemilik artifact)", header);
            }
            return;
        }

        // tp <tileX> <tileY> — วาร์ปตัวเองไปพิกัดที่ระบุ
        // ไว้เทสระยะการมองเห็น (เดินจริงติดเพดานความเร็ว M-2 ต้องเดินหลายรอบกว่าจะพ้นระยะ)
        if (cmd.StartsWith("tp ", StringComparison.Ordinal) && cmd != "tp spawn" && cmd != "tp center")
        {
            string[] t = cmd.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (t.Length >= 3 && int.TryParse(t[1], out int tx) && int.TryParse(t[2], out int ty))
            {
                ControlTeleport(tx, ty);
                SendCheatReply($"Teleport ke koordinat tile {tx},{ty}.", header);
            }
            else
            {
                SendCheatReply("Gunakan: /tp <tileX> <tileY> atau /tp spawn atau /tp center", header);
            }
            return;
        }

        // exp <จำนวน> — ยัด exp ให้เลย ไว้เทสว่าขึ้นเลเวลแล้วค่าสถานะ/หลอดโตจริงไหม
        if (cmd.StartsWith("exp ", StringComparison.Ordinal))
        {
            if (int.TryParse(cmd.Substring(4).Trim(), out int amount) && amount > 0)
            {
                GainExp(Math.Clamp(amount, 1, 1000000), "cheat");
                SendCheatReply($"Mendapatkan {amount} EXP — Sekarang Level {Level} (Total EXP: {TotalExp})", header);
            }
            else
            {
                SendCheatReply("Gunakan: /exp <jumlah>", header);
            }
            return;
        }

        // [แก้เอง] 3 ก.ย. 2026 — ลบ handler `give` ตัวที่สองทิ้ง
        //
        // มันซ้ำกับตัวข้างบน (บรรทัด ~102) ซึ่ง return ไปก่อนเสมอ ⇒ ตัวนี้เป็นโค้ดตายมาตลอด
        // เจอตอนไล่ตรวจว่าทำไมแก้เพดานจำนวนแล้วไม่มีผล — แก้ผิดตัวเพราะไม่รู้ว่ามีสองอัน
        // (ตัวที่ทำงานจริงถูกย้ายมารวมกรณี "ไม่ใส่ชื่อของ" ไว้แล้ว)

        // why <ชื่อสูตร> — ทำไมคราฟต์สูตรนี้ไม่ได้ (ไล่เช็คทีละข้อแล้วบอกว่าขาดอะไร)
        if (cmd.StartsWith("why ", StringComparison.Ordinal))
        {
            SendCheatReply(ExplainRecipe(cmd.Substring(4).Trim()), header);
            return;
        }

        // poi ... — จัดการจุดสนใจสด ๆ (ดู ServerPlayer.CheatPOI.cs)
        // แยกไปคนละไฟล์เพราะมีหลายคำสั่งย่อยและต้องมีตัวตรวจว่าวางถูกที่ไหม
        if (cmd == "poi" || cmd.StartsWith("poi ", StringComparison.Ordinal))
        {
            string poiArgs = cmd.Length <= 4 ? string.Empty : cmd.Substring(4).Trim();
            SendCheatReply(CheatPOI(poiArgs), header);
            return;
        }

        // place real <blueprintId> — วางสิ่งปลูกสร้าง "สร้างเสร็จแล้ว" ตรง ๆ เพื่อตรวจโมเดล
        // ใช้ไล่บั๊ก "สร้างเสร็จแล้วยังเป็นโครงไม้" (Parts ว่าง = client โชว์นั่งร้าน)
        if (cmd.StartsWith("place real ", StringComparison.Ordinal)
            || cmd.StartsWith("place_real ", StringComparison.Ordinal))
        {
            string want = cmd.Substring("place real ".Length).Trim();
            if (want != "fire")   // "place real fire" ยังใช้ทางเดิม (จุดไฟจริง)
            {
                SendCheatReply(PlaceCompleted(want), header);
                return;
            }
        }

        // travel <รหัสเกาะ> — เดินทางข้ามเกาะ (Beta 1.1)
        if (cmd.StartsWith("travel ", StringComparison.Ordinal) || cmd.StartsWith("warp ", StringComparison.Ordinal))
        {
            string prefix = cmd.StartsWith("travel ", StringComparison.Ordinal) ? "travel " : "warp ";
            string want = cmd.Substring(prefix.Length).Trim();
            SendCheatReply(TravelTo(want), header);
            return;
        }

        // effect <id> [วินาที] — เทสบัฟ/ดีบัฟจากอาหารให้มีผลจริง โดยไม่ต้องหาไอเทมที่ให้บัฟนั้น
        //   cheat effect poisoning       → ติดพิษ 60 วิ (เลือดไหลลง)
        //   cheat effect life_up 30      → ฟื้นเลือด 30 วิ
        //   cheat effect energetic       → บัฟสตามินา (ทำงานเปลืองน้อยลง)
        //   cheat effect clear           → ล้างบัฟทั้งหมด
        if (cmd == "effect" || cmd.StartsWith("effect ", StringComparison.Ordinal))
        {
            SendCheatReply(CheatApplyEffect(cmd.Length <= 6 ? string.Empty : cmd.Substring(6).Trim()), header);
            return;
        }

        switch (cmd)
        {
            case "island":
            case "islands":
                SendCheatReply(DescribeIslands(), header);
                break;
            case "tp spawn":
            {
                var entry = _world.GetEntryPosition();
                ControlTeleport((int)(entry.x / 200f), (int)(entry.y / 200f));
                SendCheatReply($"Teleport ke titik spawn pulau (tile {(int)(entry.x / 200f)},{(int)(entry.y / 200f)}).", header);
                break;
            }
            case "tp center":
            {
                int cx = _world.Terrain.Width / 2;
                int cy = _world.Terrain.Height / 2;
                ControlTeleport(cx, cy);
                SendCheatReply($"Teleport ke tengah pulau (tile {cx},{cy}).", header);
                break;
            }
            case "instantcraft":
            case "instant craft":
            case "craft instant":
            {
                InstantCraft = !InstantCraft;
                SendUnlockedRecipesAndBlueprints();
                string benchMsg = "";
                if (InstantCraft)
                {
                    SpawnAllroundWorkbench(out benchMsg);
                }
                SendCheatReply(InstantCraft
                    ? $"Mode Instant Craft AKTIF: Semua resep terbuka, bebas bahan & stamina, craft selesai instan (0.05s)!\n{benchMsg}\n• Gunakan /mats untuk mengisi alat & bahan ke tas (bisa Auto Fill di UI)\n• Atau /craft <nama_resep> untuk langsung buat item apapun tanpa UI"
                    : "Mode Instant Craft NONAKTIF.", header);
                break;
            }
            case "bench":
            case "workbench":
            case "spawnbench":
            {
                SpawnAllroundWorkbench(out string benchMsg);
                SendCheatReply(benchMsg + "\n(Mendukung semua resep senjata, alat, baju, kiln, loom, masak Lv. 60)", header);
                break;
            }
            case "delbench":
            case "removebench":
            {
                RemoveNearbyWorkbenches(out string delMsg);
                SendCheatReply(delMsg, header);
                break;
            }
            case "mats":
            case "materials":
            case "kit craft":
            case "craftkit":
            {
                GiveCraftingKit(header);
                break;
            }
            case "craft all":
            case "unlockrecipes":
            case "unlock all":
            {
                Send(new Recipes { Ids = RecipeData.AllRecipeIds });
                Send(new ArtifactBlueprints { Ids = RecipeData.AllBlueprintIds });
                SendCheatReply($"Berhasil membuka seluruh ({RecipeData.AllRecipeIds.Length}) resep crafting & ({RecipeData.AllBlueprintIds.Length}) blueprint bangunan!", header);
                break;
            }
            case "info":
                SendCheatReply("DurangoServer v0.1 - Pemain online: " + _world.Count, header);
                break;
            case "who":
            {
                // ใครออนไลน์อยู่บ้าง — เครื่องมือข้างนอกใช้หาชื่อตัวละครไปสั่ง control ต่อ
                ServerPlayer[] online = _world.SnapshotPlayers();
                if (online.Length == 0)
                {
                    SendCheatReply("Tidak ada pemain lain yang online.", header);
                    break;
                }
                var sb = new StringBuilder();
                sb.Append("Pemain online (").Append(online.Length).Append(" orang):");
                for (int i = 0; i < online.Length; i++)
                {
                    WorldPosition p = online[i].CurrentPosition;
                    sb.Append("\n  ").Append(online[i].Name)
                      .Append(" | ").Append(online[i].EntityId)
                      .Append(" | tile ").Append((int)(p.x / 200f)).Append(',').Append((int)(p.y / 200f))
                      .Append(" | lv").Append(online[i].Level)
                      .Append(online[i].Dead ? " ☠" : "");
                }
                SendCheatReply(sb.ToString(), header);
                break;
            }
            case "stats":
                SendStatistics();
                break;
            case "heal":
            case "revive":
                // ฟื้นชีพ ณ จุดเดิมทันที (In-place Revive) เลือด 100%, สตามินาเต็ม, ล้างความล้า 0
                // ไม่วาร์ปไปจุดเกิด sn20snow / จุดอื่น ไม่ติด TeleportLoadingCurtain ค้าง
                bool wasDead = Dead;
                ReviveHere(fullHeal: true);
                SendCheatReply(wasDead
                    ? "Bangkit kembali & pulih sepenuhnya! Darah & stamina penuh, kelelahan 0."
                    : "Pulih sepenuhnya! Darah & stamina penuh, kelelahan 0.", header);
                break;
            case "peace":
            case "peaceful":
            case "passive":
            case "noaggro":
                IsPeaceful = !IsPeaceful;
                if (IsPeaceful)
                {
                    _world.Animals.ClearAllTargets();
                }
                SendCheatReply(IsPeaceful ? "Mode Damai AKTIF (Peace): Dinosaurus tidak akan melihat, mengejar, atau menyerangmu!" : "Mode Damai NONAKTIF: Dinosaurus normal kembali.", header);
                break;
            case "god":
            case "inv":
            case "invincible":
                IsInvincible = !IsInvincible;
                SendCheatReply(IsInvincible ? "Mode Kebal AKTIF (God Mode): Kamu kebal terhadap semua serangan & damage!" : "Mode Kebal NONAKTIF: Darah berkurang normal.", header);
                break;
            case "checklist":
                SendCheatReply(DescribeChecklist(), header);
                break;
            case "quests":
                SendCheatReply(DescribeQuests(), header);
                break;
            // เดิม gather/attack มีแต่ในสาย `control <ชื่อ>` ซึ่งต้องเป็น admin
            // ทำให้บอทเทสสั่งตัวเองไม่ได้ — เพิ่มแบบสั่งตัวเองไว้ด้วย
            case "gather":
            {
                // ถ้าไม่มีของธรรมชาติในระยะเอื้อม ให้วาร์ปไปหาจุดที่ใกล้ที่สุดก่อน
                // (บอทเทสไม่ได้เดินไปไหน ยืนอยู่จุดเกิดเฉย ๆ — ถ้าไม่ช่วยหาให้ก็เก็บอะไรไม่ได้เลย)
                string result = ControlGather();
                if (result.StartsWith("Tidak ada sumber daya", StringComparison.Ordinal)
                    && _world.Terrain.TryFindNaturalNear(CurrentPosition, 400, out Point2 far, out ushort _))
                {
                    ControlTeleport(far.x, far.y);
                    result = ControlGather() + $" (Teleport ke tile {far.x},{far.y} terlebih dahulu)";
                }
                SendCheatReply(result, header);
                break;
            }
            case "attack":
                SendCheatReply(ControlAttackNearest(), header);
                break;
            case "questskip":
                // ตัวช่วยเทส: ทำทุกขั้นให้เสร็จยกเว้นขั้นสุดท้าย เพื่อกระโดดไปเทสปลายสายได้เร็ว
                // (ไม่ให้รางวัล — แค่ปลดล็อกสายให้เดินต่อ)
                SendCheatReply(SkipQuestsForTest(), header);
                break;
            // ---------------------------------------------------------------- ปลูกผัก
            case "farm":
                // วางแปลงผักสำเร็จรูปตรงที่ยืน + แจกเมล็ด/น้ำ/ปุ๋ยให้ครบชุด
                SendCheatReply(MakeTestFarm(), header);
                break;
            case "seeds":
                SendCheatReply(GiveFarmSupplies(), header);
                break;
            case "grow":
                // เร่งทุกแปลงของตัวเองให้โตทันที (ข้ามการรอ)
                SendCheatReply(RushMyFarms(), header);
                break;
            case "farms":
                SendCheatReply(DescribeMyFarms(), header);
                break;
            case "save":
                // บังคับเซฟโลกเดี๋ยวนี้ — ปกติ autosave ทุก 60 วิ
                // (เทสเรื่อง "รีสตาร์ทแล้วผลผลิตต้องไม่เกิดใหม่" ต้องใช้อันนี้)
                SendCheatReply($"Dunia berhasil disimpan ({_world.SaveAll(force: true)} file).", header);
                break;
            case "abilities":
                // ดูค่าสถานะ 8 ตัว + เลือด/สตามินาสูงสุด + พลังอาวุธที่ถืออยู่ (ไว้เทียบก่อน/หลังใส่ของ)
                SendCheatReply(DescribeAbilities(), header);
                break;
            case "skills":
                // ดูว่าสกิลที่เรียนไปมีผลเท่าไรแล้ว (ไว้เทียบก่อน/หลังเรียน)
                SendCheatReply(
                    $"Level {Level} · EXP {TotalExp} (butuh {LevelData.ToNextLevel(TotalExp)} lagi untuk naik level) · Poin Skill {_skillPoints}\n"
                    + DescribeSkillBonuses(), header);
                break;
            case "maxskills":
            case "max skills":
                // [แก้เอง] 24 ส.ค. 2026 — เจ้าของขอ "อัพเลเวลสกิลให้เต็ม" สำหรับเทสเฉยๆ (โหมด
                // --enable-cheat เท่านั้น) — เดินเลเวลผู้เล่นขึ้นสุด + ปลดทุกสกิลในเกมที่ MaxSkillLevel
                // ตรงๆ ไม่ผ่านระบบแต้ม/ลำดับปกติ (เหมือน HandleLearnSkill แต่ไม่มีการเช็ค/หักแต้ม)
                {
                    Level = MaxSkillLevel;
                    SyncExpToLevel();
                    int granted = 0;
                    foreach (KeyValuePair<string, int> kv in SkillData.SkillCategory)
                    {
                        string skillId = kv.Key;
                        Shared.Skill.Category category = (Shared.Skill.Category)kv.Value;
                        int idx = _knownSkills.FindIndex(s => s.SkillId == skillId);
                        if (idx >= 0)
                        {
                            SkillBundle bundle = _knownSkills[idx];
                            if (bundle.Levels == null)
                            {
                                bundle.Levels = new Dictionary<string, int>();
                            }
                            bundle.Levels["__base__"] = MaxSkillLevel;
                            _knownSkills[idx] = bundle;
                        }
                        else
                        {
                            _knownSkills.Add(new SkillBundle
                            {
                                Category = category,
                                SkillId = skillId,
                                Levels = new Dictionary<string, int> { { "__base__", MaxSkillLevel } }
                            });
                        }
                        granted++;
                    }
                    MaxOutProficiencies();
                    MarkDirty();
                    SendSkills();
                    SendUnlockedRecipesAndBlueprints();
                    RefreshAbilities();
                    SendStatistics();
                    SendCheatReply($"Level karakter dimaksimalkan ke {Level} + seluruh ({granted}) skill dan seluruh profisiensi kategori (Lv. 60) terbuka penuh!", header);
                }
                break;
            case "maxprof":
            case "max prof":
            case "maxproficiency":
                {
                    MaxOutProficiencies();
                    MarkDirty();
                    SendSkills();
                    SendUnlockedRecipesAndBlueprints();
                    RefreshAbilities();
                    SendStatistics();
                    SendCheatReply("Seluruh kategori profisiensi keahlian dimaksimalkan ke Lv. 60!", header);
                }
                break;
            // แบ็กอัพเซฟเดี๋ยวนี้ (ปกติทำเองทุก 4 ชม. ตาม config → Save.BackupIntervalHours)
            case "backup":
            {
                _world.SaveAll(force: true);
                string path = SaveBackup.RunOnce("cheat");
                SendCheatReply(path == null ? "Backup gagal — periksa log server" : "Backup berhasil: " + path, header);
                break;
            }
            // ระบบป่วย — ทดสอบผลของสถานะป่วย (คราฟต์ช้า/เปลืองแรง/ล้าไว/เดินช้า)
            case "sick":
                SendCheatReply(MakeSick("cheat") ? "Karakter sekarang terkena penyakit" : "Karakter sudah sakit atau sistem penyakit nonaktif", header);
                break;
            case "cure":
                SendCheatReply(CureSickness() ? "Karakter telah sembuh dari penyakit" : "Karakter sedang tidak sakit", header);
                break;
            case "add bonfire":
            case "add_bonfire":
                lock (_inventory)
                {
                    _inventory.Add(MakeCapsuleItem("capsulated_bonfire", "Api Unggun", "furniture_workbench_bonfire"));
                }
                SendInventory();
                SendCheatReply("Mendapatkan Api Unggun (Bonfire) x1", header);
                break;
            // [แก้เอง] 25 ส.ค. 2026 — ไว้เทส TryStartResting/IsRestBlueprint จริงโดยไม่ต้องเดินไปหา
            // กองไฟที่มีอยู่ในโลก (วางที่ตำแหน่งปัจจุบันตรงๆ ข้ามขั้นตอนคลิกวางของผู้เล่น)
            case "place real fire":
            case "place_real_fire":
            {
                const string blueprintId = "camp_square_fire";
                if (!RecipeData.BlueprintType.TryGetValue(blueprintId, out ushort entityType))
                {
                    SendCheatReply($"Data blueprint '{blueprintId}' tidak ditemukan.", header);
                    break;
                }
                Point2 tile = new Point2((int)(CurrentPosition.x / 200f), (int)(CurrentPosition.y / 200f));
                Point2 size = RecipeData.BlueprintSize.TryGetValue(blueprintId, out var bpSize)
                    ? new Point2(bpSize.x, bpSize.y) : new Point2(1, 1);
                string entityId = Guid.NewGuid().ToString();
                AppearArtifact placed = ArtifactFactory.Make(EntityId, entityId, entityType, tile, size,
                    default, null, 1, blueprintId, BuildingState.Completed);
                _world.AddArtifact(placed, blueprintId);
                _world.AnnounceArtifact(placed);
                SendCheatReply($"Meletakkan api unggun di tile {tile.x},{tile.y}", header);
                break;
            }
            // ใช้ตรวจ render ของ blueprint ที่ประกอบจากหลาย slot โดยตรง
            // (คำสั่งทดสอบเท่านั้น ไม่ใช่เส้นทางเล่นจริง)
            case "place real tent":
            case "place_real_tent":
            {
                if (!AllowFreeBuild)
                {
                    SendCheatReply("Mode bangunan bebas nonaktif — aktifkan CraftMenu.AllowFreeBuild untuk pengujian", header);
                    break;
                }
                const string blueprintId = "tent";
                if (!RecipeData.BlueprintType.TryGetValue(blueprintId, out ushort entityType))
                {
                    SendCheatReply($"Data blueprint '{blueprintId}' tidak ditemukan.", header);
                    break;
                }
                Point2 tile = new Point2((int)(CurrentPosition.x / 200f), (int)(CurrentPosition.y / 200f));
                if (_world.HasArtifactAt(tile))
                {
                    tile = new Point2(tile.x + 1, tile.y);
                }
                Point2 size = RecipeData.BlueprintSize.TryGetValue(blueprintId, out var bpSize)
                    ? new Point2(bpSize.x, bpSize.y) : new Point2(1, 1);
                string entityId = Guid.NewGuid().ToString();
                AppearArtifact placed = ArtifactFactory.Make(EntityId, entityId, entityType, tile, size,
                    default, null, 1, blueprintId, BuildingState.Completed);
                _world.AddArtifact(placed, blueprintId);
                _world.AnnounceArtifact(placed);
                SendCheatReply($"Meletakkan tenda di tile {tile.x},{tile.y}", header);
                break;
            }
            // เฟส C — ของสำหรับทดสอบระบบสวมใส่
            case "add axe":
            case "add_axe":
                GiveEquipTestItem("axe_onehand_stone_01", "Kapak Batu", "weapon_axe_onehand_stone_2", header.Seq);
                break;
            case "add stone":
            case "add_stone":
                // หิน 1 ก้อน — วัตถุดิบของสูตร blade_stone (มีด) ไว้เทสสายเครื่องมือ
                GiveEquipTestItem("stone", "Batu", "icon_nat_stone", header.Seq);
                break;
            case "add knife":
            case "add_knife":
                // มีดหิน — ของจริงคราฟต์เองได้จากหิน (สูตร blade_stone) นี่เป็นทางลัดตอนเทส
                GiveEquipTestItem("blade_stone", "Pisau Batu", "icon_nat_blade_stone", header.Seq);
                break;
            case "add pickaxe":
            case "add_pickaxe":
                GiveEquipTestItem("pickaxe_wooden_01", "Beliung Kayu", "weapon_pickaxe_wooden", header.Seq);
                break;
            case "add clothes":
            case "add_clothes":
                GiveEquipTestItem("clothes_builder_01", "Pakaian Tukang", "clothes_builder_01", header.Seq);
                break;
            // เฟส C — กล่องเก็บของ
            case "add box":
            case "add_box":
                lock (_inventory)
                {
                    _inventory.Add(MakeCapsuleItem("capsulated_fur_box_03_leaf", "Kotak Daun", "furniture_box"));
                }
                MarkDirty();
                SendInventory();
                SendCheatReply("Mendapatkan Kotak Daun (Leaf Box) x1 — Letakkan di tanah untuk menyimpan barang", header);
                break;

            // เฟส C — ทดสอบค่าสถานะ
            case "survival":
                SendCheatReply($"Darah {CurrentLife:F0}/{LifeMax:F0} · Stamina {_stamina.ValueAt(Times.UnixTimeNow()):F0}/{StaminaMax:F0} · Kelelahan {_fatigue.ValueAt(Times.UnixTimeNow()):F0}/{FatigueMax:F0}", header);
                break;
            case "rest":
                RestoreSurvival(clearFatigue: true);
                SendCheatReply("Istirahat selesai — Darah dan stamina penuh, kelelahan 0.", header);
                break;
            // [แก้เอง] 25 ส.ค. 2026 — ทดสอบ TryStartResting จริง (ต้องมีกองไฟ/เต็นท์จริงในระยะเอื้อม
            // ไม่ได้ตั้งค่าตรงๆ เหมือน "rest" — ไว้เช็คว่า IsRestBlueprint จับ blueprint จริงในโลกได้ไหม)
            case "test rest":
            case "test_rest":
                SendCheatReply(TryStartResting(null), header);
                break;
            case "tired":
                SetGaugeValue("stamina", 0f);
                SendCheatReply("Stamina diatur ke 0 (Coba ambil barang sekarang untuk tes penolakan — pulih 4/dtk).", header);
                break;
            case "hurt":
                bool dead = ApplyDamage(30f);
                if (dead)
                {
                    Die();          // เฟส C รอบ 2: บอกทุกคนว่าล้มแล้ว
                }
                SendCheatReply($"Menerima 30 damage. Sisa darah: {CurrentLife:F0}{(dead ? " — Karakter mati!" : "")}", header);
                break;
            case "spawn":
            case "spawn animal":
            {
                // เรียกสัตว์มาเกิดตรงที่ยืนอยู่ — สัตว์ปกติกระจายในรัศมี 30 tile ซึ่งมักอยู่นอกจอ
                ServerAnimal born = _world.Animals.SpawnAt(CurrentPosition);
                SendCheatReply($"Memunculkan hewan type {born.EntityType} Lv.{born.Level} di dekatmu [id={born.EntityId}]", header);
                break;
            }
            case "die":
                SetGaugeValue("life", 0f);
                Die();
                SendCheatReply("Karakter mati — Gunakan /heal atau tombol Revive untuk bangkit kembali.", header);
                break;
            case "kill animal":
            case "kill_animal":
            {
                // ฆ่าสัตว์ตัวที่ใกล้ที่สุดทันที — ไว้เทสการแล่เนื้อโดยไม่ต้องยืนตีเป็นนาที
                ServerAnimal[] all = _world.Animals.Snapshot();
                ServerAnimal nearest = null;
                float best = float.MaxValue;
                WorldPosition me = CurrentPosition;
                for (int i = 0; i < all.Length; i++)
                {
                    if (!all[i].IsAlive)
                    {
                        continue;
                    }
                    float ddx = all[i].Position.x - me.x;
                    float ddy = all[i].Position.y - me.y;
                    float d2 = ddx * ddx + ddy * ddy;
                    if (d2 < best)
                    {
                        best = d2;
                        nearest = all[i];
                    }
                }
                if (nearest == null)
                {
                    SendCheatReply("Tidak ada dinosaurus atau hewan hidup di sekitar.", header);
                    break;
                }
                _world.Animals.Damage(nearest.EntityId, nearest.LifeMax * 2f, EntityId);
                SendCheatReply($"Berhasil membunuh {nearest.EntityId} (type {nearest.EntityType} Lv.{nearest.Level}) di jarak {MathF.Sqrt(best) / 200f:F1} tile — Sentuh bangkai untuk butchering", header);
                break;
            }
            // ดูความทนทานของเครื่องมือที่ถืออยู่ — ไว้เทสว่าหลอดลดจริงไหมโดยไม่ต้องเปิด UI
            case "tools":
            {
                var lines = new List<string>();
                lock (_inventory)
                {
                    for (int i = 0; i < _inventory.Count; i++)
                    {
                        Item it = _inventory[i];
                        if (!ToolDurability.HasDurability(it))
                        {
                            continue;
                        }
                        float max = ToolDurability.MaxOf(it);
                        lines.Add($"{it.Name} ({it.Prototype}) Material Tier {ToolDurability.TierOf(it.Prototype)} — {ToolDurability.RemainingOf(it):F0}/{max:F0}");
                    }
                }
                ToolConfig tc = ServerConfig.Current.Tools;
                string head = tc.Enabled
                    ? $"Sistem Durability: Aktif (Basis {tc.DurabilityBase:F0} + {tc.DurabilityPerTier:F0}/tier · Berkurang {tc.WearPerUse:F0} per pakai)"
                    : "Sistem Durability: Nonaktif (Tools.Enabled = false)";
                SendCheatReply(lines.Count == 0
                    ? head + "\nTidak ada peralatan di dalam tas."
                    : head + "\n" + string.Join("\n", lines), header);
                break;
            }

            // เททิ้งทั้งกระเป๋า — มีไว้ให้ชุดทดสอบเรียกตอนเริ่ม
            // ไม่งั้นบอทชื่อเดิม (เช่น gp-check-1) สะสมของทุกรอบจนกระเป๋าเต็ม
            // แล้วข้อที่ต้อง "เก็บของได้จริง" จะตกทั้งที่โค้ดถูก (เคยหลงแก้ผิดจุดมาแล้ว)
            case "clearbag":
            case "clear bag":
            {
                int before;
                lock (_inventory)
                {
                    before = _inventory.Count;
                    _inventory.Clear();
                }
                MarkDirty();
                SendInventory();
                SendCheatReply($"Berhasil mengosongkan {before} item dari tas inventory.", header);
                break;
            }
            // ล้าเต็มหลอด — ใช้เทสว่าเลือดไหลลงจนตายจริงไหม
            case "burnout":
                SetGaugeValue("fatigue", ServerConfig.Current.Survival.FatigueMax);
                SendCheatReply($"Kelelahan diatur ke {ServerConfig.Current.Survival.FatigueMax:F0} (Maksimal) — Darah akan mulai berkurang.", header);
                break;
            case "exhaust":
                SetGaugeValue("fatigue", 90f);
                SendCheatReply("Kelelahan diatur ke 90 (Melebihi batas bahaya 85 → Konsumsi stamina x2).", header);
                break;
            case "reload gather":
            case "reload gathering":
            {
                string loaded = GatheringTools.ReloadNow();
                int cleared = _world.ForgetNaturalGeneratorCache();
                SendCheatReply(loaded + $" · Membersihkan cache titik pengumpulan ({cleared} titik).", header);
                break;
            }
            default:
            {
                // [แก้เอง] 24 ส.ค. 2026 — ระบบ mod: verb ที่ไม่ตรงกับคำสั่งในตัวสักอัน ให้ลองส่งต่อ
                // ให้ mod ที่ลงทะเบียนไว้ก่อนค่อยยอมแพ้เป็น "unknown cheat" (ดู PluginManager.cs)
                // [แก้เอง] 3 ก.ย. 2026 — ลองตีความด้วยภาษาของมาโครเกมต้นฉบับก่อน
                // (it / set level / sc ...) ไม่งั้นสั่งมาโครจากหน้าแอดมินแล้วขึ้น unknown cheat รัว
                // ดู ServerPlayer.CheatMacroCompat.cs
                if (TryGameMacroCheat(raw, header))
                {
                    break;
                }

                string[] parts = raw.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                string verb = parts.Length > 0 ? parts[0] : string.Empty;
                string[] modArgs = parts.Length > 1 ? parts[1..] : Array.Empty<string>();
                if (PluginManager.Instance != null && PluginManager.Instance.TryRunCommand(verb, this, modArgs, out string modReply))
                {
                    SendCheatReply(modReply, header);
                }
                else
                {
                    SendCheatReply("Perintah tidak dikenal: " + cmd + " (Ketik /help untuk daftar perintah)", header);
                }
                break;
            }
        }
    }

    /// <summary>
    /// รีโมทคุมตัวละครของผู้เล่นอีกคน — <c>control &lt;ชื่อ|entityId&gt; &lt;คำสั่ง&gt; [args]</c>
    /// ใช้ขับตัวละครที่ล็อกอินอยู่ในตัวเกมจริงด้วย packet ล้วน (ดู ServerPlayer.RemoteControl.cs)
    /// </summary>
    private void HandleControl(string raw, PacketHeader header)
    {
        // H-2: control ยุ่งกับตัวละครของคนอื่นได้ (ลากไปไหนก็ได้ · พูดแทน · บังคับตีสัตว์)
        // จึงต้องเป็น admin เท่านั้น — ไม่ได้ตั้ง --admin ไว้ = ใช้ไม่ได้เลย
        if (!IsAdmin)
        {
            Console.WriteLine($"[control] Ditolak {Name} ({EntityId}): Bukan admin");
            SendCheatReply("Perintah control hanya dapat digunakan oleh admin (atur dengan --admin <nama|entityId> saat menjalankan server).", header);
            return;
        }
        string[] a = raw.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        if (a.Length < 3)
        {
            SendCheatReply("Gunakan: /control <nama|id> <tp|walk|stop|gather|attack|craft|eat|place|bag|prof|spawn|kill|heal|give|travel|say|status> [args]", header);
            return;
        }
        ServerPlayer target = _world.FindPlayerByNameOrId(a[1]);
        if (target == null)
        {
            SendCheatReply($"Pemain '{a[1]}' tidak ditemukan atau sedang offline.", header);
            return;
        }
        string verb = a[2].ToLower();
        string reply;
        switch (verb)
        {
            case "tp":
            case "walk":
            {
                if (a.Length < 5 || !int.TryParse(a[3], out int tx) || !int.TryParse(a[4], out int ty))
                {
                    reply = $"Gunakan: control {a[1]} {verb} <tileX> <tileY>";
                    break;
                }
                if (verb == "tp")
                {
                    target.ControlTeleport(tx, ty);
                    reply = $"Memindahkan {target.Name} ke tile {tx},{ty}";
                }
                else
                {
                    target.ControlWalk(tx, ty);
                    reply = $"Memerintahkan {target.Name} berjalan ke tile {tx},{ty}";
                }
                break;
            }
            case "stop":
                target.ControlStop();
                reply = $"{target.Name} berhenti berjalan.";
                break;
            case "gather":
                reply = target.ControlGather();
                break;
            case "attack":
                reply = target.ControlAttackNearest();
                break;
            // ── เครื่องมือตอนเล่นเอง: สั่งจากข้างนอกให้เกิดอะไรขึ้นตรงหน้าตัวละครในเกม ──
            case "spawn":
            {
                ushort want = 0;
                if (a.Length >= 4)
                {
                    ushort.TryParse(a[3], out want);
                }
                reply = target.ControlSpawn(want);
                break;
            }
            case "kill":
                reply = target.ControlKillNearest();
                break;
            case "heal":
                reply = target.ControlHeal();
                break;
            case "abilities":
            case "stats":
                reply = target.DescribeAbilities();
                break;
            case "quests":
                reply = target.DescribeQuests();
                break;
            case "travel":
                reply = a.Length >= 4 ? target.TravelTo(a[3]) : "Gunakan: control <nama> travel <id_pulau>";
                break;
            case "give":
                reply = target.ControlGive(a.Length >= 4 ? a[3].ToLower() : "");
                break;
            case "say":
            {
                string text = raw.Substring(raw.IndexOf(" say ", StringComparison.OrdinalIgnoreCase) + 5);
                target.ControlSay(text);
                reply = $"{target.Name} berkata: {text}";
                break;
            }
            case "status":
                reply = target.ControlStatus();
                break;
            // ── สั่งให้ตัวละคร "เล่นเกม" จากข้างนอก (ServerPlayer.RemoteDrive) ──
            case "go":
            {
                // เดินแบบนับจากที่ยืนอยู่ — สคริปต์เทสใช้อันนี้ ไม่ใช่ walk ที่เป็นพิกัดตายตัว
                if (a.Length < 5 || !int.TryParse(a[3], out int gx) || !int.TryParse(a[4], out int gy))
                {
                    reply = $"Gunakan: control {a[1]} go <dx> <dy>";
                    break;
                }
                reply = target.ControlGoRelative(gx, gy);
                break;
            }
            case "craft":
                reply = target.ControlCraft(a.Length >= 4 ? a[3] : null);
                break;
            case "eat":
                reply = target.ControlEat(a.Length >= 4 ? a[3] : null);
                break;
            case "place":
                reply = target.ControlPlace(a.Length >= 4 ? a[3] : null);
                break;
            case "bag":
                reply = target.ControlBag();
                break;
            case "prof":
            case "proficiency":
                reply = target.ControlProficiency();
                break;
            default:
                reply = $"Perintah '{verb}' tidak dikenal (tp/walk/stop/gather/attack/craft/eat/place/bag/prof/give/heal/kill/spawn/say/status)";
                break;
        }
        Console.WriteLine("[control] {0} memerintahkan {1}: {2}", Name, target.Name, reply);
        SendCheatReply(reply, header);
    }

    /// <summary>เฟส C — สร้างไอเทมที่ใส่ได้จริงสำหรับทดสอบ (prototype ต้องมีใน EquipData)</summary>
    private void GiveEquipTestItem(string prototype, string name, string icon, uint replyOf)
    {
        Item item = MakeGatheredItem(new Generator
        {
            Id = prototype,
            Name = name,
            Icon = icon
        });
        lock (_inventory)
        {
            _inventory.Add(item);
        }
        MarkDirty();
        SendInventory();
        bool known = EquipData.Weapons.ContainsKey(prototype) || EquipData.Armors.ContainsKey(prototype);
        SendCheatReply($"Mendapatkan {name} x1 (prototype={prototype}, model: {(known ? "Dikenal" : "Tidak")})", new PacketHeader { Seq = replyOf });
    }

    /// <summary>
    /// เทสบัฟ/ดีบัฟจากอาหาร (status effect) ให้มีผลจริง — ติดบัฟตรง ๆ โดยไม่ต้องหาไอเทมที่ให้บัฟนั้น
    /// (ดู ServerPlayer.Group2 ว่าบัฟไหนกระทบอะไร) · "clear" = ล้างบัฟทั้งหมด
    /// </summary>
    private string CheatApplyEffect(string arg)
    {
        string effId = arg.Trim();
        float seconds = 60f;
        int sp = effId.IndexOf(' ');
        if (sp > 0)
        {
            if (float.TryParse(effId.Substring(sp + 1).Trim(), out float s) && s > 0f) seconds = s;
            effId = effId.Substring(0, sp).Trim();
        }
        if (effId.Length == 0 || effId == "clear")
        {
            _statusEffects.Clear();
            MarkDirty();
            SendStatusEffects();
            return "Seluruh efek status / buff telah dibersihkan.";
        }
        double now = Durango.Utils.Times.UnixTimeNow();
        _statusEffects.RemoveAll(x => x.Id == "food:" + effId || x.EffectId == effId);
        _statusEffects.Add(new StatusEffectSave
        {
            Id = "food:" + effId,
            EffectId = effId,
            Level = 1,
            Since = now,
            Until = now + seconds,
            Enabled = true
        });
        MarkDirty();
        SendStatusEffects();
        return $"Mendapatkan status/buff '{effId}' selama {seconds:F0} detik.";
    }

    /// <summary>
    /// วางสิ่งปลูกสร้างสถานะ Completed ตรง ๆ (คำสั่งทดสอบ) แล้วรายงานโมเดล (Parts) ที่ส่งให้ client
    /// Parts ว่าง = client จะโชว์แค่นั่งร้าน ⇒ ใช้ยืนยันว่าโมเดลของ blueprint นั้นถูกต้องแล้ว
    /// </summary>
    private string PlaceCompleted(string blueprintId)
    {
        // ไม่ต้องพึ่ง CraftMenu.AllowFreeBuild — ทั้งช่อง cheat ถูกกันด้วย --enable-cheat อยู่แล้ว
        // (เหมือน give/spawn/maxskills) และคำสั่งนี้ใช้ตรวจโมเดลอย่างเดียว
        if (string.IsNullOrEmpty(blueprintId)) { return "Gunakan: /cheat place real <blueprintId>"; }
        if (!RecipeData.BlueprintType.TryGetValue(blueprintId, out ushort entityType))
        {
            return $"Data blueprint '{blueprintId}' tidak ditemukan.";
        }
        Point2 tile = new Point2((int)(CurrentPosition.x / 200f), (int)(CurrentPosition.y / 200f));
        for (int i = 0; i < 8 && _world.HasArtifactAt(tile); i++) { tile = new Point2(tile.x + 1, tile.y); }
        Point2 size = RecipeData.BlueprintSize.TryGetValue(blueprintId, out var bpSize)
            ? new Point2(bpSize.x, bpSize.y) : new Point2(1, 1);
        AppearArtifact placed = ArtifactFactory.Make(EntityId, Guid.NewGuid().ToString(), entityType, tile, size,
            default, null, 1, blueprintId, BuildingState.Completed);
        _world.AddArtifact(placed, blueprintId);
        _world.AnnounceArtifact(placed);
        int n = placed.Display.Parts?.Count ?? 0;
        string parts = n == 0 ? "(kosong — client akan menampilkan scaffolding!)" : string.Join(", ", placed.Display.Parts);
        return $"Meletakkan '{blueprintId}' (Selesai) di tile {tile.x},{tile.y} · Model {n} bagian: {parts}";
    }

    private void MaxOutProficiencies()
    {
        int[] gates = { 20, 25, 30, 35, 40, 45, 50, 55, 59 };
        for (int i = 0; i < AllSkillCategories.Length; i++)
        {
            Shared.Skill.Category cat = AllSkillCategories[i];
            foreach (int gate in gates)
            {
                _completedCategoryResearch.Add(ResearchKey(cat, gate));
            }
            if (SkillCategoryData.TryGet(cat, out SkillCategoryData.Curve curve))
            {
                int sum = 0;
                for (int k = 0; k < curve.ExpNeeded.Length; k++)
                {
                    sum += curve.ExpNeeded[k];
                }
                _categoryExp[cat] = sum;
            }
            else
            {
                _categoryExp[cat] = 999999;
            }
        }
    }

    public bool SpawnAllroundWorkbench(out string reply)
    {
        const string blueprintId = "allround";
        if (!RecipeData.BlueprintType.TryGetValue(blueprintId, out ushort entityType))
        {
            entityType = 8012;
        }
        Point2 tile = new Point2((int)(CurrentPosition.x / 200f), (int)(CurrentPosition.y / 200f));
        for (int i = 0; i < 8 && _world.HasArtifactAt(tile); i++) { tile = new Point2(tile.x + 1, tile.y); }
        Point2 size = RecipeData.BlueprintSize.TryGetValue(blueprintId, out var bpSize)
            ? new Point2(bpSize.x, bpSize.y) : new Point2(1, 1);
        string entityId = Guid.NewGuid().ToString();
        AppearArtifact placed = ArtifactFactory.Make(EntityId, entityId, entityType, tile, size,
            default, null, 60, blueprintId, BuildingState.Completed);
        _world.AddArtifact(placed, blueprintId);
        _world.AnnounceArtifact(placed);
        reply = $"Meja Serbaguna (All-Round Workbench Lv.60) dimunculkan di tile {tile.x},{tile.y}!";
        return true;
    }

    public bool RemoveNearbyWorkbenches(out string reply)
    {
        Point2 center = new Point2((int)(CurrentPosition.x / 200f), (int)(CurrentPosition.y / 200f));
        int removed = 0;
        foreach (var art in _world.SnapshotArtifacts())
        {
            if (_world.TryGetArtifactBlueprint(art.EntityId, out string bp) && bp == "allround"
                && Math.Abs(art.Tile.x - center.x) <= 10 && Math.Abs(art.Tile.y - center.y) <= 10)
            {
                _world.RemoveArtifact(art.EntityId);
                _world.AnnounceGone(art.EntityId);
                removed++;
            }
        }
        reply = $"Dibersihkan {removed} Meja Serbaguna di sekitarmu.";
        return true;
    }

    private void GiveCraftingKit(PacketHeader header)
    {
        var kitItems = new (string proto, string name, string icon, int count, int lv)[]
        {
            ("axe_tool_metal_01", "Work Axe Logam", "tool_axe_L01", 1, 60),
            ("sword_tool_metal_01", "Work Knife Logam", "tool_knife_L01", 1, 60),
            ("hammer_onehand_metal_01", "Palu Logam", "hammer_onehand_metal_01_blade", 1, 60),
            ("saw_metal_01", "Gergaji Logam", "weapon_saw_metal", 1, 60),
            ("stone", "Batu", "icon_nat_mine_stone", 5, 60),
            ("branch", "Ranting", "icon_nat_wood_branch", 5, 60),
            ("log", "Batang Kayu", "icon_nat_wood_log", 5, 60),
            ("bone_piece", "Tulang", "icon_nat_bone_piece", 5, 60),
            ("blade_axe_stone_01", "Bilah Kapak Batu", "axe_twohand_stone_01_blade", 2, 60),
            ("string_leather", "Tali Kulit", "material_string_leather", 5, 60),
            ("leather_01", "Kulit Olahan", "material_leather_01", 5, 60),
            ("ingot_iron_01", "Batang Besi", "material_ingot_iron", 5, 60),
            ("leaf", "Daun", "icon_nat_leaf", 5, 60),
            ("mud", "Tanah Liat", "icon_nat_mud", 5, 60)
        };

        int room = FreeInventorySlots();
        if (room <= 0)
        {
            SendCheatReply("Tas inventory penuh! Kosongkan sebagian slot tas terlebih dahulu.", header);
            return;
        }

        int added = 0;
        lock (_inventory)
        {
            foreach (var it in kitItems)
            {
                if (_inventory.Count >= PlayerInventoryMaxSize) break;
                for (int i = 0; i < it.count && _inventory.Count < PlayerInventoryMaxSize; i++)
                {
                    _inventory.Add(MakeCraftedItem(it.proto, it.name, it.icon, it.lv));
                    added++;
                }
            }
        }
        MarkDirty();
        SendInventory();
        SendCheatReply($"Berhasil menambahkan {added} alat & bahan crafting ke tas!\n(Work Axe, Work Knife, Palu, Gergaji, Batu, Kayu, Tulang, Tali, Besi Lv. 60)\nSekarang kamu bisa gunakan tombol 'Auto Fill' di menu Craft!", header);
    }

    private void HandleDirectCraft(string args, PacketHeader header)
    {
        string[] parts = args.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            SendCheatReply("Gunakan: /craft <nama_resep|prototype> [jumlah] [level]\nContoh: /craft blade_bone 1 60\nAtau: /craft all (buka semua resep)", header);
            return;
        }
        string query = parts[0];
        int count = parts.Length >= 2 && int.TryParse(parts[1], out int c) ? Math.Clamp(c, 1, 99) : 1;
        int level = parts.Length >= 3 && int.TryParse(parts[2], out int lv) ? Math.Clamp(lv, 1, 60) : Math.Max(1, Level);

        string prototype = null;
        string displayName = null;
        string icon = null;

        // 1. Exact recipe
        if (RecipeMeta.Map.TryGetValue(query, out RecipeMeta.Info meta))
        {
            prototype = ResolveOutputPrototype(query, meta, null);
            if (RecipeData.RecipeInfo.TryGetValue(query, out var rInfo))
            {
                displayName = rInfo.name;
                icon = rInfo.icon;
            }
        }
        else
        {
            // Substring or case-insensitive search in RecipeMeta
            foreach (var kvp in RecipeMeta.Map)
            {
                if (kvp.Key.Equals(query, StringComparison.OrdinalIgnoreCase) || kvp.Key.Contains(query, StringComparison.OrdinalIgnoreCase))
                {
                    prototype = ResolveOutputPrototype(kvp.Key, kvp.Value, null);
                    if (RecipeData.RecipeInfo.TryGetValue(kvp.Key, out var rInfo))
                    {
                        displayName = rInfo.name;
                        icon = rInfo.icon;
                    }
                    break;
                }
            }
        }

        // 2. Prototype or common alias
        if (prototype == null)
        {
            string lower = query.ToLowerInvariant();
            switch (lower)
            {
                case "axe": prototype = "axe_onehand_stone_01"; break;
                case "workaxe":
                case "work_axe": prototype = "axe_tool_bone_01"; break;
                case "knife":
                case "blade": prototype = "blade_bone"; break;
                case "workknife":
                case "work_knife": prototype = "sword_tool_bone_01"; break;
                case "bow": prototype = "bow_wooden_01"; break;
                case "crossbow": prototype = "crossbow_wooden_01"; break;
                case "hammer": prototype = "hammer_onehand_stone_01"; break;
                case "spear":
                case "lance": prototype = "lance_twohand_stone_01"; break;
                case "pick":
                case "pickaxe": prototype = "pick_stone_01"; break;
                case "saw": prototype = "saw_metal_01"; break;
                case "clothes": prototype = "clothes_builder_01"; break;
                case "tent": prototype = "capsulated_tent"; break;
                case "bonfire": prototype = "capsulated_bonfire"; break;
                default:
                    if (ItemNameData.Map.ContainsKey(query))
                    {
                        prototype = query;
                    }
                    else
                    {
                        foreach (var k in ItemNameData.Map.Keys)
                        {
                            if (k.Equals(query, StringComparison.OrdinalIgnoreCase) || k.Contains(query, StringComparison.OrdinalIgnoreCase))
                            {
                                prototype = k;
                                break;
                            }
                        }
                    }
                    break;
            }
        }

        if (prototype == null)
        {
            SendCheatReply($"Resep atau item '{query}' tidak ditemukan. Contoh pemakaian: /craft blade_bone atau /craft stone_work_axe", header);
            return;
        }

        displayName = ItemNameData.NameOf(prototype, displayName ?? prototype);
        icon = ItemNameData.IconOf(prototype, icon ?? string.Empty);

        int room = FreeInventorySlots();
        if (room <= 0)
        {
            SendCheatReply("Tas inventory penuh! Buang atau simpan beberapa item sebelum crafting.", header);
            return;
        }

        int give = Math.Clamp(count, 1, room);
        lock (_inventory)
        {
            for (int i = 0; i < give; i++)
            {
                _inventory.Add(MakeCraftedItem(prototype, displayName, icon, level));
            }
        }
        MarkDirty();
        SendInventory();

        string reply = $"Berhasil craft instan: {displayName} x{give} (Lv.{level}, proto={prototype}) ke tas!";
        if (give < count)
        {
            reply += $" (Diminta {count}, tetapi slot tas hanya tersisa {room})";
        }
        SendCheatReply(reply, header);
    }
}
