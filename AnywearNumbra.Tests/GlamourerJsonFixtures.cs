using System.Text.Json.Nodes;

namespace AnywearNumbra.Tests;

/// <summary>
/// Builds JSON in the exact shape Glamourer's DesignBase.Serialize writes (FileVersion 1):
/// Equipment (12 slots + Hat/VieraEars/Visor/Weapon toggles), Bonus, Customize (+Wetness), Parameters, Materials.
/// "Apply" markers are written only when true, like Glamourer's WriteIfNot(..., false).
/// </summary>
internal static class GlamourerJsonFixtures
{
    public static readonly string[] EquipSlots =
        ["MainHand", "OffHand", "Head", "Body", "Hands", "Legs", "Feet", "Ears", "Neck", "Wrists", "RFinger", "LFinger"];

    public static readonly string[] ArmorSlots = ["Head", "Body", "Hands", "Legs", "Feet", "Ears", "Neck", "Wrists", "RFinger", "LFinger"];

    public static readonly string[] CustomizeKeys =
    [
        "Race", "Gender", "BodyType", "Height", "Clan", "Face", "Hairstyle", "Highlights", "SkinColor", "EyeColorRight",
        "HairColor", "HighlightsColor", "FacialFeature1", "FacialFeature2", "FacialFeature3", "FacialFeature4",
        "FacialFeature5", "FacialFeature6", "FacialFeature7", "LegacyTattoo", "TattooColor", "Eyebrows", "EyeColorLeft",
        "EyeShape", "SmallIris", "Nose", "Jaw", "Mouth", "Lipstick", "LipColor", "MuscleMass", "TailShape", "BustSize",
        "FacePaint", "FacePaintReversed", "FacePaintColor",
    ];

    /// <summary> A full state or design. <paramref name="seed"/> makes every value differ between objects. </summary>
    public static JsonObject Build(int seed, bool applyAll = true, bool withMaterials = true)
    {
        var equipment = new JsonObject();
        for (var i = 0; i < EquipSlots.Length; ++i)
        {
            var slot = new JsonObject
            {
                ["ItemId"] = (ulong)(seed * 1000 + i + 1),
                ["Stain"]  = (byte)((seed + i) % 100 + 1),
                ["Stain2"] = (byte)((seed + i + 7) % 100 + 1),
            };
            if (seed % 2 == 1)
                slot["Crest"] = true;
            if (applyAll)
            {
                slot["Apply"]      = true;
                slot["ApplyStain"] = true;
                slot["ApplyCrest"] = true;
            }

            equipment[EquipSlots[i]] = slot;
        }

        equipment["Hat"]       = Toggle("Show", seed % 2 == 0, applyAll);
        equipment["VieraEars"] = Toggle("Show", seed % 2 == 0, applyAll);
        equipment["Visor"]     = Toggle("IsToggled", seed % 2 == 1, applyAll);
        equipment["Weapon"]    = Toggle("Show", seed % 2 == 0, applyAll);

        var bonus = new JsonObject
        {
            ["Glasses"] = Entry(new JsonObject { ["BonusId"] = (ulong)(seed * 10 + 1) }, applyAll),
            ["UnkSlot"] = Entry(new JsonObject { ["BonusId"] = (ulong)(seed * 10 + 2) }, applyAll),
        };

        var customize = new JsonObject();
        for (var i = 0; i < CustomizeKeys.Length; ++i)
            customize[CustomizeKeys[i]] = Entry(new JsonObject { ["Value"] = (byte)((seed * 3 + i) % 200 + 1) }, applyAll);
        customize["Wetness"] = Toggle("Value", seed % 2 == 1, applyAll);

        var parameters = new JsonObject
        {
            ["SkinDiffuse"]   = Entry(new JsonObject { ["Red"] = 0.1f * seed, ["Green"] = 0.2f, ["Blue"] = 0.3f }, applyAll),
            ["HairDiffuse"]   = Entry(new JsonObject { ["Red"] = 0.4f, ["Green"] = 0.05f * seed, ["Blue"] = 0.6f }, applyAll),
            ["HairHighlight"] = Entry(new JsonObject { ["Red"] = 0.7f, ["Green"] = 0.8f, ["Blue"] = 0.01f * seed }, applyAll),
            ["LipDiffuse"]    = Entry(new JsonObject { ["Red"] = 0.1f, ["Green"] = 0.1f, ["Blue"] = 0.1f, ["Alpha"] = 0.02f * seed }, applyAll),
            ["FacePaintUvMultiplier"] = Entry(new JsonObject { ["Value"] = 1.0f + seed }, applyAll),
            ["LeftLimbalIntensity"]   = Entry(new JsonObject { ["Percentage"] = 0.03f * seed }, applyAll),
        };

        var root = new JsonObject
        {
            ["FileVersion"] = 1,
            ["Equipment"]   = equipment,
            ["Bonus"]       = bonus,
            ["Customize"]   = customize,
            ["Parameters"]  = parameters,
        };

        if (withMaterials)
            root["Materials"] = new JsonObject
            {
                ["0A000000"] = new JsonObject { ["Revert"] = false, ["Enabled"] = true, ["DiffuseR"] = 0.5f * seed },
            };

        // Round-trip through text so values are element-backed, exactly like data parsed from Glamourer.
        return (JsonObject)JsonNode.Parse(root.ToJsonString())!;
    }

