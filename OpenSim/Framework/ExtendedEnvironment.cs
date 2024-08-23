using OpenMetaverse.StructuredData;
using OpenMetaverse;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using static System.TimeZoneInfo;
using System.Security.AccessControl;

namespace OpenSim.Framework
{
    public abstract class EnvironmentUpdate
    {
        public string ObjectName = string.Empty;
        public UUID OwnerID = UUID.Zero;
        public string ParcelName = string.Empty;
        public int Permission = 0;
        public string Action = string.Empty;
        public float TransitionTime = 0.0f;

        public abstract OSDMap ToMap();
    }

    public class FullEnvironmentUpdate : EnvironmentUpdate
    {
        public UUID AssetID = UUID.Zero;
        public new string Action => "PushFullEnvironment";

        public override OSDMap ToMap()
        {
            OSDMap map = new OSDMap();

            map["ObjectName"] = ObjectName;
            map["OwnerID"] = OwnerID;
            map["ParcelName"] = ParcelName;
            map["Permission"] = Permission;
            map["action"] = Action;

            OSDMap action_data = new OSDMap();
            
            action_data["asset_id"] = AssetID;
            action_data["transition_time"] = TransitionTime;

            map["action_data"] = action_data;

            return map;
        }
    }

    public class PartialEnvironmentUpdate : EnvironmentUpdate
    {
        public OSDMap water = new OSDMap();
        public OSDMap sky = new OSDMap();
        public new string Action => "PushPartialEnvironment";

        public override OSDMap ToMap()
        {
            OSDMap map = new OSDMap();

            map["ObjectName"] = ObjectName;
            map["OwnerID"] = OwnerID;
            map["ParcelName"] = ParcelName;
            map["Permission"] = Permission;
            map["action"] = Action;

            OSDMap settings = new OSDMap();

            if (water.Count > 0)
                settings["water"] = water;

            if (sky.Count > 0)
                settings["sky"] = sky;

            OSDMap action_data = new OSDMap();
            
            action_data["settings"] = settings;
            action_data["transition_time"] = TransitionTime;

            map["action_data"] = action_data;

            return map;
        }
    }

    public class ClearEnvironmentUpdate : EnvironmentUpdate
    {
        public new string Action => "ClearEnvironment";

        public override OSDMap ToMap()
        {
            OSDMap map = new OSDMap();

            map["ObjectName"] = ObjectName;
            map["OwnerID"] = OwnerID;
            map["ParcelName"] = ParcelName;
            map["Permission"] = Permission;
            map["action"] = Action;

            OSDMap action_data = new OSDMap();
            action_data["transition_time"] = TransitionTime;

            map["action_data"] = action_data;

            return map;
        }
    }

    public abstract class EnvironmentData
    {
        public abstract OSDMap ToOSD(bool include_name = true);
        public abstract void FromOSD(OSDMap map);

        public abstract void GatherAssets(Dictionary<UUID, sbyte> uuids);

        public static EnvironmentData ClassFromMap(OSDMap map)
        {
            string type = map["type"];
            EnvironmentData data = null;

            try
            {
                switch (type)
                {
                    case "sky":
                        {
                            data = new SkyData();
                            data.FromOSD(map);
                            break;
                        }
                    case "water":
                        {
                            data = new WaterData();
                            data.FromOSD(map);
                            break;
                        }
                    case "daycycle":
                        {
                            data = new DayCycle();
                            data.FromOSD(map);
                            break;
                        }
                }
            }
            catch
            {

            }

            return data;
        }
    }

    public class EEPOverrides
    {
        public OSDMap[] Tracks =
        [
            new OSDMap(),
            new OSDMap(),
            new OSDMap(),
            new OSDMap(),
            new OSDMap()
        ];

        public OSDArray ToOSD()
        {
            var arr = new OSDArray();

            foreach(var track in Tracks)
            {
                arr.Add(track);
            }

            return arr;
        }

        public void FromOSD(OSDArray arr)
        {
            for(int iter = 0; iter < arr.Count; iter++)
            {
                var track = arr[iter];
                Tracks[iter] = track as OSDMap;
            }
        }

        // I feel like this should exist alredy? perhaps it does?
        public static void MergeOSDMaps(OSDMap original, OSDMap other)
        {
            foreach(var key in other.Keys)
            {
                var value = other[key];

                if (value is OSDMap)
                {
                    var omap = original[key] as OSDMap;
                    var map = value as OSDMap;
                    MergeOSDMaps(omap, map);
                }
                else original[key] = value;
            }
        }

        public void ClearOverrides()
        {
            foreach (var arr in Tracks)
                arr.Clear();
        }
    }
}
