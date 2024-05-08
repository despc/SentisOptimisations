using System.Collections.Generic;

namespace NAPI
{
    public static class SharpUtils
    {
        public static void Sum<T>(this Dictionary<T, int> dict, T key, int value)
        {
            if (!dict.ContainsKey(key))
            {
                dict[key] = value;
            }
            else
            {
                dict[key] = dict[key] + value;
            }
        }
    }
}