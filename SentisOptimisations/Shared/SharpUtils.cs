using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using Sandbox.ModAPI;
using VRage;
using VRage.Game.ModAPI;

namespace NAPI
{
    public static class SharpUtils
    {
        static Regex SmartReplaceRegex = new Regex("{([a-zA-Z0-9\\-])}");
        public static string SmartReplace (string what, Func<string, string> replacer)
        {
            return SmartReplaceRegex.Replace(what, (match) => {
                var name = match.Groups[1].Value;
                return replacer(name);
            });
        }

        public static void Increment(this Dictionary<long, int> dict, long key, int am = 1)
        {
            if (!dict.ContainsKey(key)) { dict.Add(key, am); } else { dict[key] += am; }
        }

        public static StringBuilder AppendMany(this StringBuilder sb, params object[] data)
        {
            foreach (var x in data) { sb.Append(x); }

            return sb;
        }


        public static void AddOrRemove<T>(this HashSet<T> set, T data, bool add)
        {
            if (add) { set.Add(data); } else { set.Remove(data); }
        }


        private static DateTime utcZero = new DateTime(1970, 1, 1);


        public static long timeStamp() { return (long)(DateTime.UtcNow.Subtract(utcZero)).TotalSeconds; }
        public static long msTimeStamp() { return (long)(DateTime.UtcNow.Subtract(utcZero)).TotalMilliseconds; }


        public static string printContent<T, K>(this Dictionary<T, K> dict)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("Dict[");
            foreach (var x in dict) { sb.Append(x.Key).Append("->").Append(x.Value).Append("\n"); }

            sb.Append("]");
            return sb.ToString();
        }


        public static string toHumanQuantity(this double num)
        {
            if (num < 1000) return String.Format("{0:N2}", num);
            if (num < 1000000) return String.Format("{0:N2} K", num / 1000);
            if (num < 1000000000) return String.Format("{0:N2} M", num / 1000000);
            return "TONS";
        }

        public static string toHumanQuantityCeiled(this double num)
        {
            if (num < 1000) return String.Format("{0:N0}", num);
            if (num < 1000000) return String.Format("{0:N2} K", num / 1000);
            if (num < 1000000000) return String.Format("{0:N2} M", num / 1000000);
            return "TONS";
        }


        public static string toHumanTime(this double num)
        {
            if (num < 120) return String.Format("{0:N0} s", num);
            if (num < 3600) return String.Format("{0:N0} min", num / 60);
            if (num < 3600 * 24) return String.Format("{0:N0} h", num / 3600);
            return String.Format("{0:N0} days", num / 3600 / 24);
        }

        public static string fixZero(this double num) { return String.Format("{0:N2}", num); }


        public static void Sum<T>(this Dictionary<T, double> dict, T key, double value)
        {
            if (!dict.ContainsKey(key)) { dict[key] = value; } else { dict[key] = dict[key] + value; }
        }

        public static void Sum<T>(this Dictionary<T, int> dict, T key, int value)
        {
            if (!dict.ContainsKey(key)) { dict[key] = value; } else { dict[key] = dict[key] + value; }
        }

        public static void Sum<T>(this Dictionary<T, MyFixedPoint> dict, T key, MyFixedPoint value)
        {
            if (!dict.ContainsKey(key)) { dict[key] = value; } else { dict[key] = dict[key] + value; }
        }


        public static void Sum<T>(this Dictionary<T, MyFixedPoint> dict, Dictionary<T, MyFixedPoint> other)
        {
            foreach (var x in other)
            {
                dict.Sum(x.Key, x.Value);
            }
        }

        public static void Sum<T>(this Dictionary<T, double> dict, T key, int value)
        {
            if (!dict.ContainsKey(key)) { dict[key] = value; } else { dict[key] = dict[key] + value; }
        }

        public static string printContent<T>(this List<T> dict)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("List[");
            foreach (var x in dict) { sb.Append(x).Append(",\n"); }

            sb.Append("]");
            return sb.ToString();
        }


        /*public static byte[] toBytes(this List<long> list) {
            using (MemoryStream ms = new MemoryStream()) {
                using (BinaryWriter bw = new BinaryWriter(ms)) {
                    if (list == null) { bw.Write (-1); }
                    bw.Write (list.Count);
                    foreach (var x in list) {
                        bw.Write (x);
                    }
                    bw.Flush();
                    return ms.ToArray();
                }
            }
        }

        public static List<long> toListLong(this byte[] bytes) {
            using (MemoryStream ms = new MemoryStream(bytes)) {
                using (BinaryReader bw = new BinaryReader(ms)) {

                    var count = bw.ReadInt32 ();
                    if (count == -1) return null;

                    var list = new List<long>();
                    for (var x=0; x<count; x++) {
                        var l = bw.ReadInt64 ();
                        list.Add (l);
                    }
                    return list;
                }
            }
        }*/

        public static string printContent(this List<IMyPlayer> dict)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("List[");
            foreach (var x in dict) { sb.Append(x.DisplayName + "/" + x.PlayerID).Append(", "); }

            sb.Append("]");
            return sb.ToString();
        }


        public static StringBuilder Append(this StringBuilder sb, IMyPlayer player, IMyFaction faction)
        {
            sb.Append(player.DisplayName);
            if (faction != null) { sb.Append("[").Append(faction.Tag).Append("]"); }

            return sb;
        }

        public static StringBuilder Append(this StringBuilder sb, IMyIdentity player, IMyFaction faction)
        {
            sb.Append(player.DisplayName);
            if (faction != null) { sb.Append("[").Append(faction.Tag).Append("]"); }

            return sb;
        }

        public static string printContent(this List<IMyFaction> dict)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("List[");
            foreach (var x in dict) { sb.Append(x.Name + "/" + x.FactionId).Append(", "); }

            sb.Append("]");
            return sb.ToString();
        }


        public static string printContent(this List<MyProductionQueueItem> dict)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("List[");
            foreach (var x in dict) { sb.Append("{").Append(x.ItemId).Append("/").Append(x.Amount).Append("/").Append(x.Blueprint).Append("},\n"); }

            sb.Append("]");
            return sb.ToString();
        }


        public static K GetOr<T, K>(this Dictionary<T, K> dict, T t, K k)
        {
            if (dict.ContainsKey(t)) { return dict[t]; } else { return k; }
        }

        public static List<K> GetOrCreate<T, K>(this Dictionary<T, List<K>> dict, T t)
        {
            if (!dict.ContainsKey(t)) { dict.Add(t, new List<K>()); }

            return dict[t];
        }

        public static HashSet<K> GetOrCreate<T, K>(this Dictionary<T, HashSet<K>> dict, T t)
        {
            if (!dict.ContainsKey(t)) { dict.Add(t, new HashSet<K>()); }

            return dict[t];
        }

        public static Dictionary<K, V> GetOrCreate<T, K, V>(this Dictionary<T, Dictionary<K, V>> dict, T t)
        {
            if (!dict.ContainsKey(t)) { dict.Add(t, new Dictionary<K, V>()); }

            return dict[t];
        }

        public static K Set<T, K>(this Dictionary<T, K> dict, T t, K k)
        {
            K old = default(K);
            if (dict.ContainsKey(t))
            {
                old = dict[t];
                dict.Remove(t);
            }

            dict.Add(t, k);
            return old;
        }
    }
}