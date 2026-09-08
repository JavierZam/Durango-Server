using System;
using System.Collections.Generic;
using System.Linq;
using Durango.Network;
using Durango.Utils;
using Messages;
using Shared.Ability;
using Shared.Animal;
using Shared.Display;

namespace DurangoServer.Core;

public partial class ServerPlayer
{
    private readonly List<PetSave> _pets = new List<PetSave>();
    private PetSave _spawnedPet;

    private WorldPosition _petPosition;
    private float _petYaw;
    private float _petHeight;
    private WorldPosition _petFrom;
    private WorldPosition _petTo;
    private double _petMoveStartAt;
    private double _petMoveEndAt;
    private double _lastPetFollowTickAt;
    private bool _petStayMode;

    public IReadOnlyList<PetSave> Pets => _pets;
    public PetSave SpawnedPet => _spawnedPet;
    public bool PetStayMode => _petStayMode;

    private static ushort GetPetMotionType(ushort petEntityType)
    {
        PetTemplate tmpl = PetData.FindByEntityType(petEntityType);
        if (tmpl != null && tmpl.VehicleEntityType > 0)
        {
            return tmpl.VehicleEntityType;
        }
        return petEntityType;
    }

    public WorldPosition PetPositionAt(double now)
    {
        if (_spawnedPet == null) return CurrentPosition;
        if (_spawnedPet.IsBoarding) return CurrentPosition;
        if (_petMoveEndAt <= _petMoveStartAt || now >= _petMoveEndAt)
        {
            return _petTo;
        }
        if (now <= _petMoveStartAt)
        {
            return _petFrom;
        }
        float t = (float)((now - _petMoveStartAt) / (_petMoveEndAt - _petMoveStartAt));
        return new WorldPosition(
            _petFrom.x + (_petTo.x - _petFrom.x) * t,
            _petFrom.y + (_petTo.y - _petFrom.y) * t);
    }

    private void ApplyPetSave(PlayerSave save)
    {
        _pets.Clear();
        _spawnedPet = null;
        _petStayMode = false;
        if (save?.Pets != null)
        {
            foreach (var p in save.Pets)
            {
                if (p != null && !string.IsNullOrEmpty(p.EntityId))
                {
                    _pets.Add(p);
                    if (p.IsSpawned)
                    {
                        _spawnedPet = p;
                        _petStayMode = p.StayMode;
                        if (p.PosX != 0f || p.PosY != 0f)
                        {
                            _petPosition = new WorldPosition(p.PosX, p.PosY);
                            _petYaw = p.Yaw;
                        }
                        else
                        {
                            _petPosition = CurrentPosition;
                            _petYaw = CurrentYaw;
                        }
                        _petFrom = _petPosition;
                        _petTo = _petPosition;
                    }
                }
            }
        }
    }

    private void FillPetSave(PlayerSave save)
    {
        if (save == null) return;
        save.Pets.Clear();
        if (_spawnedPet != null)
        {
            _spawnedPet.StayMode = _petStayMode;
            _spawnedPet.PosX = _petPosition.x;
            _spawnedPet.PosY = _petPosition.y;
            _spawnedPet.Yaw = _petYaw;
        }
        foreach (var p in _pets)
        {
            if (p != null)
            {
                save.Pets.Add(p);
            }
        }
    }

    public Messages.Pet ConvertToPetMessage(PetSave pet)
    {
        double now = Times.UnixTimeNow();
        return new Messages.Pet
        {
            EntityId = pet.EntityId,
            EntityType = pet.EntityType,
            TamerEntityId = EntityId,
            Name = pet.Name ?? string.Empty,
            Rank = (PetRank)pet.Rank,
            Generation = 1,
            IsBoarding = pet.IsBoarding,
            IsSpawned = pet.IsSpawned,
            Stat = new PetStats
            {
                PlaybackRate = 1f,
                Size = 1,
                Taste = string.Empty,
                InventoryUsage = 0,
                Life = new Gauge(pet.LifeMax, 0f, new[] { new GaugeNode { Time = now, Value = pet.Life } }),
                Hungry = new Gauge(pet.HungryMax, 0f, new[] { new GaugeNode { Time = now, Value = pet.Hungry } }),
                EatableTags = new[] { "food" },
                Tags = new Dictionary<string, int>(),
                LastMilestoneAccepted = true,
                RetryCost = null,
                IsOld = false,
                AgingSince = 0,
                AgingUntil = 0,
                GrazedAt = null
            },
            Statistics = new PetStatistics
            {
                Level = Math.Max(1, pet.Level),
                Exp = pet.Exp,
                RequiredExp = Math.Max(100, pet.Level * 100),
                DerivedAbilities = new Dictionary<Derived, float>(),
                MilestonesInformation = Array.Empty<MilestoneInfo>(),
                AvailableActiveSkill = Array.Empty<PetActiveSkill>()
            },
            CageInfo = null
        };
    }

