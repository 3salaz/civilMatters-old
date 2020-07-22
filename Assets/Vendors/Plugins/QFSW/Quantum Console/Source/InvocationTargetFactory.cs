using QFSW.QC.Comparators;
using QFSW.QC.Utilities;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using Object = UnityEngine.Object;

namespace QFSW.QC
{
    public static class InvocationTargetFactory
    {
        private static readonly Dictionary<(MonoTargetType, Type), object> TargetCache = new Dictionary<(MonoTargetType, Type), object>();

        public static IEnumerable<T> FindTargets<T>(MonoTargetType method) where T : MonoBehaviour
        {
            foreach (object target in FindTargets(typeof(T), method))
            {
                yield return target as T;
            }
        }

        public static IEnumerable<object> FindTargets(Type classType, MonoTargetType method)
        {
            switch (method)
            {
                case MonoTargetType.Single:
                {
                    Object target = Object.FindObjectOfType(classType);
                    return target == null ? Enumerable.Empty<object>() : target.Yield();
                }
                case MonoTargetType.SingleInactive:
                {
                    return WrapSingleCached(classType, method, type =>
                    {
                        return Resources.FindObjectsOfTypeAll(type)
                            .FirstOrDefault(x => !x.hideFlags.HasFlag(HideFlags.HideInHierarchy));
                    });
                }
                case MonoTargetType.All:
                {
                    return Object.FindObjectsOfType(classType)
                        .OrderBy(x => x.name, new AlphanumComparator());
                }
                case MonoTargetType.AllInactive:
                {
                    return Resources.FindObjectsOfTypeAll(classType)
                        .Where(x => !x.hideFlags.HasFlag(HideFlags.HideInHierarchy))
                        .OrderBy(x => x.name, new AlphanumComparator());
                }
                case MonoTargetType.Registry:
                {
                    return QuantumRegistry.GetRegistryContents(classType);
                }
                case MonoTargetType.Singleton:
                {
                    return GetSingletonInstance(classType).Yield();
                }
                default:
                {
                    throw new ArgumentException($"Unsupported MonoTargetType {method}");
                }
            }
        }

        private static IEnumerable<object> WrapSingleCached(Type classType, MonoTargetType method, Func<Type, object> targetFinder)
        {
            if (!TargetCache.TryGetValue((method, classType), out object target) || target as Object == null)
            {
                target = targetFinder(classType);
                TargetCache[(method, classType)] = target;
            }

            return target == null ? Enumerable.Empty<object>() : target.Yield();
        }

        public static object InvokeOnTargets(MethodInfo invokingMethod, IEnumerable<object> targets, object[] data)
        {
            int returnCount = 0;
            Dictionary<object, object> resultsParts = new Dictionary<object, object>();

            foreach (object target in targets)
            {
                object result = invokingMethod.Invoke(target, data);

                if (result != null)
                {
                    resultsParts.Add(target, result);
                    returnCount++;
                }
            }

            if (returnCount > 1)
            {
                return resultsParts;
            }

            if (returnCount == 1)
            {
                return resultsParts.Values.First();
            }

            return null;
        }

        private static string FormatInvocationMessage(int invocationCount, object lastTarget = null)
        {
            switch (invocationCount)
            {
                case 0:
                    throw new Exception("No targets could be found");
                case 1:
                {
                    string name;
                    if (lastTarget is Object obj)
                    {
                        name = obj.name;
                    }
                    else
                    {
                        name = lastTarget?.ToString();
                    }

                    return $"> Invoked on {name}";
                }
                default:
                    return $"> Invoked on {invocationCount} targets";
            }
        }

        private static object GetSingletonInstance(Type classType)
        {
            object target;
            if (QuantumRegistry.GetRegistrySize(classType) > 0)
            {
                target = QuantumRegistry.GetRegistryContents(classType).First();
            }
            else
            {
                target = CreateCommandSingletonInstance(classType);
                QuantumRegistry.RegisterObject(classType, target);
            }

            return target;
        }

        private static Component CreateCommandSingletonInstance(Type classType)
        {
            GameObject obj = new GameObject($"{classType}Singleton");
            Object.DontDestroyOnLoad(obj);
            return obj.AddComponent(classType);
        }
    }
}
