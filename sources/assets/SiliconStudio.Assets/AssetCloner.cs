﻿// Copyright (c) 2014 Silicon Studio Corp. (http://siliconstudio.co.jp)
// This file is distributed under GPL v3. See LICENSE.md for details.

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using SiliconStudio.Core;
using SiliconStudio.Core.Reflection;
using SiliconStudio.Core.Serialization;
using SiliconStudio.Core.Serialization.Contents;
using SiliconStudio.Core.Storage;
using SiliconStudio.Core.Yaml;

namespace SiliconStudio.Assets
{
    /// <summary>
    /// Allows to clone an asset or values stored in an asset.
    /// </summary>
    public class AssetCloner
    {
        private readonly AssetClonerFlags flags;
        private readonly object streamOrValueType;

        private readonly List<object> invariantObjects;
        private readonly object[] objectReferences;
        private readonly Dictionary<object, object> clonedObjectMapping;
        private Dictionary<Guid, Guid> cloningIdRemapping;
        public static SerializerSelector ClonerSelector { get; internal set; }
        public static PropertyKey<List<object>> InvariantObjectListProperty = new PropertyKey<List<object>>("InvariantObjectList", typeof(AssetCloner));

        static AssetCloner()
        {
            ClonerSelector = new SerializerSelector(true, "Default", "Content", "AssetClone");
            ClonerSelector.SerializerFactories.Add(new GenericSerializerFactory(typeof(IUnloadable), typeof(UnloadableCloneSerializer<>)));
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="AssetCloner" /> struct.
        /// </summary>
        /// <param name="value">The value to clone.</param>
        /// <param name="flags">Cloning flags</param>
        private AssetCloner(object value, AssetClonerFlags flags)
        {
            this.flags = flags;
            invariantObjects = null;
            objectReferences = null;
            clonedObjectMapping = new Dictionary<object, object>();
            cloningIdRemapping = null;
            // Clone only if value is not a value type
            if (value != null && !value.GetType().IsValueType)
            {
                invariantObjects = new List<object>();

                // TODO: keepOnlySealedOverride is currently ignored
                // TODO Clone is not supporting SourceCodeAsset (The SourceCodeAsset.Text won't be cloned)

                var stream = new MemoryStream();
                var writer = new BinarySerializationWriter(stream);
                writer.Context.SerializerSelector = ClonerSelector;
                var refFlag = (flags & AssetClonerFlags.ReferenceAsNull) != 0
                    ? ContentSerializerContext.AttachedReferenceSerialization.AsNull
                    : ContentSerializerContext.AttachedReferenceSerialization.AsSerializableVersion;
                writer.Context.Set(InvariantObjectListProperty, invariantObjects);
                writer.Context.Set(ContentSerializerContext.SerializeAttachedReferenceProperty, refFlag);
                writer.SerializeExtended(value, ArchiveMode.Serialize);
                writer.Flush();

                // Retrieve back all object references that were discovered while serializing
                // They will be used layer by OnObjectDeserialized when cloning ShadowObject datas
                var objectRefs = writer.Context.Get(MemberSerializer.ObjectSerializeReferences);
                if (objectRefs != null)
                {
                    // Remap object references to a simple array
                    objectReferences = new object[objectRefs.Count];
                    foreach (var objRef in objectRefs)
                    {
                        objectReferences[objRef.Value] = objRef.Key;
                    }
                }

                streamOrValueType = stream;
            }
            else
            {
                streamOrValueType = value;
            }
        }

        /// <summary>
        /// Clones the current value of this cloner with the specified new shadow registry (optional)
        /// </summary>
        /// <param name="idRemapping">A dictionary containing the remapping of <see cref="IIdentifiable.Id"/> if <see cref="AssetClonerFlags.GenerateNewIdsForIdentifiableObjects"/> has been passed to the cloner.</param>
        /// <returns>A clone of the value associated with this cloner.</returns>
        private object Clone(out Dictionary<Guid, Guid> idRemapping)
        {
            var stream = streamOrValueType as Stream;
            if (stream != null)
            {
                stream.Position = 0;
                var reader = new BinarySerializationReader(stream);
                reader.Context.SerializerSelector = ClonerSelector;
                var refFlag = (flags & AssetClonerFlags.ReferenceAsNull) != 0
                    ? ContentSerializerContext.AttachedReferenceSerialization.AsNull
                    : ContentSerializerContext.AttachedReferenceSerialization.AsSerializableVersion;
                reader.Context.Set(InvariantObjectListProperty, invariantObjects);
                reader.Context.Set(ContentSerializerContext.SerializeAttachedReferenceProperty, refFlag);
                reader.Context.Set(MemberSerializer.ObjectDeserializeCallback, OnObjectDeserialized);
                object newObject = null;
                reader.SerializeExtended(ref newObject, ArchiveMode.Deserialize);

                if ((flags & AssetClonerFlags.RemoveUnloadableObjects) != 0)
                {
                    UnloadableObjectRemover.Run(newObject);
                }

                idRemapping = cloningIdRemapping;
                return newObject;
            }
            // Else this is a value type, so it is cloned automatically
            idRemapping = null;
            return streamOrValueType;
        }

        private ObjectId GetHashId()
        {
            // This methods use the stream that is already filled-up by the standard binary serialization of the object
            // Here we add ids and overrides metadata informations to the stream in order to calculate an accurate id
            var stream = streamOrValueType as MemoryStream;
            if (stream != null)
            {
                // ------------------------------------------------------
                // Un-comment the following code to debug the ObjectId of the serialized version without taking into account overrides
                // ------------------------------------------------------
                //var savedPosition = stream.Position;
                //stream.Position = 0;
                //var intermediateHashId = ObjectId.FromBytes(stream.ToArray());
                //stream.Position = savedPosition;

                var writer = new BinarySerializationWriter(stream);

                // Write invariant objects
                foreach (var invarialtObject in invariantObjects)
                {
                    writer.SerializeExtended(invarialtObject, ArchiveMode.Serialize);
                }

                writer.Flush();
                stream.Position = 0;

                return ObjectId.FromBytes(stream.ToArray());
            }

            return ObjectId.Empty;
        }

        private void OnObjectDeserialized(int i, object newObject)
        {
            if (objectReferences != null && newObject != null)
            {
                var previousObject = objectReferences[i];

                //// If the object is an attached reference, there is no need to copy the shadow object
                //if (AttachedReferenceManager.GetAttachedReference(previousObject) != null)
                //{
                //    return;
                //}

                ShadowObject.Copy(previousObject, newObject);

                // NOTE: we don't use Add because of strings that might be duplicated
                clonedObjectMapping[previousObject] = newObject;

                if ((flags & AssetClonerFlags.RemoveItemIds) != AssetClonerFlags.RemoveItemIds)
                {
                    CollectionItemIdentifiers sourceIds;
                    if (CollectionItemIdHelper.TryGetCollectionItemIds(previousObject, out sourceIds))
                    {
                        var newIds = CollectionItemIdHelper.GetCollectionItemIds(newObject);
                        sourceIds.CloneInto(newIds, clonedObjectMapping);
                    }
                }

                if ((flags & AssetClonerFlags.GenerateNewIdsForIdentifiableObjects) == AssetClonerFlags.GenerateNewIdsForIdentifiableObjects)
                {
                    var identifiable = newObject as IIdentifiable;
                    if (identifiable != null)
                    {
                        cloningIdRemapping = cloningIdRemapping ?? new Dictionary<Guid, Guid>();
                        var newId = Guid.NewGuid();
                        cloningIdRemapping[identifiable.Id] = newId;
                        identifiable.Id = newId;
                    }
                }
            }
        }

        /// <summary>
        /// Clones the specified asset using asset serialization.
        /// </summary>
        /// <param name="asset">The asset.</param>
        /// <param name="flags">Flags used to control the cloning process</param>
        /// <param name="idRemapping">A dictionary containing the remapping of <see cref="IIdentifiable.Id"/> if <see cref="AssetClonerFlags.GenerateNewIdsForIdentifiableObjects"/> has been passed to the cloner.</param>
        /// <returns>A clone of the asset.</returns>
        public static object Clone(object asset, AssetClonerFlags flags, out Dictionary<Guid, Guid> idRemapping)
        {
            if (asset == null)
            {
                idRemapping = null;
                return null;
            }
            var cloner = new AssetCloner(asset, flags);
            var newObject = cloner.Clone(out idRemapping);
            return newObject;
        }

        /// <summary>
        /// Clones the specified asset using asset serialization.
        /// </summary>
        /// <param name="asset">The asset.</param>
        /// <param name="flags">Flags used to control the cloning process</param>
        /// <returns>A clone of the asset.</returns>
        public static object Clone(object asset, AssetClonerFlags flags = AssetClonerFlags.None)
        {
            if (asset == null)
            {
                return null;
            }
            var cloner = new AssetCloner(asset, flags);
            Dictionary<Guid, Guid> idMapping;
            var newObject = cloner.Clone(out idMapping);
            return newObject;
        }

        /// <summary>
        /// Clones the specified asset using asset serialization.
        /// </summary>
        /// <typeparam name="T">The type of the asset.</typeparam>
        /// <param name="asset">The asset.</param>
        /// <param name="flags">Flags used to control the cloning process</param>
        /// <returns>A clone of the asset.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static T Clone<T>(T asset, AssetClonerFlags flags = AssetClonerFlags.None)
        {
            Dictionary<Guid, Guid> idRemapping;
            return Clone(asset, flags, out idRemapping);
        }

        /// <summary>
        /// Clones the specified asset using asset serialization.
        /// </summary>
        /// <typeparam name="T">The type of the asset.</typeparam>
        /// <param name="asset">The asset.</param>
        /// <param name="flags">Flags used to control the cloning process</param>
        /// <param name="idRemapping">A dictionary containing the remapping of <see cref="IIdentifiable.Id"/> if <see cref="AssetClonerFlags.GenerateNewIdsForIdentifiableObjects"/> has been passed to the cloner.</param>
        /// <returns>A clone of the asset.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static T Clone<T>(T asset, AssetClonerFlags flags, out Dictionary<Guid, Guid> idRemapping)
        {
            return (T)Clone((object)asset, flags, out idRemapping);
        }

        /// <summary>
        /// Generates a runtime hash id from the serialization of this asset.
        /// </summary>
        /// <param name="asset">The asset to get the runtime hash id</param>
        /// <param name="flags">Flags used to control the serialization process</param>
        /// <returns>An object id</returns>
        internal static ObjectId ComputeHash(object asset, AssetClonerFlags flags = AssetClonerFlags.None)
        {
            if (asset == null)
            {
                return ObjectId.Empty;
            }

            var cloner = new AssetCloner(asset, flags);
            var result = cloner.GetHashId();
            return result;
        }

        class UnloadableCloneSerializer<T> : DataSerializer<T> where T : class, IUnloadable
        {
            private DataSerializer parentSerializer;

            public override void Initialize(SerializerSelector serializerSelector)
            {
                parentSerializer = serializerSelector.GetSerializer(typeof(T).BaseType);
            }

            public override void PreSerialize(ref T obj, ArchiveMode mode, SerializationStream stream)
            {
                var invariantObjectList = stream.Context.Get(InvariantObjectListProperty);
                if (mode == ArchiveMode.Serialize)
                {
                    stream.Write(invariantObjectList.Count);
                    invariantObjectList.Add(obj);
                }
                else
                {
                    var index = stream.Read<int>();

                    if (index >= invariantObjectList.Count)
                    {
                        throw new InvalidOperationException($"The type [{typeof(T).FullName}] cannot be only be used for clone serialization");
                    }

                    var invariant = invariantObjectList[index] as T;
                    if (invariant == null)
                    {
                        throw new InvalidOperationException($"Unexpected null {typeof(T).FullName} while cloning");
                    }

                    // Create a new object to avoid exception in case its identity is important
                    obj = (T)Activator.CreateInstance(typeof(T), invariant.TypeName, invariant.AssemblyName, invariant.Error, invariant.ParsingEvents);
                }
            }

            public override void Serialize(ref T obj, ArchiveMode mode, SerializationStream stream)
            {
                // Process with parent serializer first
                object parentObj = obj;
                parentSerializer?.Serialize(ref parentObj, mode, stream);
                obj = (T)parentObj;
            }
        }
    }
}