    public AppearPet MakeAppearPet(PetSave pet)
    {
        double now = Times.UnixTimeNow();
        ushort motionType = GetPetMotionType(pet.EntityType);
        string motionName = AnimalMotionData.Stand(motionType) ?? "idle";
        WorldPosition pos = pet.IsBoarding ? CurrentPosition : (_petPosition.x != 0f || _petPosition.y != 0f ? _petPosition : CurrentPosition);
        float yaw = pet.IsBoarding ? CurrentYaw : _petYaw;

        return new AppearPet
        {
            EntityId = pet.EntityId,
            EntityType = pet.EntityType,
            IsAlive = true,
            Move = new Move
            {
                EntityId = pet.EntityId,
                Movements = new[]
                {
                    new Movement
                    {
                        MotionName = motionName,
                        MotionOption = 5,
                        PlaybackRate = 1f,
                        RotSpeed = 540f,
                        Path = new[]
                        {
                            new Location
                            {
                                Position = pos,
                                Yaw = yaw,
                                Time = now,
                                Floor = 0,
                                Height = _petHeight
                            }
                        }
                    }
                }
            },
            Survival = new Survival
            {
                EntityId = pet.EntityId,
                Life = new Gauge(pet.LifeMax, 0f, new[] { new GaugeNode { Time = now, Value = pet.Life } })
            },
            PetData = ConvertToPetMessage(pet)
        };
    }

    public void SendPetsInfo(uint? seq = null)
    {
        var petsArray = new Messages.Pet[_pets.Count];
        for (int i = 0; i < _pets.Count; i++)
        {
            petsArray[i] = ConvertToPetMessage(_pets[i]);
        }

        var petsInfo = new PetsInfo
        {
            Pets = new Messages.Pets { Data = petsArray },
            GrazedPets = new Messages.Pets { Data = Array.Empty<Messages.Pet>() },
            GrazableCount = 0
        };

        if (seq.HasValue)
        {
            Send(petsInfo, seq.Value);
        }
        else
        {
            Send(petsInfo);
        }
    }

    private void HandleGetPetsInfo(GetPetsInfo msg, PacketHeader header)
    {
        SendPetsInfo(header.Seq);
    }

    private void HandleSpawnPet(SpawnPet msg, PacketHeader header)
    {
        var pet = _pets.FirstOrDefault(p => string.Equals(p.EntityId, msg.PetId, StringComparison.OrdinalIgnoreCase));
        if (pet == null)
        {
            Send(new Info { Text = "ไม่พบสัตว์เลี้ยงตัวนี้" }, header.Seq);
            return;
        }

        SpawnPetInternal(pet);
        Send(new OK(), header.Seq);
    }

    public void SpawnPetInternal(PetSave pet)
    {
        if (_spawnedPet != null && _spawnedPet != pet)
        {
            ReturnPetInternal(_spawnedPet);
        }

        pet.IsSpawned = true;
        pet.IsBoarding = false;
        _spawnedPet = pet;

        float rad = (CurrentYaw + 90f) * (MathF.PI / 180f);
        _petPosition = new WorldPosition(CurrentPosition.x + MathF.Sin(rad) * 150f, CurrentPosition.y + MathF.Cos(rad) * 150f);
        _petYaw = CurrentYaw;
        _petHeight = _lastHeight;
        _petFrom = _petPosition;
        _petTo = _petPosition;
        _petMoveStartAt = 0;
        _petMoveEndAt = 0;

        AppearPet appear = MakeAppearPet(pet);
        Send(appear);
        _world.BroadcastToViewers(EntityId, appear, except: this);
        Send(ConvertToPetMessage(pet));
        MarkDirty();
    }

