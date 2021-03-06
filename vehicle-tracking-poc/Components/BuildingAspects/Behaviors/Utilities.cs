﻿using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization.Formatters.Binary;
using System.Text;

namespace BuildingAspects.Behaviors
{
    public static class Utilities
    {
        private const string MandatoryParam = "Parameter is manadatory!";

        public static IDictionary<string, object> ToDictionary(this object source)
        {
            var fields = source.GetType().GetFields(
                BindingFlags.GetField |
                BindingFlags.Public |
                BindingFlags.Instance).ToDictionary
            (
                propInfo => propInfo.Name,
                propInfo => propInfo.GetValue(source) ?? string.Empty
            );

            var properties = source.GetType().GetProperties(
                BindingFlags.GetField |
                BindingFlags.GetProperty |
                BindingFlags.Public |
                BindingFlags.Instance).ToDictionary
            (
                propInfo => propInfo.Name,
                propInfo => propInfo.GetValue(source, null) ?? string.Empty
            );

            return fields.Concat(properties).ToDictionary(key => key.Key, value => value.Value); ;
        }
        public static bool EqualsByValue(this object source, object destination)
        {
            var firstDic = source.ToFlattenDictionary();
            var secondDic = destination.ToFlattenDictionary();
            if (firstDic.Count != secondDic.Count)
                return false;
            if (firstDic.Keys.Except(secondDic.Keys).Any())
                return false;
            if (secondDic.Keys.Except(firstDic.Keys).Any())
                return false;
            return firstDic.All(pair =>
              pair.Value.Equals(secondDic[pair.Key])
            );
        }
        public static bool IsAnonymousType(this object instance)
        {

            if (instance == null)
                return false;

            return instance.GetType().Namespace == null;
        }
        public static IDictionary<string, object> ToFlattenDictionary(this object source, string parentPropertyKey = null, IDictionary<string, object> parentPropertyValue = null)
        {
            var propsDic = parentPropertyValue ?? new Dictionary<string, object>();
            foreach (var item in source.ToDictionary())
            {
                var key = string.IsNullOrEmpty(parentPropertyKey) ? item.Key : $"{parentPropertyKey}.{item.Key}";
                if (item.Value.IsAnonymousType())
                    return item.Value.ToFlattenDictionary(key, propsDic);
                else
                    propsDic.Add(key, item.Value);
            }
            return propsDic;
        }

        public static byte[] JsonBinarySerialize(object instance)
        {
            if (instance == null) throw new ArgumentNullException(MandatoryParam);
            return Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(instance, Defaults.JsonSerializerSettings));
        }
        public static T JsonBinaryDeserialize<T>(byte[] objAsBinary)
        {
            if (objAsBinary == null) throw new ArgumentNullException(MandatoryParam);
            return JsonConvert.DeserializeObject<T>(Encoding.UTF8.GetString(objAsBinary), Defaults.JsonSerializerSettings);
        }
        public static byte[] BinarySerialize(object instance)
        {
            if (instance == null) throw new ArgumentNullException(MandatoryParam);
            byte[] binObjSource;
            var formatter = new BinaryFormatter();
            using (var memory = new MemoryStream())
            {
                formatter.Serialize(memory, instance);
                binObjSource = memory.ToArray();
            }
            return binObjSource;
        }
        public static object BinaryDeserialize(byte[] objAsBinary)
        {
            if (objAsBinary == null) throw new ArgumentNullException(MandatoryParam);
            if (objAsBinary.Length == 0) return null;
            var formatter = new BinaryFormatter();
            using (var memory = new MemoryStream(objAsBinary))
            {
                return formatter.Deserialize(memory);
            }
        }
    }
}