    /// <summary> A design that sets every field (all Apply markers true), like a "full" Glamourer design. </summary>
    public static JsonObject FullDesign(int seed)
    {
        var design = Build(seed);
        design["Identifier"] = Guid.NewGuid().ToString();
        design["Name"]       = "Test design";
        design["Mods"]       = new JsonArray(new JsonObject { ["Name"] = "Some Mod", ["Enabled"] = true });
        design["Links"]      = new JsonObject();
        return design;
    }

    public static JsonObject Section(JsonObject root, string key)
        => (JsonObject)root[key]!;

    public static JsonObject Slot(JsonObject root, string slot)
        => (JsonObject)Section(root, "Equipment")[slot]!;

    public static bool Bool(JsonObject entry, string key)
        => entry[key] is JsonValue v && v.TryGetValue<bool>(out var b) && b;

    /// <summary> Copy of a section with all Apply* markers removed, serialized, for byte-for-byte value comparison. </summary>
    public static string ValuesOnly(JsonNode? section)
    {
        if (section is null)
            return "null";

        var clone = section.DeepClone();
        Strip(clone);
        return clone.ToJsonString();

        static void Strip(JsonNode node)
        {
            if (node is not JsonObject obj)
                return;

            foreach (var key in obj.Select(p => p.Key).Where(k => k is "Apply" or "ApplyStain" or "ApplyCrest").ToList())
                obj.Remove(key);
            foreach (var (_, child) in obj)
            {
                if (child is not null)
                    Strip(child);
            }
        }
    }

    /// <summary> Every Apply* marker anywhere under <paramref name="node"/>, with its path. </summary>
    public static IEnumerable<(string Path, bool Value)> ApplyMarkers(JsonNode? node, string path = "")
    {
        if (node is not JsonObject obj)
            yield break;

        foreach (var (key, child) in obj)
        {
            var childPath = path.Length == 0 ? key : $"{path}.{key}";
            if (key is "Apply" or "ApplyStain" or "ApplyCrest")
                yield return (childPath, child is JsonValue v && v.TryGetValue<bool>(out var b) && b);
            else
                foreach (var marker in ApplyMarkers(child, childPath))
                    yield return marker;
        }
    }

    private static JsonObject Toggle(string valueKey, bool value, bool apply)
        => Entry(new JsonObject { [valueKey] = value }, apply);

    private static JsonObject Entry(JsonObject entry, bool apply)
    {
        if (apply)
            entry["Apply"] = true;
        return entry;
    }
}