    private void HandleReturnPet(ReturnPet msg, PacketHeader header)
    {
        var pet = _pets.FirstOrDefault(p => string.Equals(p.EntityId, msg.PetId, StringComparison.OrdinalIgnoreCase)) ?? _spawnedPet;
        if (pet == null)
        {
            Send(new OK(), header.Seq);
            return;
        }

        ReturnPetInternal(pet);
        Send(new OK(), header.Seq);
    }

    public void ReturnPetInternal(PetSave pet)
    {
        bool wasBoarding = pet.IsBoarding;
        pet.IsSpawned = false;
        pet.IsBoarding = false;
        if (_spawnedPet == pet)
        {
            _spawnedPet = null;
        }

        var dis = new DisappearPet
        {
            EntityId = pet.EntityId,
            TamerEntityId = EntityId
        };

        Send(dis);
        _world.BroadcastToViewers(EntityId, dis, except: this);
        Send(ConvertToPetMessage(pet));

        if (wasBoarding)
        {
            PlayerDisplay display = CurrentDisplay;
            Send(display);
            _world.BroadcastToViewers(EntityId, display, except: this);
        }

        MarkDirty();
    }

    public bool MountPetInternal()
    {
        if (_spawnedPet == null)
        {
            return false;
        }

        _spawnedPet.IsBoarding = true;
        _petPosition = CurrentPosition;
        _petYaw = CurrentYaw;
        _petHeight = _lastHeight;
        _petFrom = CurrentPosition;
        _petTo = CurrentPosition;
        _petMoveStartAt = 0;
        _petMoveEndAt = 0;

        var petMsg = ConvertToPetMessage(_spawnedPet);
        Send(petMsg);
        _world.BroadcastToViewers(EntityId, petMsg, except: this);

        PlayerDisplay display = CurrentDisplay;
        Send(display);
        _world.BroadcastToViewers(EntityId, display, except: this);

        MarkDirty();
        return true;
    }

    public bool UnmountPetInternal()
    {
        if (_spawnedPet == null)
        {
            return false;
        }

        _spawnedPet.IsBoarding = false;
        float rad = (CurrentYaw - 90f) * (MathF.PI / 180f);
        _petPosition = new WorldPosition(CurrentPosition.x + MathF.Sin(rad) * 150f, CurrentPosition.y + MathF.Cos(rad) * 150f);
        _petYaw = CurrentYaw;
        _petHeight = _lastHeight;
        _petFrom = _petPosition;
        _petTo = _petPosition;
        _petMoveStartAt = 0;
        _petMoveEndAt = 0;

        var petMsg = ConvertToPetMessage(_spawnedPet);
        Send(petMsg);
        _world.BroadcastToViewers(EntityId, petMsg, except: this);

        PlayerDisplay display = CurrentDisplay;
        Send(display);
        _world.BroadcastToViewers(EntityId, display, except: this);

        MarkDirty();
        return true;
    }

    private void HandleMount(Mount msg, PacketHeader header)
    {
        MountPetInternal();
    }

    private void HandleUnmount(Unmount msg, PacketHeader header)
    {
        UnmountPetInternal();
    }

    private void HandleUseTamingAction(UseTamingAction msg, PacketHeader header)
    {
        if (!ServerConfig.Current.Features.Taming)
        {
            RejectTamingDisabled(header);
            return;
        }

        if (!_world.Animals.TryGet(msg.EntityId, out ServerAnimal animal) || !animal.IsAlive)
        {
            Send(new Info { Text = "เป้าหมายไม่อยู่ในระยะหรือตายแล้ว" }, header.Seq);
            return;
        }

        float distSq = DistanceSqTo(animal.Position);
        float maxDistSq = 25f * 25f * 200f * 200f;
        if (distSq > maxDistSq)
        {
            Send(new Info { Text = "เป้าหมายอยู่ไกลเกินไป" }, header.Seq);
            return;
        }

        // If a tool/item was used for taming, consume or damage it
        if (!string.IsNullOrEmpty(msg.ToolItemId))
        {
            WearTool(msg.ToolItemId, WearKind.Taming);
        }

        // Send progress timer for client animation
        Send(new Messages.Timer
        {
            Duration = 2.0f
        }, header.Seq);

        // Perform taming
        TameAnimal(animal);
    }

