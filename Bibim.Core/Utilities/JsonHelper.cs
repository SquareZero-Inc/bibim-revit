// Copyright (c) 2026 SquareZero Inc. â€” Licensed under Apache 2.0. See LICENSE in the repo root.
using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;

namespace Bibim.Core
{
    /// <summary>
    /// JSON helper using Newtonsoft.Json for both .NET 4.8 and .NET 8.
    /// Avoids System.Text.Json assembly loading issues in Revit host process.
    /// </summary>
    public static class JsonHelper
    {
        public static string Serialize<T>(T obj, bool indented = false)
        {
            if (obj == null) return null;
            return JsonConvert.SerializeObject(obj, indented ? Formatting.Indented : Formatting.None);
        }

        public static T Deserialize<T>(string json)
        {
            if (string.IsNullOrEmpty(json)) return default;
            return JsonConvert.DeserializeObject<T>(json);
        }

        public static string SerializeCamelCase<T>(T obj, bool indented = false)
        {
            if (obj == null) return null;
            var settings = new JsonSerializerSettings
            {
                ContractResolver = new CamelCasePropertyNamesContractResolver(),
                Formatting = indented ? Formatting.Indented : Formatting.None
            };
            return JsonConvert.SerializeObject(obj, settings);
        }

        public static bool TryDeserialize<T>(string json, out T result)
        {
            try { result = Deserialize<T>(json); return true; }
            catch { result = default; return false; }
        }

        public static object ParseDynamic(string json)
        {
            if (string.IsNullOrEmpty(json)) return null;
            return JObject.Parse(json);
        }
    }
}
