using System;
using System.Collections.Generic;
using System.IO;
using ICities;
using UnityEngine;

namespace NagoyaRoadDelivery
{
    public sealed class NativeCitySaveData : SerializableDataExtensionBase
    {
        private const string DataId = "sunwood.nagoya.native-city.v1";
        private const string DeliveriesId = "sunwood.nagoya.android-deliveries.v1";

        public override void OnLoadData()
        {
            try { NativeCityLedger.Load(serializableDataManager.LoadData(DataId)); }
            catch (Exception error)
            {
                NativeCityLedger.Load(null);
                Debug.LogException(error);
            }
            NativeAndroidDeliveries.LoadCheckpoint(null);
            try { NativeAndroidDeliveries.LoadCheckpoint(serializableDataManager.LoadData(DeliveriesId)); }
            catch (Exception error) { Debug.LogException(error); }
        }

        public override void OnSaveData()
        {
            byte[] payload = NativeCityLedger.Save();
            if (payload != null)
                serializableDataManager.SaveData(DataId, payload);
            payload = NativeAndroidDeliveries.SaveCheckpoint();
            if (payload != null)
                serializableDataManager.SaveData(DeliveriesId, payload);
        }
    }

    internal static class NativeCityLedger
    {
        private const uint Format = 0x3259474e; // NGY2
        private const int MaxRoads = 34000;
        private const int MaxParcels = 12000;
        internal const int MaxResidents = 4096;
        private const int MaxBytes = 6 * 1024 * 1024;
        private static readonly object Gate = new object();
        private static string city;
        private static FileData pending;
        private static readonly Dictionary<ushort, Road> Roads = new Dictionary<ushort, Road>();
        private static readonly Dictionary<ushort, Lot> Lots = new Dictionary<ushort, Lot>();
        private static readonly Dictionary<uint, Android> Residents = new Dictionary<uint, Android>();

        internal struct Road
        {
            public string Source, Tag;
            public ushort Id, Start, End;
            public uint BuildIndex;
        }

        internal struct Lot
        {
            public string Group, Ward;
            public ushort Id, Road, ZoneBlock;
            public uint BuildIndex;
            public ItemClass.Zone Zone;
            public int Members, Row, Width, Length;
        }

        internal struct Android
        {
            public uint Id, HomeUnit;
            public ushort Home;
            public float Battery;
        }

        private sealed class FileData
        {
            public string City;
            public Road[] Roads;
            public Lot[] Lots;
            public Android[] Residents;
        }

        internal static void Enter(string name)
        {
            lock (Gate)
            {
                city = name;
                Roads.Clear();
                Lots.Clear();
                Residents.Clear();
                if (pending != null && pending.City == city)
                {
                    foreach (Road road in pending.Roads) Roads.Add(road.Id, road);
                    foreach (Lot lot in pending.Lots) Lots.Add(lot.Id, lot);
                    foreach (Android android in pending.Residents) Residents.Add(android.Id, android);
                }
                pending = null;
            }
        }

        internal static void Leave()
        {
            lock (Gate)
            {
                city = null;
                Roads.Clear();
                Lots.Clear();
                Residents.Clear();
            }
        }

        internal static void Record(Road road)
        {
            lock (Gate)
            {
                if (city != null)
                    Roads[road.Id] = road;
            }
        }

        internal static void Record(Lot lot)
        {
            lock (Gate)
            {
                if (city != null)
                    Lots[lot.Id] = lot;
            }
        }

        internal static string RegisteredWard(ushort site)
        {
            lock (Gate)
            {
                Lot lot;
                return Lots.TryGetValue(site, out lot) ? lot.Ward : null;
            }
        }

        internal static void Snapshot(out Road[] roads, out Lot[] lots)
        {
            lock (Gate)
            {
                roads = new Road[Roads.Count];
                Roads.Values.CopyTo(roads, 0);
                lots = new Lot[Lots.Count];
                Lots.Values.CopyTo(lots, 0);
            }
        }

        internal static Android[] SavedAndroids()
        {
            lock (Gate)
            {
                Android[] result = new Android[Residents.Count];
                Residents.Values.CopyTo(result, 0);
                return result;
            }
        }

