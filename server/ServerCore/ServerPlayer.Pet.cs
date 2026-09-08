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

    public IReadOnlyList<PetSave> Pets => _pets;
    public PetSave SpawnedPet => _spawnedPet;

    private void ApplyPetSave(PlayerSave save)
    {
        _pets.Clear();
        _spawnedPet = null;
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
                    }
                }
            }
        }
    }

    private void FillPetSave(PlayerSave save)
    {
        if (save == null) return;
        save.Pets.Clear();
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
                        MotionName = "idle",
                        MotionOption = 0,
                        PlaybackRate = 1f,
                        RotSpeed = 0f,
                        Path = new[]
                        {
                            new Location
                            {
                                Position = CurrentPosition,
                                Yaw = CurrentYaw,
                                Time = now,
                                Floor = 0,
                                Height = 0f
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
}
