using QFSW.QC.Comparators;
using QFSW.QC.Utilities;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace QFSW.QC
{
    /// <summary>
    /// Contains the full data about a command and provides an execution point for invoking the command.
    /// </summary>
    public class CommandData
    {
        public readonly string CommandName;
        public readonly string CommandDescription;
        public readonly string CommandSignature;
        public readonly string ParameterSignature;
        public readonly string GenericSignature;
        public readonly ParameterInfo[] MethodParamData;
        public readonly Type[] ParamTypes;
        public readonly Type[] GenericParamTypes;
        public readonly MethodInfo MethodData;
        public readonly MonoTargetType MonoTarget;

        private readonly object[] _defaultParameters;
        private readonly bool _isMono;

        public bool IsGeneric => GenericParamTypes.Length > 0;
        public bool IsStatic => MethodData.IsStatic;
        public bool HasDescription => !string.IsNullOrWhiteSpace(CommandDescription);
        public int ParamCount => ParamTypes.Length - _defaultParameters.Length;

        public Type[] MakeGenericArguments(params Type[] genericTypeArguments)
        {
            if (genericTypeArguments.Length != GenericParamTypes.Length)
            {
                throw new ArgumentException("Incorrect number of generic substitution types were supplied.");
            }

            Dictionary<string, Type> substitutionTable = new Dictionary<string, Type>();
            for (int i = 0; i < genericTypeArguments.Length; i++)
            {
                substitutionTable.Add(GenericParamTypes[i].Name, genericTypeArguments[i]);
            }

            Type[] types = new Type[ParamTypes.Length];
            for (int i = 0; i < types.Length; i++)
            {
                if (ParamTypes[i].ContainsGenericParameters)
                {
                    Type substitution = ConstructGenericType(ParamTypes[i], substitutionTable);
                    types[i] = substitution;
                }
                else
                {
                    types[i] = ParamTypes[i];
                }
            }

            return types;
        }

        private Type ConstructGenericType(Type genericType, Dictionary<string, Type> substitutionTable)
        {
            if (!genericType.ContainsGenericParameters) { return genericType; }
            if (substitutionTable.ContainsKey(genericType.Name)) { return substitutionTable[genericType.Name]; }
            if (genericType.IsArray) { return ConstructGenericType(genericType.GetElementType(), substitutionTable).MakeArrayType(); }
            if (genericType.IsGenericType)
            {
                Type baseType = genericType.GetGenericTypeDefinition();
                Type[] typeArguments = genericType.GetGenericArguments();
                for (int i = 0; i < typeArguments.Length; i++)
                {
                    typeArguments[i] = ConstructGenericType(typeArguments[i], substitutionTable);
                }

                return baseType.MakeGenericType(typeArguments);
            }

            throw new ArgumentException($"Could not construct the generic type {genericType}");
        }

        public object Invoke(object[] paramData, Type[] genericTypeArguments)
        {
            object[] data = new object[paramData.Length + _defaultParameters.Length];
            Array.Copy(paramData, 0, data, 0, paramData.Length);
            Array.Copy(_defaultParameters, 0, data, paramData.Length, _defaultParameters.Length);

            MethodInfo invokingMethod = MethodData;
            if (IsGeneric)
            {
                try { invokingMethod = MethodData.MakeGenericMethod(genericTypeArguments); }
                catch (ArgumentException) { throw new ArgumentException($"Supplied generic parameters did not satisfy the generic constraints imposed by '{CommandName}'"); }
            }

            if (IsStatic)
            {
                return invokingMethod.Invoke(null, data);
            }
            else
            {
                IEnumerable<object> targets = InvocationTargetFactory.FindTargets(invokingMethod.DeclaringType, MonoTarget);
                return InvocationTargetFactory.InvokeOnTargets(invokingMethod, targets, data);
            }
        }

        public CommandData(MethodInfo methodData, int defaultParameterCount = 0) : this(methodData, methodData.Name, defaultParameterCount) { }
        public CommandData(MethodInfo methodData, string commandName, int defaultParameterCount = 0)
        {
            CommandName = commandName;
            MethodData = methodData;

            if (string.IsNullOrWhiteSpace(commandName))
            {
                CommandName = methodData.Name;
            }

            Type declaringType = methodData.DeclaringType;
            _isMono = typeof(MonoBehaviour).IsAssignableFrom(declaringType);
            if (!_isMono)
            {
                MonoTarget = MonoTargetType.Registry;
            }

            while (declaringType != null)
            {
                IEnumerable<CommandPrefixAttribute> prefixAttributes = declaringType.GetCustomAttributes<CommandPrefixAttribute>();
                foreach (CommandPrefixAttribute prefixAttribute in prefixAttributes.Reverse())
                {
                    if (prefixAttribute.Valid)
                    {
                        string prefix = prefixAttribute.Prefix;
                        if (string.IsNullOrWhiteSpace(prefix)) { prefix = declaringType.Name; }
                        CommandName = $"{prefix}{CommandName}";
                    }
                }

                declaringType = declaringType.DeclaringType;
            }

            MethodParamData = methodData.GetParameters();
            ParamTypes = new Type[MethodParamData.Length];
            for (int i = 0; i < ParamTypes.Length; i++) { ParamTypes[i] = MethodParamData[i].ParameterType; }

            _defaultParameters = new object[defaultParameterCount];
            for (int i = 0; i < defaultParameterCount; i++)
            {
                int j = MethodParamData.Length - defaultParameterCount + i;
                _defaultParameters[i] = MethodParamData[j].DefaultValue;
            }

            if (methodData.IsGenericMethodDefinition)
            {
                GenericParamTypes = methodData.GetGenericArguments();
                GenericSignature = $"<{string.Join(", ", GenericParamTypes.Select(x => x.Name))}>";
            }
            else { GenericParamTypes = Array.Empty<Type>(); }

            ParameterSignature = string.Empty;
            for (int i = 0; i < MethodParamData.Length - defaultParameterCount; i++) { ParameterSignature += $"{(i == 0 ? string.Empty : " ")}{MethodParamData[i].Name}"; }
            if (ParamCount > 0) { CommandSignature += $"{commandName}{GenericSignature} {ParameterSignature}"; }
            else { CommandSignature = $"{commandName}{GenericSignature}"; }
        }

        public CommandData(MethodInfo methodData, CommandAttribute commandAttribute, int defaultParameterCount = 0) : this(methodData, commandAttribute.Alias, defaultParameterCount)
        {
            CommandDescription = commandAttribute.Description;
            if (_isMono) { MonoTarget = commandAttribute.MonoTarget; }
        }

        public CommandData(MethodInfo methodData, CommandAttribute commandAttribute, CommandDescriptionAttribute descriptionAttribute, int defaultParameterCount = 0)
            : this(methodData, commandAttribute, defaultParameterCount)
        {
            if ((descriptionAttribute?.Valid ?? false) && string.IsNullOrWhiteSpace(commandAttribute.Description))
            {
                CommandDescription = descriptionAttribute.Description;
            }
        }
    }
}