        internal static void Record(Android resident)
        {
            lock (Gate)
            {
                if (city != null)
                    Residents[resident.Id] = resident;
            }
        }

        internal static void RemoveAndroid(uint id)
        {
            lock (Gate)
                Residents.Remove(id);
        }

        internal static bool HasActiveZone(Lot lot)
        {
            global::Building[] buildings = BuildingManager.instance.m_buildings.m_buffer;
            if (lot.Id == 0 || lot.Id >= buildings.Length)
                return false;
            global::Building building = buildings[lot.Id];
            if ((building.m_flags & global::Building.Flags.Created) == 0 || building.m_buildIndex != lot.BuildIndex)
                return false;
            ZoneBlock block;
            return TryGetZonedBlock(lot, out block);
        }

        internal static bool TryGetZonedBlock(Lot lot, out ZoneBlock block)
        {
            block = new ZoneBlock();
            ZoneBlock[] blocks = ZoneManager.instance.m_blocks.m_buffer;
            if (lot.ZoneBlock == 0 || lot.ZoneBlock >= blocks.Length) return false;
            block = blocks[lot.ZoneBlock];
            if ((block.m_flags & ZoneBlock.FLAG_CREATED) == 0 || lot.Row + lot.Width > block.RowCount)
                return false;
            for (int y = lot.Row; y < lot.Row + lot.Width; y++)
                for (int x = 0; x < lot.Length; x++)
                    if (block.GetZone(x, y) != lot.Zone)
                        return false;
            return true;
        }

        internal static bool TryRebind(Lot original, ushort newId, uint generation)
        {
            lock (Gate)
            {
                Lot stored;
                if (city == null || !Lots.TryGetValue(original.Id, out stored) || stored.Group != original.Group ||
                    stored.BuildIndex != original.BuildIndex || (original.Id != newId && Lots.ContainsKey(newId)))
                    return false;
                Lots.Remove(original.Id);
                original.Id = newId;
                original.BuildIndex = generation;
                Lots.Add(newId, original);
                return true;
            }
        }

        internal static bool HasActiveRoad(Road road)
        {
            NetSegment[] segments = NetManager.instance.m_segments.m_buffer;
            if (road.Id == 0 || road.Id >= segments.Length)
                return false;
            NetSegment actual = segments[road.Id];
            return (actual.m_flags & NetSegment.Flags.Created) != 0 && actual.m_buildIndex == road.BuildIndex &&
                actual.m_startNode == road.Start && actual.m_endNode == road.End;
        }

        internal static byte[] Save()
        {
            lock (Gate)
            {
                if (city == null)
                    return null;
                if (Roads.Count > MaxRoads || Lots.Count > MaxParcels || Residents.Count > MaxResidents)
                    throw new InvalidDataException("Native construction save exceeds the ledger bounds");
                using (MemoryStream memory = new MemoryStream())
                using (BinaryWriter writer = new BinaryWriter(memory))
                {
                    writer.Write(Format);
                    writer.Write(city);
                    writer.Write(Roads.Count);
                    writer.Write(Lots.Count);
                    writer.Write(Residents.Count);
                    foreach (Road road in Roads.Values)
                    {
                        writer.Write(road.Source);
                        writer.Write(road.Tag);
                        writer.Write(road.Id);
                        writer.Write(road.Start);
                        writer.Write(road.End);
                        writer.Write(road.BuildIndex);
                    }
                    foreach (Lot lot in Lots.Values)
                    {
                        writer.Write(lot.Group);
                        writer.Write(lot.Ward);
                        writer.Write(lot.Id);
                        writer.Write(lot.Road);
                        writer.Write(lot.ZoneBlock);
                        writer.Write(lot.BuildIndex);
                        writer.Write((byte)lot.Zone);
                        writer.Write(lot.Members);
                        writer.Write((byte)lot.Row);
                        writer.Write((byte)lot.Width);
                        writer.Write((byte)lot.Length);
                    }
                    foreach (Android android in Residents.Values)
                    {
                        writer.Write(android.Id);
                        writer.Write(android.HomeUnit);
                        writer.Write(android.Home);
                        writer.Write(android.Battery);
                    }
                    writer.Flush();
                    if (memory.Length > MaxBytes)
                        throw new InvalidDataException("Native construction save exceeds the byte limit");
                    return memory.ToArray();
                }
            }
        }

