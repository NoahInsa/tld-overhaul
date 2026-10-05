using Il2Cpp;

namespace TLDOverhaul.Core
{
    /// <summary>Bridges TLD's SkillType enum and the overhaul's SkillId (which adds Carpentry, Mechanics, etc.).</summary>
    public static class SkillMap
    {
        public static bool TryFromVanilla(SkillType t, out SkillId id)
        {
            switch (t)
            {
                case SkillType.Firestarting: id = SkillId.Firestarting; return true;
                case SkillType.CarcassHarvesting: id = SkillId.CarcassHarvesting; return true;
                case SkillType.IceFishing: id = SkillId.IceFishing; return true;
                case SkillType.Cooking: id = SkillId.Cooking; return true;
                case SkillType.Rifle: id = SkillId.Rifle; return true;
                case SkillType.Archery: id = SkillId.Archery; return true;
                case SkillType.ClothingRepair: id = SkillId.Mending; return true;
                case SkillType.ToolRepair: id = SkillId.ToolRepair; return true;
                case SkillType.Revolver: id = SkillId.Revolver; return true;
                case SkillType.Gunsmithing: id = SkillId.Gunsmithing; return true;
                default: id = SkillId.Firestarting; return false;
            }
        }

        public static bool TryToVanilla(SkillId id, out SkillType t)
        {
            switch (id)
            {
                case SkillId.Firestarting: t = SkillType.Firestarting; return true;
                case SkillId.CarcassHarvesting: t = SkillType.CarcassHarvesting; return true;
                case SkillId.IceFishing: t = SkillType.IceFishing; return true;
                case SkillId.Cooking: t = SkillType.Cooking; return true;
                case SkillId.Rifle: t = SkillType.Rifle; return true;
                case SkillId.Archery: t = SkillType.Archery; return true;
                case SkillId.Mending: t = SkillType.ClothingRepair; return true;
                case SkillId.ToolRepair: t = SkillType.ToolRepair; return true;
                case SkillId.Revolver: t = SkillType.Revolver; return true;
                case SkillId.Gunsmithing: t = SkillType.Gunsmithing; return true;
                default: t = SkillType.None; return false;
            }
        }

        public static bool IsVanilla(SkillId id) { SkillType t; return TryToVanilla(id, out t); }

        public static string Display(SkillId id)
        {
            switch (id)
            {
                case SkillId.Mending: return "Tailoring";
                case SkillId.CarcassHarvesting: return "Butchering";
                case SkillId.IceFishing: return "Ice Fishing";
                case SkillId.ToolRepair: return "Tool Repair";
                case SkillId.FirstAid: return "First Aid";
                default: return id.ToString();
            }
        }

        public static readonly string[] Roman = { "I", "II", "III", "IV", "V" };
    }
}
