using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using JetBrains.Annotations;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Anatawa12.AvatarOptimizer.PrefabSafeSet
{
    internal static class PrefabSafeSetRuntimeUtil
    {
        public static void ResizeArray<T>(ref T[] array, int size) where T : new()
        {
            var source = array;
            var result = new T[size];
            Array.Copy(source, result, Math.Min(size, source.Length));
            for (var i = source.Length; i < result.Length; i++)
                result[i] = new T();
            array = result;
        }

        internal static bool IsNull<T>(this T arg)
        {
            if (arg == null) return true;
            if (typeof(Object).IsAssignableFrom(typeof(T)))
                return (Object)(object)arg == null;
            return false;
        }

        internal static bool IsNotNull<T>(this T arg) => !arg.IsNull();

        internal static Transform GetPrefabRoot(this Object obj) =>
            (obj is GameObject go ? go.transform : ((Component)obj).transform).root;

#if UNITY_EDITOR
        private static readonly Type OnBeforeSerializeImplType;

        static PrefabSafeSetRuntimeUtil()
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                OnBeforeSerializeImplType =
                    assembly.GetType("Anatawa12.AvatarOptimizer.PrefabSafeSet.OnBeforeSerializeImpl`2");
                if (OnBeforeSerializeImplType != null) return;
            }
        }

        public static MethodInfo GetOnBeforeSerializeCallbackMethod(Type tType, Type tLayerType, Type setType)
        {
            var implType = OnBeforeSerializeImplType.MakeGenericType(tType, tLayerType);
            return implType.GetMethod("Impl", BindingFlags.Public | BindingFlags.Static, null, new[] { setType }, null);
        }
#endif
    }


    /// <summary>
    /// The serializable class to express hashset.
    /// using array will make prefab modifications too big so I made this class
    /// </summary>
    /// <typeparam name="T">Element Type</typeparam>
    /// <typeparam name="TLayer">Layer Type</typeparam>
    [Serializable]
    public class PrefabSafeSet<T, TLayer> : ISerializationCallbackReceiver where TLayer : PrefabLayer<T>, new()
    {
        [SerializeField] internal T[] mainSet = Array.Empty<T>();
        [SerializeField] internal TLayer[] prefabLayers = Array.Empty<TLayer>();

#if UNITY_EDITOR
        [SerializeField, HideInInspector] internal T fakeSlot;
        [CanBeNull] internal Object[] MainSetCorrespondingObjects;
        internal readonly Object OuterObject;
        internal T[] CheckedCurrentLayerRemoves;
        internal T[] CheckedCurrentLayerAdditions;
        private static MethodInfo _onBeforeSerializeCallback = PrefabSafeSetRuntimeUtil
            .GetOnBeforeSerializeCallbackMethod(typeof(T), typeof(TLayer), typeof(PrefabSafeSet<T, TLayer>));
#endif

        protected PrefabSafeSet(Object outerObject)
        {
#if UNITY_EDITOR
            if (!outerObject) throw new ArgumentNullException(nameof(outerObject));
            OuterObject = outerObject;
#endif
        }

        public HashSet<T> GetAsSet()
        {
            var result = new HashSet<T>(mainSet.Where(x => x.IsNotNull()));
            foreach (var layer in prefabLayers)
                layer.ApplyTo(result);
            return result;
        }

        public List<T> GetAsList()
        {
            var result = new List<T>(mainSet.Where(x => x.IsNotNull()));
            var set = new HashSet<T>(result);
            foreach (var layer in prefabLayers)
                layer.ApplyTo(set, result);
            return result;
        }

        void ISerializationCallbackReceiver.OnBeforeSerialize()
        {
#if UNITY_EDITOR
            _onBeforeSerializeCallback.Invoke(null, new object[] {this});
            SetCorrespondingArray();
#endif
        }

        void ISerializationCallbackReceiver.OnAfterDeserialize()
        {
            SetCorrespondingArray();
        }

        void SetCorrespondingArray() {
#if UNITY_EDITOR
            if (typeof(Object).IsAssignableFrom(typeof(T)))
            {
                UnityEditor.EditorApplication.delayCall += () =>
                {
                    var outerObjectCorrespondingRoot = UnityEditor.PrefabUtility
                        .GetCorrespondingObjectFromSource(OuterObject)?.GetPrefabRoot();

                    void GetCorrespondingObjects(T[] values, ref Object[] correspondingObjects)
                    {
                        if (correspondingObjects?.Length == values.Length) return;
                        correspondingObjects = values.Select(x =>
                            {
                                var asObj = (Object)(object)x;
                                if (asObj == null) return default;
                                var corresponding = UnityEditor.PrefabUtility.GetCorrespondingObjectFromSource(asObj);
                                if (corresponding == null) return default;
                                if (corresponding.GetPrefabRoot() != outerObjectCorrespondingRoot) return default;
                                return corresponding;
                            })
                            .ToArray();
                    }

                    GetCorrespondingObjects(mainSet, ref MainSetCorrespondingObjects);
                    foreach (var prefabLayer in prefabLayers)
                    {
                        GetCorrespondingObjects(prefabLayer.additions, ref prefabLayer.AdditionsCorrespondingObjects);
                        GetCorrespondingObjects(prefabLayer.removes, ref prefabLayer.RemovesCorrespondingObjects);
                    }
                };
            }
#endif
        }
    }

    [Serializable]
    public class PrefabLayer<T>
    {
        // if some value is in both removes and additions, the values should be added
        [SerializeField] internal T[] removes = Array.Empty<T>();
        [SerializeField] internal T[] additions = Array.Empty<T>();
#if UNITY_EDITOR
        internal Object[] RemovesCorrespondingObjects;
        internal Object[] AdditionsCorrespondingObjects;
#endif

        public void ApplyTo(HashSet<T> result, [CanBeNull] List<T> list = null)
        {
            foreach (var remove in removes)
                if (remove.IsNotNull() && result.Remove(remove))
                    list?.Remove(remove);
            foreach (var addition in additions)
                if (addition.IsNotNull() && result.Add(addition))
                    list?.Add(addition);
        }
    }
    
    internal readonly struct ListSet<T>
    {
        [NotNull] private readonly List<T> _list;
        [NotNull] private readonly HashSet<T> _set;
        public ListSet(T[] initialize)
        {
            _list = new List<T>(initialize);
            _set = new HashSet<T>(initialize);
        }

        public void AddRange(IEnumerable<T> values)
        {
            foreach (var value in values)
                if (value != null && _set.Add(value))
                    _list?.Add(value);
        }

        public void RemoveRange(IEnumerable<T> values)
        {
            foreach (var value in values)
                if (value != null && _set.Remove(value))
                    _list?.Remove(value);
        }

        public T[] ToArray() => _list.ToArray();
    }
}