        internal static void Load(byte[] payload)
        {
            lock (Gate)
                pending = payload == null || payload.Length == 0 ? null : Parse(payload);
        }

        private static FileData Parse(byte[] payload)
        {
            if (payload.Length > MaxBytes)
                throw new InvalidDataException("Native construction save exceeds the byte limit");
            using (MemoryStream memory = new MemoryStream(payload, false))
            using (BinaryReader reader = new BinaryReader(memory))
            {
                if (reader.ReadUInt32() != Format)
                    throw new InvalidDataException("Unsupported native city ledger data");
                string name = reader.ReadString();
                if (!name.StartsWith("NagoyaCitywide-", StringComparison.Ordinal) || name.Length > 150)
                    throw new InvalidDataException("Invalid native city ledger name");
                int roadCount = reader.ReadInt32(), lotCount = reader.ReadInt32(), residents = reader.ReadInt32();
                if (roadCount < 0 || roadCount > MaxRoads || lotCount < 0 || lotCount > MaxParcels ||
                    residents < 0 || residents > MaxResidents)
                    throw new InvalidDataException("Invalid native construction record counts");
                Road[] roads = new Road[roadCount];
                Lot[] lots = new Lot[lotCount];
                Android[] androids = new Android[residents];
                HashSet<ushort> seen = new HashSet<ushort>();
                for (int i = 0; i < roads.Length; i++)
                {
                    Road road = new Road {
                        Source=reader.ReadString(), Tag=reader.ReadString(), Id=reader.ReadUInt16(),
                        Start=reader.ReadUInt16(), End=reader.ReadUInt16(), BuildIndex=reader.ReadUInt32()
                    };
                    if (!seen.Add(road.Id) || road.Id == 0 || road.Start == 0 || road.End == 0 ||
                        road.Source.Length > 100 || road.Tag.Length > 24)
                        throw new InvalidDataException("Invalid native road in saved city");
                    roads[i] = road;
                }
                seen.Clear();
                for (int i = 0; i < lots.Length; i++)
                {
                    Lot lot = new Lot {
                        Group=reader.ReadString(), Ward=reader.ReadString(), Id=reader.ReadUInt16(),
                        Road=reader.ReadUInt16(), ZoneBlock=reader.ReadUInt16(), BuildIndex=reader.ReadUInt32(),
                        Zone=(ItemClass.Zone)reader.ReadByte(), Members=reader.ReadInt32(),
                        Row=reader.ReadByte(), Width=reader.ReadByte(), Length=reader.ReadByte()
                    };
                    if (!seen.Add(lot.Id) || lot.Id == 0 || lot.Road == 0 || lot.ZoneBlock == 0 ||
                        lot.Width < 1 || lot.Width > 4 || lot.Length < 1 || lot.Length > 4 || lot.Row + lot.Width > 8 ||
                        lot.Members < 1 || lot.Members > 738178 || lot.Group.Length > 140 || lot.Ward.Length > 40)
                        throw new InvalidDataException("Invalid native lot in saved city");
                    lots[i] = lot;
                }
                HashSet<uint> seenAndroids = new HashSet<uint>();
                for (int i = 0; i < androids.Length; i++)
                {
                    Android android = new Android {
                        Id=reader.ReadUInt32(), HomeUnit=reader.ReadUInt32(), Home=reader.ReadUInt16(), Battery=reader.ReadSingle()
                    };
                    if (!seenAndroids.Add(android.Id) || android.Id == 0 || android.HomeUnit == 0 || android.Home == 0 ||
                        Single.IsNaN(android.Battery) || android.Battery < 0 || android.Battery > 100)
                        throw new InvalidDataException("Invalid native Android in saved city");
                    androids[i] = android;
                }
                if (memory.Position != memory.Length)
                    throw new InvalidDataException("Unexpected trailing data in native city ledger");
                return new FileData {City=name, Roads=roads, Lots=lots, Residents=androids};
            }
        }
    }
}
