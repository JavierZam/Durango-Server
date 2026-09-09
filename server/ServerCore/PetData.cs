using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using Shared.Animal;

namespace DurangoServer.Core;

public sealed class PetTemplate
{
    public ushort EntityType { get; set; }
    public ushort VehicleEntityType { get; set; }
    public string TypeName { get; set; }
    public string Species { get; set; }
    public string ReinId { get; set; }
    public string Name { get; set; }
    public bool IsRidable { get; set; }
    public bool IsFightable { get; set; }
    public string Type { get; set; }
    public List<int> AvailableRanks { get; set; } = new List<int>();
}

public static class PetData
{
    private static readonly Dictionary<ushort, PetTemplate> _byEntityType = new Dictionary<ushort, PetTemplate>();
    private static readonly Dictionary<ushort, PetTemplate> _byVehicleEntityType = new Dictionary<ushort, PetTemplate>();
    private static readonly List<PetTemplate> _all = new List<PetTemplate>();

    public static IReadOnlyList<PetTemplate> All => _all;

    public static void Load(string filePath)
    {
        if (!File.Exists(filePath))
        {
            Console.WriteLine("[pet] ไม่พบไฟล์ {0}", filePath);
            return;
        }

        try
        {
            string json = File.ReadAllText(filePath);
            var obj = JObject.Parse(json);
            _byEntityType.Clear();
            _byVehicleEntityType.Clear();
            _all.Clear();

            foreach (var prop in obj.Properties())
            {
                if (!ushort.TryParse(prop.Name, out ushort entityType))
                    continue;

                var data = prop.Value as JObject;
                if (data == null) continue;

                ushort vehicleType = data.Value<ushort>("vehicle_entity_type");
                string typeName = data.Value<string>("type_name") ?? string.Empty;
                string species = data.Value<string>("species") ?? string.Empty;
                string reinId = data.Value<string>("rein_id") ?? string.Empty;
                bool ridable = data.Value<bool>("is_ridable");
                bool fightable = data.Value<bool>("is_fightable");
                string type = data.Value<string>("type") ?? "Herbivore";

                string name = species;
                if (data["name"] is JObject nameObj && nameObj.Properties().Any())
                {
                    name = nameObj.Properties().First().Name;
                }

                var ranks = new List<int>();
                if (data["available_ranks"] is JArray ranksArr)
                {
                    foreach (var r in ranksArr)
                    {
                        ranks.Add(r.Value<int>());
                    }
                }

                var template = new PetTemplate
                {
                    EntityType = entityType,
                    VehicleEntityType = vehicleType,
                    TypeName = typeName,
                    Species = species,
                    ReinId = reinId,
                    Name = name,
                    IsRidable = ridable,
                    IsFightable = fightable,
                    Type = type,
                    AvailableRanks = ranks
                };

                _byEntityType[entityType] = template;
                if (!_byVehicleEntityType.ContainsKey(vehicleType))
                {
                    _byVehicleEntityType[vehicleType] = template;
                }
                _all.Add(template);
            }

            Console.WriteLine("[pet] โหลดเทมเพลตสัตว์เลี้ยงสำเร็จ {0} ชนิด จาก {1}", _all.Count, Path.GetFileName(filePath));
        }
        catch (Exception ex)
        {
            Console.WriteLine("[pet] เกิดข้อผิดพลาดในการโหลด pets_for_client.json: {0}", ex.Message);
        }
    }

    public static PetTemplate FindByEntityType(ushort entityType)
    {
        _byEntityType.TryGetValue(entityType, out var t);
        return t;
    }

    public static PetTemplate FindByVehicleEntityType(ushort vehicleEntityType)
    {
        _byVehicleEntityType.TryGetValue(vehicleEntityType, out var t);
        return t;
    }

    public static PetTemplate FindByQuery(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return null;

        if (ushort.TryParse(query, out ushort id))
        {
            var byId = FindByEntityType(id);
            if (byId != null) return byId;
            var byVeh = FindByVehicleEntityType(id);
            if (byVeh != null) return byVeh;
        }

        query = query.ToLowerInvariant();
        return _all.FirstOrDefault(p =>
            p.Species.ToLowerInvariant().Contains(query) ||
            p.TypeName.ToLowerInvariant().Contains(query) ||
            p.Name.ToLowerInvariant().Contains(query));
    }

    public static float GetDefaultSpeed(ushort entityType)
    {
        var tmpl = FindByEntityType(entityType) ?? FindByVehicleEntityType(entityType);
        if (tmpl == null) return 650f;
        string sp = (tmpl.Species ?? string.Empty).ToLowerInvariant();
        string tn = (tmpl.TypeName ?? string.Empty).ToLowerInvariant();
        if (sp.Contains("ostrich") || sp.Contains("galli") || sp.Contains("struthio")) return 780f;
        if (sp.Contains("raptor") || sp.Contains("deinon") || sp.Contains("smilodon") || tn.Contains("raptor")) return 750f;
        if (sp.Contains("tarbo") || sp.Contains("trex") || sp.Contains("t-rex") || sp.Contains("tyranno") || sp.Contains("carnot")) return 680f;
        if (sp.Contains("mammoth") || sp.Contains("elephant") || sp.Contains("stego") || sp.Contains("ankyl") || sp.Contains("brachio")) return 580f;
        return 650f;
    }
}
