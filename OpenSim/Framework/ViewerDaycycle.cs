/*
 * Copyright (c) Contributors, http://opensimulator.org/
 * See CONTRIBUTORS.TXT for a full list of copyright holders.
 *
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the following conditions are met:
 *     * Redistributions of source code must retain the above copyright
 *       notice, this list of conditions and the following disclaimer.
 *     * Redistributions in binary form must reproduce the above copyright
 *       notice, this list of conditions and the following disclaimer in the
 *       documentation and/or other materials provided with the distribution.
 *     * Neither the name of the OpenSimulator Project nor the
 *       names of its contributors may be used to endorse or promote products
 *       derived from this software without specific prior written permission.
 *
 * THIS SOFTWARE IS PROVIDED BY THE DEVELOPERS ``AS IS'' AND ANY
 * EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
 * WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
 * DISCLAIMED. IN NO EVENT SHALL THE CONTRIBUTORS BE LIABLE FOR ANY
 * DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
 * (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
 * LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
 * ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
 * (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
 * SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using OpenMetaverse;
using OpenMetaverse.StructuredData;

namespace OpenSim.Framework
{
    public class DayCycle : EnvironmentData
    {
        public struct TrackEntry
        {
            public float time;
            public string frameName;

            public TrackEntry(float t, string f)
            {
                time = t;
                frameName = f;
            }
        }

        public class CompareTrackEntries : IComparer<TrackEntry>
        {
            public int Compare(TrackEntry x, TrackEntry y)
            {
                    return x.time.CompareTo(y.time);
            }
        }

        public bool IsStaticDayCycle = false;
        public List<TrackEntry> waterTrack = new List<TrackEntry>();
        public List<TrackEntry> skyTrack0 = new List<TrackEntry>();
        public List<TrackEntry>[] skyTracks = new List<TrackEntry>[3]
        {
            new List<TrackEntry>(),
            new List<TrackEntry>(),
            new List<TrackEntry>()
        };

        public Dictionary<string, SkyData> skyframes = new Dictionary<string, SkyData>();
        public Dictionary<string, WaterData> waterframes = new Dictionary<string, WaterData>();

        public string Name;

        public void FromWLOSD(OSDArray array)
        {
            CompareTrackEntries cte = new CompareTrackEntries();
            TrackEntry track;

            OSDArray skytracksArray = null;
            if (array.Count > 1)
                skytracksArray = array[1] as OSDArray;
            if(skytracksArray != null)
            {
                foreach (OSD setting in skytracksArray)
                {
                    OSDArray innerSetting = setting as OSDArray;
                    if(innerSetting != null)
                    {
                        track = new TrackEntry((float)innerSetting[0].AsReal(), innerSetting[1].AsString());
                        skyTrack0.Add(track);
                    }
                }
                skyTrack0.Sort(cte);
            }

            OSDMap skyFramesArray = null;
            if (array.Count > 2)
                skyFramesArray = array[2] as OSDMap;
            if(skyFramesArray != null)
            {
                foreach (KeyValuePair<string, OSD> kvp in skyFramesArray)
                {
                    SkyData sky = new SkyData();
                    sky.FromWLOSD(kvp.Key, kvp.Value);
                    skyframes[kvp.Key] = sky;
                }
            }

            WaterData water = new WaterData();
            OSDMap watermap = null;
            if(array.Count > 3)
                watermap = array[3] as OSDMap;
            if(watermap != null)
                water.FromWLOSD("WLWater", watermap);

            waterframes["WLWater"] = water;
            track = new TrackEntry(-1f, "WLWater");
            waterTrack.Add(track);

            Name = "WLDaycycle";

            if (skyTrack0.Count == 1 && skyTrack0[0].time == -1f)
                IsStaticDayCycle = true;
        }

        public void ToWLOSD(ref OSDArray array)
        {
            OSDArray track = new OSDArray();
            foreach (TrackEntry te in skyTrack0)
                track.Add(new OSDArray { te.time, te.frameName });
            array[1] = track;

            OSDMap frames = new OSDMap();
            foreach (KeyValuePair<string, SkyData> kvp in skyframes)
                frames[kvp.Key] = kvp.Value.ToWLOSD();
            array[2] = frames;

            if(waterTrack.Count > 0)
            {
                TrackEntry te = waterTrack[0];
                if(waterframes.TryGetValue(te.frameName, out WaterData water))
                    array[3] = water.ToWLOSD();
            }
            else
                array[3] = new OSDMap();
        }

        public void ClearAllTracks(bool include_water = false)
        {
            skyframes.Clear();

            skyTrack0.Clear();
            skyTracks[0].Clear();
            skyTracks[1].Clear();
            skyTracks[2].Clear();

            if (include_water)
            {
                waterTrack.Clear();
                waterframes.Clear();
            }
        }

        void GatherNames(List<TrackEntry> entries, List<string> names)
        {
            foreach (var entry in entries)
            {
                if(!names.Contains(entry.frameName))
                {
                    names.Add(entry.frameName);
                }
            }
        }

        public void CleanseFrames()
        {
            List<string> water_track_names = new List<string>();
            List<string> sky_track_names = new List<string>();

            GatherNames(waterTrack, water_track_names);
            GatherNames(skyTrack0, sky_track_names);
            foreach (var list in skyTracks)
                GatherNames(list, sky_track_names);

            List<string> names_to_remove = new List<string>();

            skyframes = skyframes.Where(pair => sky_track_names.Contains(pair.Key)).ToDictionary(pair => pair.Key, pair => pair.Value);
            waterframes = waterframes.Where(pair => water_track_names.Contains(pair.Key)).ToDictionary(pair => pair.Key, pair => pair.Value);
        }

        string QuickFrameName()
        {
            return Random.Shared.NextInt64(99999999999999, 99999999999999999).ToString();
        }

        public bool replaceWater(WaterData water)
        {
            var track_entry = new TrackEntry
            {
                time = 0,
                frameName = QuickFrameName()
            };

            waterTrack.Clear();
            waterframes.Clear();

            waterTrack.Add(track_entry);
            waterframes[track_entry.frameName] = water;

            return true;
        }

        public bool replaceFromSkyData(SkyData sky_data, int track)
        {
            var track_entry = new TrackEntry
            {
                time = 0,
                frameName = QuickFrameName()
            };

            if (track == -1)
            {
                ClearAllTracks();
                skyTrack0.Add(track_entry);
            }
            else if (track == 1)
            {
                skyTrack0.Clear();
                skyTrack0.Add(track_entry);
            }
            else if (track > 1 && track < 5)
            {
                int index = track - 2;
                skyTracks[index].Clear();
                skyTracks[index].Add(track_entry);
            }
            else return false; // ignore 0, and anything higher than 4

            skyframes[track_entry.frameName] = sky_data;
            CleanseFrames();

            return false;
        }

        public bool replaceFromDayCycle(DayCycle daycycle, int track)
        {
            Dictionary<string, SkyData> sky_frames = new Dictionary<string, SkyData>();

            foreach(var track_entry in daycycle.skyTrack0)
            {
                if(daycycle.skyframes.ContainsKey(track_entry.frameName))
                {
                    var sky = daycycle.skyframes[track_entry.frameName];
                    sky_frames[track_entry.frameName] = sky;
                }
            }

            if (track == -1)
            {
                ClearAllTracks(true);

                skyTrack0 = daycycle.skyTrack0;
                skyframes = sky_frames;
                waterTrack = daycycle.waterTrack;
                waterframes = daycycle.waterframes;
            }
            else if (track == 0)
            {
                waterTrack = daycycle.waterTrack;
                waterframes = daycycle.waterframes;
            }
            else if (track == 1)
            {
                skyTrack0 = daycycle.skyTrack0;

                foreach (var pair in sky_frames)
                    skyframes[pair.Key] = pair.Value;
            }
            else if (track < 5)
            {
                int index = track - 2;

                skyTracks[index] = daycycle.skyTrack0;

                foreach (var pair in sky_frames)
                    skyframes[pair.Key] = pair.Value;

            }
            else return false;

            CleanseFrames();

            return true;
        }

        public override void FromOSD(OSDMap map)
        {
            CompareTrackEntries cte = new CompareTrackEntries();
            OSD otmp;

            if(map.TryGetValue("frames", out otmp) && otmp is OSDMap)
            {
                OSDMap mframes = otmp as OSDMap;
                foreach(KeyValuePair<string, OSD> kvp in mframes)
                {
                    OSDMap v = kvp.Value as OSDMap;
                    if(v.TryGetValue("type", out otmp))
                    {
                        string type = otmp;
                        if (type.Equals("water"))
                        {
                            WaterData water = new WaterData();
                            water.FromOSD(v);
                            waterframes[kvp.Key] = water;
                        }
                        else if (type.Equals("sky"))
                        {
                            SkyData sky = new SkyData();
                            sky.FromOSD(v);
                            skyframes[kvp.Key] = sky;
                        }
                    }
                }
            }

            if (map.TryGetValue("name", out otmp))
                Name = otmp;
            else
                Name = "DayCycle";

            OSDArray track;
            if (map.TryGetValue("tracks", out otmp) && otmp is OSDArray)
            {
                OSDArray tracks = otmp as OSDArray;
                if(tracks.Count > 0)
                {
                    track = tracks[0] as OSDArray;
                    if (track != null && track.Count > 0)
                    {
                        for (int i = 0; i < track.Count; ++i)
                        {
                            OSDMap d = track[i] as OSDMap;
                            if (d.TryGetValue("key_keyframe", out OSD dtime))
                            {
                                if (d.TryGetValue("key_name", out OSD dname))
                                {
                                    TrackEntry t = new TrackEntry()
                                    {
                                        time = dtime,
                                        frameName = dname
                                    };
                                    waterTrack.Add(t);
                                }
                            }
                        }
                        waterTrack.Sort(cte);
                    }
                }
                if (tracks.Count > 1)
                {
                    track = tracks[1] as OSDArray;
                    if (track != null && track.Count > 0)
                    {
                        for (int i = 0; i < track.Count; ++i)
                        {
                            OSDMap d = track[i] as OSDMap;
                            if (d.TryGetValue("key_keyframe", out OSD dtime))
                            {
                                if (d.TryGetValue("key_name", out OSD dname))
                                {
                                    TrackEntry t = new TrackEntry();
                                    t.time = dtime;
                                    t.frameName = dname;
                                    skyTrack0.Add(t);
                                }
                            }
                        }
                        skyTrack0.Sort(cte);
                    }
                }
                if (tracks.Count > 2)
                {
                    for(int st = 2, dt = 0; st < tracks.Count && dt < 3; ++st, ++dt)
                    {
                        track = tracks[st] as OSDArray;
                        if(track != null && track.Count > 0)
                        {
                            skyTracks[dt] = new List<TrackEntry>();
                            for (int i = 0; i < track.Count; ++i)
                            {
                                OSDMap d = track[i] as OSDMap;
                                if (d.TryGetValue("key_keyframe", out OSD dtime))
                                {
                                    if (d.TryGetValue("key_name", out OSD dname))
                                    {
                                        TrackEntry t = new TrackEntry();
                                        t.time = dtime;
                                        t.frameName = dname;
                                        skyTracks[dt].Add(t);
                                    }
                                }
                            }
                            skyTracks[dt].Sort(cte);
                        }
                    }
                }
            }
        }

        public override OSDMap ToOSD(bool include_name = true)
        {
            OSDMap cycle = new OSDMap();

            OSDMap frames = new OSDMap();
            foreach (KeyValuePair<string, WaterData> kvp in waterframes)
            {
                frames[kvp.Key] = kvp.Value.ToOSD(include_name);
            }
            foreach (KeyValuePair<string, SkyData> kvp in skyframes)
            {
                frames[kvp.Key] = kvp.Value.ToOSD(include_name);
            }
            cycle["frames"] = frames;

            cycle["name"] = Name;

            OSDArray tracks = new OSDArray();

            OSDArray track = new OSDArray();
            OSDMap tmp;
            foreach (TrackEntry te in waterTrack)
            {
                tmp = new OSDMap();
                if (te.time < 0)
                    tmp["key_keyframe"] = 0f;
                else
                    tmp["key_keyframe"] = te.time;
                tmp["key_name"] = te.frameName;
                track.Add(tmp);
            }
            tracks.Add(track);

            track = new OSDArray();
            foreach (TrackEntry te in skyTrack0)
            {
                tmp = new OSDMap();
                if (te.time < 0)
                    tmp["key_keyframe"] = 0f;
                else
                    tmp["key_keyframe"] = te.time;
                tmp["key_name"] = te.frameName;
                track.Add(tmp);
            }
            tracks.Add(track);

            for(int st = 0; st < 3; ++st)
            {
                track = new OSDArray();
                if(skyTracks[st] != null)
                {
                    foreach (TrackEntry te in skyTracks[st])
                    {
                        tmp = new OSDMap();
                        if (te.time < 0)
                            tmp["key_keyframe"] = 0f;
                        else
                            tmp["key_keyframe"] = te.time;
                        tmp["key_name"] = te.frameName;
                        track.Add(tmp);
                    }
                }
                tracks.Add(track);
            }

            cycle["tracks"] = tracks;
            cycle["type"] = "daycycle";

            return cycle;
        }

        public override void GatherAssets(Dictionary<UUID, sbyte> uuids)
        {
            foreach (WaterData wd in waterframes.Values)
            {
                wd.GatherAssets(uuids);
            }
            foreach (SkyData sd in skyframes.Values)
            {
                sd.GatherAssets(uuids);
            }
        }
    }
}
