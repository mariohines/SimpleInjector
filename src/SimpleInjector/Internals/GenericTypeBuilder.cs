﻿// Copyright (c) Simple Injector Contributors. All rights reserved.
// Licensed under the MIT License. See LICENSE file in the project root for license information.

namespace SimpleInjector.Internals
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Linq;

    /// <summary>
    /// Helper class for building closed generic type for a given open generic type and a closed generic base.
    /// </summary>
    internal sealed class GenericTypeBuilder
    {
        [DebuggerDisplay("{" + TypesExtensions.FriendlyName + "(" + nameof(closedServiceType) + "),nq}")]
        private readonly Type closedServiceType;

        [DebuggerDisplay("{" + TypesExtensions.FriendlyName + "(" + nameof(implementation) + "),nq}")]
        private readonly Type implementation;

        private readonly Type openGenericImplementation;

        [DebuggerDisplay("{(" + nameof(partialOpenGenericImplementation) + " == null ? \"null\" : " +
            TypesExtensions.FriendlyName + "(" + nameof(partialOpenGenericImplementation) + ")),nq}")]
        private readonly Type? partialOpenGenericImplementation;

        private readonly bool isPartialOpenGenericImplementation;

        internal GenericTypeBuilder(Type closedServiceType, Type implementation)
        {
            this.closedServiceType = closedServiceType;
            this.implementation = implementation;

            this.isPartialOpenGenericImplementation =
                implementation.IsGenericType() && !implementation.IsGenericTypeDefinition();

            if (this.isPartialOpenGenericImplementation)
            {
                this.openGenericImplementation = implementation.GetGenericTypeDefinition();
                this.partialOpenGenericImplementation = implementation;
            }
            else
            {
                this.openGenericImplementation = implementation;
                this.partialOpenGenericImplementation = null;
            }
        }

        internal static bool IsImplementationApplicableToEveryGenericType(
            Type openAbstraction, Type openImplementation)
        {
            try
            {
                return MakeClosedImplementation(openAbstraction, openImplementation) != null;
            }
            catch (InvalidOperationException)
            {
                // This can happen in case there are some weird as type constraints, as with the curiously
                // recurring template pattern. In that case we know that the implementation can't be applied
                // to every generic type
                return false;
            }
        }

        internal static Type? MakeClosedImplementation(Type closedAbstraction, Type openImplementation)
        {
            var builder = new GenericTypeBuilder(closedAbstraction, openImplementation);
            var results = builder.BuildClosedGenericImplementation();
            return results.ClosedGenericImplementation;
        }

        internal bool OpenGenericImplementationCanBeAppliedToServiceType()
        {
            var openGenericBaseType = this.closedServiceType.GetGenericTypeDefinition();

            var openGenericBaseTypes = this.GetOpenGenericBaseTypes(openGenericBaseType);

            return openGenericBaseTypes.Any(type =>
            {
                var typeArguments = GetNestedTypeArgumentsForType(type);

                var partialOpenImplementation =
                    this.partialOpenGenericImplementation ?? this.openGenericImplementation;

                var unmappedArguments = partialOpenImplementation.GetGenericArguments().Except(typeArguments);

                return unmappedArguments.All(argument => !argument.IsGenericParameter);
            });
        }

        private Type[] GetOpenGenericBaseTypes(Type openGenericBaseType) => (
            from baseType in this.openGenericImplementation.GetTypeBaseTypesAndInterfaces()
            where openGenericBaseType.IsGenericTypeDefinitionOf(baseType)
            select baseType)
            .Distinct()
            .ToArray();

        internal BuildResult BuildClosedGenericImplementation()
        {
            bool isClosedImplementation = !this.implementation.ContainsGenericParameters();

            // In case the given implementation is already closed (or non-generic), we don't have to build a
            // type. If the implementation matches, we can directly return it. This is much faster and simpler.
            if (isClosedImplementation)
            {
                return this.closedServiceType.IsAssignableFrom(this.implementation)
                    ? BuildResult.Valid(this.implementation)
                    : BuildResult.Invalid;
            }
            else
            {
                var serviceType = this.FindMatchingOpenGenericServiceType();

                if (serviceType != null && this.SafisfiesPartialTypeArguments(serviceType))
                {
                    Type? closedGenericImplementation =
                        this.BuildClosedGenericImplementationBasedOnMatchingServiceType(serviceType);

                    // closedGenericImplementation will be null when there was a mismatch on type constraints.
                    if (closedGenericImplementation != null
                        && this.closedServiceType.IsAssignableFrom(closedGenericImplementation))
                    {
                        return BuildResult.Valid(closedGenericImplementation);
                    }
                }

                return BuildResult.Invalid;
            }
        }

        private CandicateServiceType FindMatchingOpenGenericServiceType()
        {
            // There can be more than one service that exactly matches, but they will never have a different
            // set of generic type arguments; the type system ensures this.
            return (
                from openCandidateServiceType in this.GetOpenCandidateServiceTypes()
                where this.MatchesClosedGenericBaseType(openCandidateServiceType)
                select openCandidateServiceType)
                .FirstOrDefault();
        }

        private Type? BuildClosedGenericImplementationBasedOnMatchingServiceType(
            CandicateServiceType candicateServiceType)
        {
            if (this.openGenericImplementation.IsGenericType())
            {
                try
                {
                    return this.openGenericImplementation.MakeGenericType(candicateServiceType.Arguments);
                }
                catch (ArgumentException)
                {
                    // This can happen when there is a type constraint that we didn't check. For instance
                    // the constraint where TIn : TOut is one we cannot check and have to do here (bit ugly).
                    return null;
                }
            }
            else
            {
                return this.openGenericImplementation;
            }
        }

        private IEnumerable<CandicateServiceType> GetOpenCandidateServiceTypes()
        {
            var openGenericBaseType = this.closedServiceType.GetGenericTypeDefinition();

            var openGenericBaseTypes = (
                from baseType in this.openGenericImplementation.GetTypeBaseTypesAndInterfaces()
                where openGenericBaseType.IsGenericTypeDefinitionOf(baseType)
                select baseType)
                .Distinct()
                .ToArray();

            return (
                from type in openGenericBaseTypes
                select this.ToCandicateServiceType(type))
                .ToArray();
        }

        private CandicateServiceType ToCandicateServiceType(Type openCandidateServiceType)
        {
            if (openCandidateServiceType.IsGenericType())
            {
                return new CandicateServiceType(openCandidateServiceType,
                    this.GetMatchingGenericArgumentsForOpenImplementationBasedOn(openCandidateServiceType));
            }

            return new CandicateServiceType(openCandidateServiceType, Helpers.Array<Type>.Empty);
        }

        private bool MatchesClosedGenericBaseType(CandicateServiceType openCandidateServiceType)
        {
            if (this.openGenericImplementation.IsGenericType())
            {
                return this.SatisfiesGenericTypeConstraints(openCandidateServiceType);
            }

            // When there are no generic type arguments, there are (obviously) no generic type constraints
            // so checking for the number of argument would always succeed, while this is not correct.
            // Instead we should check whether the given service type is the requested closed generic base
            // type.
            return this.closedServiceType == openCandidateServiceType.ServiceType;
        }

        private bool SatisfiesGenericTypeConstraints(CandicateServiceType openCandidateServiceType)
        {
            // Type arguments that don't match are left out of the list.
            // When the length of the result does not match the actual length, this means that the generic
            // type constraints don't match and the given service type does not satisfy the generic type
            // constraints.
            return openCandidateServiceType.Arguments.Length ==
                this.openGenericImplementation.GetGenericArguments().Length;
        }

        private bool SafisfiesPartialTypeArguments(CandicateServiceType candicateServiceType)
        {
            if (!this.isPartialOpenGenericImplementation)
            {
                return true;
            }

            return this.SafisfiesPartialTypeArguments(candicateServiceType.Arguments);
        }

        private bool SafisfiesPartialTypeArguments(Type[] arguments)
        {
            // Map the partial open generic type arguments to the concrete arguments.
            var mappings =
                this.partialOpenGenericImplementation!.GetGenericArguments()
                .Zip(arguments, ArgumentMapping.Create);

            return mappings.All(mapping => mapping.ConcreteTypeMatchesPartialArgument());
        }

        private Type[] GetMatchingGenericArgumentsForOpenImplementationBasedOn(Type openCandidateServiceType)
        {
            var finder = new GenericArgumentFinder(
                openCandidateServiceType,
                this.closedServiceType,
                this.openGenericImplementation,
                this.partialOpenGenericImplementation);

            return finder.GetConcreteTypeArgumentsForClosedImplementation();
        }

        private static IEnumerable<Type> GetNestedTypeArgumentsForType(Type type) => (
            from argument in type.GetGenericArguments()
            from nestedArgument in GetNestedTypeArgumentsForTypeArgument(argument, new List<Type>())
            select nestedArgument)
            .Distinct()
            .ToArray();

        private static IEnumerable<Type> GetNestedTypeArgumentsForTypeArgument(
            Type argument, IList<Type> processedArguments)
        {
            processedArguments.Add(argument);

            if (argument.IsGenericParameter)
            {
                var nestedArguments =
                    from constraint in argument.GetGenericParameterConstraints()
                    from arg in GetNestedTypeArgumentsForTypeArgument(constraint, processedArguments)
                    select arg;

                return nestedArguments.Concat(new[] { argument });
            }

            if (!argument.IsGenericType())
            {
                return Enumerable.Empty<Type>();
            }

            return
                from genericArgument in argument.GetGenericArguments().Except(processedArguments)
                from arg in GetNestedTypeArgumentsForTypeArgument(genericArgument, processedArguments)
                select arg;
        }

        /// <summary>Result of the GenericTypeBuilder.</summary>
        internal sealed class BuildResult
        {
            internal static readonly BuildResult Invalid = new BuildResult(null);

            private BuildResult(Type? closedGenericImplementation)
            {
                this.ClosedGenericImplementation = closedGenericImplementation;
            }

            internal bool ClosedServiceTypeSatisfiesAllTypeConstraints =>
                this.ClosedGenericImplementation != null;

            internal Type? ClosedGenericImplementation { get; }

            internal static BuildResult Valid(Type closedGenericImplementation)
            {
                return new BuildResult(closedGenericImplementation);
            }
        }

        /// <summary>
        /// A open generic type with the concrete arguments that can be used to create a closed generic type.
        /// </summary>
        private sealed class CandicateServiceType
        {
            internal readonly Type ServiceType;
            internal readonly Type[] Arguments;

            public CandicateServiceType(Type serviceType, Type[] arguments)
            {
                this.ServiceType = serviceType;
                this.Arguments = arguments;
            }

#if DEBUG
            // This is for our own debugging purposes. We don't use the DebuggerDisplayAttribute, because
            // this code is hard to write (and maintain) as debugger display string.
            public override string ToString() =>
                string.Format(
                    System.Globalization.CultureInfo.InvariantCulture,
                    "ServiceType: {0}, Arguments: {1}",
                    this.ServiceType.ToFriendlyName(),
                    this.Arguments.Select(type => type.ToFriendlyName()).ToCommaSeparatedText());
#endif
        }
    }
}