    public bool TameAnimal(ServerAnimal animal)
    {
        if (animal == null || !animal.IsAlive) return false;

        PetTemplate template = PetData.FindByVehicleEntityType(animal.EntityType);
        ushort petEntityType = template?.EntityType ?? (ushort)3001;
        string petName = template != null && !string.IsNullOrEmpty(template.Name)
            ? template.Name
            : ("Pet " + animal.EntityType);
        int petRank = template != null && template.AvailableRanks.Count > 0
            ? template.AvailableRanks[0]
            : (int)PetRank.B;

        // Despawn wild dinosaur
        _world.Animals.Remove(animal.EntityId);

        // Create new pet
        var newPet = new PetSave
        {
            EntityId = "pet_" + Guid.NewGuid().ToString("N").Substring(0, 12),
            EntityType = petEntityType,
            Name = petName,
            Rank = petRank,
            Level = Math.Max(1, animal.Level),
            Exp = 0,
            Life = 100f,
            LifeMax = 100f,
            Hungry = 100f,
            HungryMax = 100f,
            IsSpawned = false,
            IsBoarding = false
        };

        _pets.Add(newPet);

        // Send taming completed reward effect
        Send(new Rewarded
        {
            Effect = new TamingCompletedEffect
            {
                Type = Shared.System.RewardEffect.AnimalTamed,
                AnimalEntityId = animal.EntityId,
                AnimalEntityType = animal.EntityType,
                ReinsId = newPet.EntityId
            },
            Reward = new RewardInfo()
        });

        SendPetsInfo();
        SpawnPetInternal(newPet);

        Send(new Info { Text = $"จับ {newPet.Name} สำเร็จแล้ว!" });
        Console.WriteLine("[tame] {0} จับ {1} (type {2} -> pet {3}) สำเร็จ", Name, newPet.Name, animal.EntityType, petEntityType);
        MarkDirty();
        return true;
    }

    public PetSave AddPetDirect(ushort entityType, string name = null, int level = 1)
    {
        PetTemplate template = PetData.FindByEntityType(entityType) ?? PetData.FindByVehicleEntityType(entityType);
        ushort finalType = template?.EntityType ?? entityType;
        string petName = name ?? template?.Name ?? ("Pet " + finalType);
        int petRank = template != null && template.AvailableRanks.Count > 0 ? template.AvailableRanks[0] : (int)PetRank.B;

        var newPet = new PetSave
        {
            EntityId = "pet_" + Guid.NewGuid().ToString("N").Substring(0, 12),
            EntityType = finalType,
            Name = petName,
            Rank = petRank,
            Level = Math.Max(1, level),
            Exp = 0,
            Life = 100f,
            LifeMax = 100f,
            Hungry = 100f,
            HungryMax = 100f,
            IsSpawned = false,
            IsBoarding = false
        };

        _pets.Add(newPet);
        SendPetsInfo();
        SpawnPetInternal(newPet);
        MarkDirty();
        return newPet;
    }

    private void HandleRenamePet(RenamePet msg, PacketHeader header)
    {
        var pet = _pets.FirstOrDefault(p => string.Equals(p.EntityId, msg.PetId, StringComparison.OrdinalIgnoreCase));
        if (pet == null)
        {
            Send(new OK(), header.Seq);
            return;
        }

        if (!string.IsNullOrWhiteSpace(msg.Name))
        {
            pet.Name = msg.Name.Trim();
        }

        Send(new OK(), header.Seq);
        Send(ConvertToPetMessage(pet));
        MarkDirty();
    }

    private void HandleReleasePet(ReleasePet msg, PacketHeader header)
    {
        var pet = _pets.FirstOrDefault(p => string.Equals(p.EntityId, msg.PetId, StringComparison.OrdinalIgnoreCase));
        if (pet == null)
        {
            Send(new OK(), header.Seq);
            return;
        }

        if (pet.IsSpawned)
        {
            ReturnPetInternal(pet);
        }

        _pets.Remove(pet);
        Send(new OK(), header.Seq);
        SendPetsInfo();
        MarkDirty();
    }

