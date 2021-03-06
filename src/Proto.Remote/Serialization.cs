﻿// -----------------------------------------------------------------------
//   <copyright file="Serialization.cs" company="Asynkron HB">
//       Copyright (C) 2015-2018 Asynkron HB All rights reserved
//   </copyright>
// -----------------------------------------------------------------------

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Google.Protobuf;
using Google.Protobuf.Reflection;

namespace Proto.Remote
{
    public interface ISerializer
    {
        ByteString Serialize(object obj);
        object Deserialize(ByteString bytes, string typeName);
        string GetTypeName(object message);

        bool CanSerialize(object obj);
    }

    public class JsonSerializer : ISerializer
    {
        private readonly Serialization _serialization;

        public JsonSerializer(Serialization serialization)
        {
            _serialization = serialization;
        }
        public ByteString Serialize(object obj)
        {
            if (obj is JsonMessage jsonMessage)
            {
                return ByteString.CopyFromUtf8(jsonMessage.Json);
            }

            var message = obj as IMessage;
            var json = JsonFormatter.Default.Format(message);
            return ByteString.CopyFromUtf8(json);
        }

        public object Deserialize(ByteString bytes, string typeName)
        {
            var json = bytes.ToStringUtf8();
            var parser = _serialization.TypeLookup[typeName];

            var o = parser.ParseJson(json);
            return o;
        }

        public string GetTypeName(object obj)
        {
            if (obj is JsonMessage jsonMessage)
            {
                return jsonMessage.TypeName;
            }

            var message = obj as IMessage;
            if (message == null)
            {
                throw new ArgumentException("obj must be of type IMessage", nameof(obj));
            }
            return message.Descriptor.File.Package + "." + message.Descriptor.Name;
        }

        public bool CanSerialize(object obj) => true;
    }

    public class ProtobufSerializer : ISerializer
    {
        private readonly Serialization _serialization;

        public ProtobufSerializer(Serialization serialization)
        {
            _serialization = serialization;
        }
        public ByteString Serialize(object obj)
        {
            var message = obj as IMessage;
            return message.ToByteString();
        }

        public object Deserialize(ByteString bytes, string typeName)
        {
            var parser = _serialization.TypeLookup[typeName];
            var o = parser.ParseFrom(bytes);
            return o;
        }

        public string GetTypeName(object obj)
        {
            var message = obj as IMessage;
            if (message == null)
            {
                throw new ArgumentException("obj must be of type IMessage", nameof(obj));
            }
            return message.Descriptor.File.Package + "." + message.Descriptor.Name;
        }

        public bool CanSerialize(object obj) => obj is IMessage;
    }

    public class Serialization
    {
        internal readonly Dictionary<string, MessageParser> TypeLookup = new Dictionary<string, MessageParser>();

        struct SerializerItem
        {
            public int SerializerId;
            public int PriorityValue;
            public ISerializer Serializer;
        }
        private List<SerializerItem> Serializers = new List<SerializerItem>();

        struct TypeSerializerItem
        {
            public ISerializer Serializer;
            public int SerializerId;
        }
        readonly ConcurrentDictionary<Type, TypeSerializerItem> SerializerLookup = new ConcurrentDictionary<Type, TypeSerializerItem>();

        public Serialization()
        {
            RegisterFileDescriptor(Proto.ProtosReflection.Descriptor);
            RegisterFileDescriptor(ProtosReflection.Descriptor);
            RegisterSerializer(
                0,
                priority: 0,
                new ProtobufSerializer(this));
        }

        /// <summary>
        /// Protobuf has priority of 0, and protobuf JSON has priority of -1000.
        /// </summary>
        public void RegisterSerializer(
            int serializerId,
            int priority,
            ISerializer serializer)
        {
            if (Serializers.Any(v => v.SerializerId == serializerId))
                throw new Exception($"Already registered serializer id: {serializerId} = {Serializers[serializerId].GetType()}");

            Serializers.Add(new SerializerItem()
            {
                SerializerId = serializerId,
                PriorityValue = priority,
                Serializer = serializer,
            });
            // Sort by PriorityValue, from highest to lowest.
            Serializers = Serializers
                .OrderByDescending(v => v.PriorityValue)
                .ToList();
        }

        public void RegisterFileDescriptor(FileDescriptor fd)
        {
            foreach (var msg in fd.MessageTypes)
            {
                var name = fd.Package + "." + msg.Name;
                TypeLookup.Add(name, msg.Parser);
            }
        }

        public ByteString Serialize(object message, out string typename, out int serializerId)
        {
            var serializer = FindSerializerToUse(message);
            typename = serializer.Serializer.GetTypeName(message);
            serializerId = serializer.SerializerId;
            return serializer.Serializer.Serialize(message);
        }

        TypeSerializerItem FindSerializerToUse(object message)
        {
            var type = message.GetType();
            TypeSerializerItem serializer;
            if (SerializerLookup.TryGetValue(type, out serializer))
                return serializer;

            // Check if the default serializer can serialize this object.
            foreach (var serializerItem in Serializers)
            {
                if (serializerItem.Serializer.CanSerialize(message))
                {
                    var item = new TypeSerializerItem()
                    {
                        Serializer = serializerItem.Serializer,
                        SerializerId = serializerItem.SerializerId,
                    };
                    SerializerLookup[type] = item;
                    return item;
                }
            }
            throw new Exception($"Couldn't find a serializer for {message.GetType()}");
        }

        public object Deserialize(string typeName, ByteString bytes, int serializerId)
        {
            foreach (var serializerItem in Serializers)
                if (serializerItem.SerializerId == serializerId)
                    return serializerItem.Serializer.Deserialize(
                        bytes,
                        typeName);
            throw new Exception($"Couldn't find serializerId: {serializerId} for typeName: {typeName}");
        }
    }
}