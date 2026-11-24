using System.IO;
using System.Text;
using Unity.VisualScripting;
using UnityEditor;
using UnityEngine;

namespace FlightWorld.EditorTools
{
    /// <summary>Read-only: every aircraft's fuel settings as built into the scene, and how long a tank lasts. Logs/fuel_census.txt.</summary>
    public static class FuelCensus
    {
        [MenuItem("FlightWorld/Tools/Fuel Census")]
        public static void Run()
        {
            var text = new StringBuilder();
            var fleet = GameObject.Find("Aircraft");
            foreach (Transform v in fleet.transform)
            {
                var vars = v.GetComponent<Variables>();
                if (vars == null || !vars.declarations.IsDefined("Fuel")) continue;
                float F(string n) => vars.declarations.IsDefined(n) ? System.Convert.ToSingle(vars.declarations.Get(n)) : float.NaN;
                float fuel = F("Fuel"), use = F("FuelConsumption"), ab = F("FuelConsumptionABMulti");
                text.AppendLine($"{v.name}: fuel {fuel}, low {F("LowFuel")}, use {use}/s (idle share {F("MinFuelConsumption")}, afterburner x{ab}), refuel {F("RefuelTime")} s, repair {F("RepairTime")} s" +
                                $" -> {(use > 0 ? fuel / use / 60f : float.PositiveInfinity):F0} min at full dry throttle; catapult {(vars.declarations.IsDefined("HasCatapult") ? vars.declarations.Get("HasCatapult") : "-")}");
            }
            File.WriteAllText("Logs/fuel_census.txt", text.ToString());
        }
    }
}