    public void SetPetStay(bool stay)
    {
        _petStayMode = stay;
        if (_spawnedPet != null)
        {
            _spawnedPet.StayMode = stay;
            MarkDirty();
            if (stay)
            {
                double now = Times.UnixTimeNow();
                WorldPosition petPos = PetPositionAt(now);
                _petFrom = petPos;
                _petTo = petPos;
                _petMoveStartAt = 0;
                _petMoveEndAt = 0;
                ushort motionType = GetPetMotionType(_spawnedPet.EntityType);
                string motionName = AnimalMotionData.Stand(motionType) ?? "idle";
                var standMove = new Move
                {
                    EntityId = _spawnedPet.EntityId,
                    Movements = new[]
                    {
                        new Movement
                        {
                            MotionName = motionName,
                            MotionOption = 5,
                            PlaybackRate = 1f,
                            RotSpeed = 540f,
                            Path = new[]
                            {
                                new Location { Position = petPos, Yaw = _petYaw, Time = now, Floor = 0, Height = _lastHeight }
                            }
                        }
                    }
                };
                Send(standMove);
                _world.BroadcastToViewers(EntityId, standMove, except: this);
                Send(new Info { Text = $"{_spawnedPet.Name} ได้รับคำสั่งให้อยู่กับที่ (Stay)" });
            }
            else
            {
                Send(new Info { Text = $"{_spawnedPet.Name} ได้รับคำสั่งให้ติดตามคุณ (Follow)" });
            }
        }
    }

    public void ProcessPet(double now)
    {
        if (_spawnedPet == null) return;

        if (_spawnedPet.IsBoarding)
        {
            _petPosition = CurrentPosition;
            _petYaw = CurrentYaw;
            _petHeight = _lastHeight;
            _petFrom = CurrentPosition;
            _petTo = CurrentPosition;
            return;
        }

        if (now - _lastPetFollowTickAt < 0.3) return;
        _lastPetFollowTickAt = now;

        if (_petStayMode) return;

        WorldPosition petPos = PetPositionAt(now);
        _petPosition = petPos;
        WorldPosition playerPos = CurrentPosition;

        float dx = playerPos.x - petPos.x;
        float dy = playerPos.y - petPos.y;
        float distSq = dx * dx + dy * dy;
        float dist = MathF.Sqrt(distSq);

        ushort motionType = GetPetMotionType(_spawnedPet.EntityType);

        // If player travelled very far (> 3500 units / ~17.5 tiles), teleport pet near player
        if (dist > 3500f)
        {
            float rad = (CurrentYaw + 180f) * (MathF.PI / 180f);
            WorldPosition dest = new WorldPosition(playerPos.x + MathF.Sin(rad) * 150f, playerPos.y + MathF.Cos(rad) * 150f);
            _petPosition = dest;
            _petFrom = dest;
            _petTo = dest;
            _petYaw = CurrentYaw;
            _petMoveStartAt = 0;
            _petMoveEndAt = 0;

            string motionName = AnimalMotionData.Stand(motionType) ?? "idle";
            var teleMove = new Move
            {
                EntityId = _spawnedPet.EntityId,
                Movements = new[]
                {
                    new Movement
                    {
                        MotionName = motionName,
                        MotionOption = 5,
                        PlaybackRate = 1f,
                        RotSpeed = 540f,
                        Path = new[]
                        {
                            new Location { Position = dest, Yaw = _petYaw, Time = now, Floor = 0, Height = _lastHeight }
                        }
                    }
                }
            };
            Send(teleMove);
            _world.BroadcastToViewers(EntityId, teleMove, except: this);
            return;
        }

        // Close enough: stop moving
        if (dist <= 260f)
        {
            if (now < _petMoveEndAt)
            {
                _petFrom = petPos;
                _petTo = petPos;
                _petMoveStartAt = 0;
                _petMoveEndAt = 0;
                string motionName = AnimalMotionData.Stand(motionType) ?? "idle";
                var standMove = new Move
                {
                    EntityId = _spawnedPet.EntityId,
                    Movements = new[]
                    {
                        new Movement
                        {
                            MotionName = motionName,
                            MotionOption = 5,
                            PlaybackRate = 1f,
                            RotSpeed = 540f,
                            Path = new[]
                            {
                                new Location { Position = petPos, Yaw = _petYaw, Time = now, Floor = 0, Height = _lastHeight },
                                new Location { Position = petPos, Yaw = _petYaw, Time = now + 1.0, Floor = 0, Height = _lastHeight }
                            }
                        }
                    }
                };
                Send(standMove);
                _world.BroadcastToViewers(EntityId, standMove, except: this);
            }
            return;
        }

        // Far enough: calculate target position near player
        float angleToPlayer = MathF.Atan2(dx, dy);
        float targetYaw = angleToPlayer * (180f / MathF.PI);
        if (targetYaw < 0f) targetYaw += 360f;

        float stopDist = 160f;
        WorldPosition destPos = new WorldPosition(
            playerPos.x - MathF.Sin(angleToPlayer) * stopDist,
            playerPos.y - MathF.Cos(angleToPlayer) * stopDist);

        float moveDist = MathF.Sqrt(
            (destPos.x - petPos.x) * (destPos.x - petPos.x) +
            (destPos.y - petPos.y) * (destPos.y - petPos.y));

        if (moveDist < 60f) return;

        bool isRunning = dist >= 700f;
        float speed = isRunning ? 520f : 280f;
        double duration = Math.Max(0.2, moveDist / speed);

        string anim = isRunning
            ? (AnimalMotionData.Run(motionType) ?? "run")
            : (AnimalMotionData.Walk(motionType) ?? "walk");

        _petFrom = petPos;
        _petTo = destPos;
        _petMoveStartAt = now;
        _petMoveEndAt = now + duration;
        _petYaw = targetYaw;

        var move = new Move
        {
            EntityId = _spawnedPet.EntityId,
            Movements = new[]
            {
                new Movement
                {
                    MotionName = anim,
                    MotionOption = 5,
                    PlaybackRate = 1f,
                    RotSpeed = 540f,
                    Path = new[]
                    {
                        new Location { Position = petPos, Yaw = targetYaw, Time = now, Floor = 0, Height = _lastHeight },
                        new Location { Position = destPos, Yaw = targetYaw, Time = now + duration, Floor = 0, Height = _lastHeight }
                    }
                }
            }
        };

        Send(move);
        _world.BroadcastToViewers(EntityId, move, except: this);
    }

