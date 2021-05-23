// Decompiled with JetBrains decompiler
// Type: Torch.SharedLibrary.Utils.ConfigUtils
// Assembly: SKO-GridPCULimiter, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null
// MVID: 763A9EB8-F42B-4B0E-ACDE-740B34BF1092
// Assembly location: C:\Users\idesp\Downloads\sko-gridpculimiter-v1.2.1\SKO-GridPCULimiter.dll

using System;
using System.IO;
using System.Xml.Serialization;
using Torch;

namespace SentisOptimisations
{
    public static class ConfigUtils
    {
        public static T Load<T>(TorchPluginBase plugin, string fileName) where T : new()
        {
            string path = Path.Combine(plugin.StoragePath, fileName);
            T data = new T();
            if (File.Exists(path))
            {
                using (StreamReader streamReader = new StreamReader(path))
                    data = (T) new XmlSerializer(typeof(T)).Deserialize((TextReader) streamReader);
            }
            else
                Save<T>(plugin, data, fileName);

            return data;
        }

        public static bool Save<T>(TorchPluginBase plugin, T data, string fileName) where T : new()
        {
            try
            {
                using (StreamWriter streamWriter = new StreamWriter(Path.Combine(plugin.StoragePath, fileName)))
                    new XmlSerializer(typeof(T)).Serialize((TextWriter) streamWriter, (object) data);
                return true;
            }
            catch (Exception ex)
            {
            }

            return false;
        }
    }
}