    public bool FeedPet(PetSave pet, Item foodItem)
    {
        if (pet == null || string.IsNullOrEmpty(foodItem.Id)) return false;
        pet.Hungry = Math.Min(pet.HungryMax, pet.Hungry + 35f);
        pet.Life = Math.Min(pet.LifeMax, pet.Life + 30f);
        pet.Stamina = Math.Min(pet.StaminaMax, pet.Stamina + 50f);
        MarkDirty();

        Send(new FeedingSuccess { PetId = pet.EntityId });
        Send(ConvertToPetMessage(pet));
        SendPetsInfo();
        Send(new Info { Text = $"ให้อาหาร {foodItem.Name} แก่ {pet.Name} แล้ว (ความอิ่ม: {pet.Hungry:F0}/{pet.HungryMax:F0}, พลัง: {pet.Life:F0}/{pet.LifeMax:F0})" });
        return true;
    }

    public void HandleFeeding(Feeding msg, PacketHeader header)
    {
        PetSave pet = _pets.FirstOrDefault(p => string.Equals(p.EntityId, msg.PetId, StringComparison.OrdinalIgnoreCase)) ?? _spawnedPet;
        if (pet == null)
        {
            Send(new OK(), header.Seq);
            return;
        }

        if (msg.FoodIds != null && msg.FoodIds.Length > 0)
        {
            List<string> removedIds = new List<string>();
            lock (_inventory)
            {
                for (int i = 0; i < msg.FoodIds.Length; i++)
                {
                    string fid = msg.FoodIds[i];
                    if (string.IsNullOrEmpty(fid)) continue;
                    int idx = _inventory.FindIndex(it => it.Id == fid);
                    if (idx >= 0)
                    {
                        Item item = _inventory[idx];
                        _inventory.RemoveAt(idx);
                        removedIds.Add(fid);
                        FeedPet(pet, item);
                        break;
                    }
                }
            }

            if (removedIds.Count > 0)
            {
                Send(new InventoryUpdated { EntityId = EntityId, RemovedItemIds = removedIds.ToArray() });
                SendInventory();
            }
            else
            {
                Send(new Info { Text = "ไม่พบอาหารที่เลือกในกระเป๋า" });
            }
        }
        else
        {
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
                FeedPet(pet, food);
                Send(new InventoryUpdated { EntityId = EntityId, RemovedItemIds = new[] { food.Id } });
                SendInventory();
            }
            else
            {
                Send(new Info { Text = "ไม่มีอาหารในกระเป๋าที่จะให้สัตว์เลี้ยง" });
            }
        }

        Send(new OK(), header.Seq);
    }
}
