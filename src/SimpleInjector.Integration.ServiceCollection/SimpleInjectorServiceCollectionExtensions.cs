﻿#region Copyright Simple Injector Contributors
/* The Simple Injector is an easy-to-use Inversion of Control library for .NET
 * 
 * Copyright (c) 2019 Simple Injector Contributors
 * 
 * Permission is hereby granted, free of charge, to any person obtaining a copy of this software and 
 * associated documentation files (the "Software"), to deal in the Software without restriction, including 
 * without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell 
 * copies of the Software, and to permit persons to whom the Software is furnished to do so, subject to the 
 * following conditions:
 * 
 * The above copyright notice and this permission notice shall be included in all copies or substantial 
 * portions of the Software.
 * 
 * THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT 
 * LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO 
 * EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER 
 * IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE 
 * USE OR OTHER DEALINGS IN THE SOFTWARE.
*/
#endregion

namespace SimpleInjector
{
    using System;
    using System.Linq;
    using System.Reflection;
    using Microsoft.Extensions.DependencyInjection;
    using SimpleInjector.Diagnostics;
    using SimpleInjector.Integration.ServiceCollection;
    using SimpleInjector.Lifestyles;

    /// <summary>
    /// Extensions to configure Simple Injector on top of <see cref="IServiceCollection"/>.
    /// </summary>
    public static class SimpleInjectorServiceCollectionExtensions
    {
        private static readonly object SimpleInjectorAddOptionsKey = new object();

        /// <summary>
        /// Sets up the basic configuration that allows Simple Injector to be used in frameworks that require
        /// the use of <see cref="IServiceCollection"/> for registration of framework components.
        /// In case of the absense of a 
        /// <see cref="ContainerOptions.DefaultScopedLifestyle">DefaultScopedLifestyle</see>, this method 
        /// will configure <see cref="AsyncScopedLifestyle"/> as the default scoped lifestyle.
        /// In case a <paramref name="setupAction"/> is supplied, that delegate will be called that allow
        /// further configuring the container.
        /// </summary>
        /// <param name="services">The framework's <see cref="IServiceCollection"/> instance.</param>
        /// <param name="container">The application's <see cref="Container"/> instance.</param>
        /// <param name="setupAction">An optional setup action.</param>
        /// <returns>The supplied <paramref name="services"/>.</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="services"/> or
        /// <paramref name="container"/> are null references.</exception>
        public static IServiceCollection AddSimpleInjector(
            this IServiceCollection services,
            Container container,
            Action<SimpleInjectorAddOptions> setupAction = null)
        {
            if (services is null)
            {
                throw new ArgumentNullException(nameof(services));
            }

            if (container is null)
            {
                throw new ArgumentNullException(nameof(container));
            }

            var options = new SimpleInjectorAddOptions(
                services,
                container,
                new DefaultServiceProviderAccessor(container));

            // This stores the options, which includes the IServiceCollection. IServiceCollection is required
            // when calling UseSimpleInjector to enable auto cross wiring.
            AddOptions(container, options);

            // Set lifestyle before calling setupAction. Code in the delegate might depend on that.
            TrySetDefaultScopedLifestyle(container);

            setupAction?.Invoke(options);

            return services;
        }

        /// <summary>
        /// Finalizes the configuration of Simple Injector on top of <see cref="IServiceCollection"/>. Will
        /// ensure framework components can be injected into Simple Injector-resolved components, unless
        /// <see cref="SimpleInjectorUseOptions.AutoCrossWireFrameworkComponents"/> is set to <c>false</c>
        /// using the <paramref name="setupAction"/>.
        /// </summary>
        /// <param name="provider">The application's <see cref="IServiceProvider"/>.</param>
        /// <param name="container">The application's <see cref="Container"/> instance.</param>
        /// <param name="setupAction">An optional setup action.</param>
        /// <returns>The supplied <paramref name="provider"/>.</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="provider"/> or
        /// <paramref name="container"/> are null references.</exception>
        public static IServiceProvider UseSimpleInjector(
            this IServiceProvider provider,
            Container container,
            Action<SimpleInjectorUseOptions> setupAction = null)
        {
            if (provider is null)
            {
                throw new ArgumentNullException(nameof(provider));
            }

            if (container is null)
            {
                throw new ArgumentNullException(nameof(container));
            }

            SimpleInjectorAddOptions addOptions = GetOptions(container);

            RegisterServiceScope(provider, container);

            var useOptions = new SimpleInjectorUseOptions(addOptions, provider);

            setupAction?.Invoke(useOptions);

            if (useOptions.AutoCrossWireFrameworkComponents)
            {
                AddAutoCrossWiring(container, provider, addOptions);
            }

            return provider;
        }

        /// <summary>
        /// Cross wires an ASP.NET Core or third-party service to the container, to allow the service to be
        /// injected into components that are built by Simple Injector.
        /// </summary>
        /// <typeparam name="TService">The type of service object to cross-wire.</typeparam>
        /// <param name="options">The options.</param>
        /// <exception cref="ArgumentNullException">Thrown when the parameter is a null reference.
        /// </exception>
        public static void CrossWire<TService>(this SimpleInjectorUseOptions options)
            where TService : class
        {
            CrossWire(options, typeof(TService));
        }

        /// <summary>
        /// Cross wires an ASP.NET Core or third-party service to the container, to allow the service to be
        /// injected into components that are built by Simple Injector.
        /// </summary>
        /// <param name="options">The options.</param>
        /// <param name="serviceType">The type of service object to ross-wire.</param>
        /// <exception cref="ArgumentNullException">Thrown when one of the parameters is a null reference.
        /// </exception>
        public static void CrossWire(this SimpleInjectorUseOptions options, Type serviceType)
        {
            if (options is null)
            {
                throw new ArgumentNullException(nameof(options));
            }

            if (serviceType is null)
            {
                throw new ArgumentNullException(nameof(serviceType));
            }

            Registration registration = CreateCrossWireRegistration(
                options.Builder,
                options.ApplicationServices,
                serviceType,
                DetermineLifestyle(serviceType, options.Services));

            options.Container.AddRegistration(serviceType, registration);
        }

        private static void RegisterServiceScope(IServiceProvider provider, Container container)
        {
            if (container.Options.DefaultScopedLifestyle is null)
            {
                throw new InvalidOperationException(
                    "Please ensure that the container is configured with a default scoped lifestyle by " +
                    "setting the Container.Options.DefaultScopedLifestyle property with the required " +
                    "scoped lifestyle for your type of application. In ASP.NET Core, the typical " +
                    $"lifestyle to use is the {nameof(AsyncScopedLifestyle)}. " +
                    "See: https://simpleinjector.org/lifestyles#scoped");
            }

            container.Register<IServiceScope>(
                provider.GetRequiredService<IServiceScopeFactory>().CreateScope,
                Lifestyle.Scoped);
        }

        private static SimpleInjectorAddOptions GetOptions(Container container)
        {
            SimpleInjectorAddOptions options =
                (SimpleInjectorAddOptions)container.ContainerScope.GetItem(SimpleInjectorAddOptionsKey);

            if (options is null)
            {
                throw new InvalidOperationException(
                    "Please ensure the " +
                    $"{nameof(SimpleInjectorServiceCollectionExtensions.AddSimpleInjector)} extension " +
                    "method is called on the IServiceCollection instance before using this method.");
            }

            return options;
        }

        private static void AddAutoCrossWiring(
            Container container, IServiceProvider provider, SimpleInjectorAddOptions builder)
        {
            var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();
            var services = builder.Services;

            container.ResolveUnregisteredType += (s, e) =>
            {
                if (e.Handled)
                {
                    return;
                }

                Type serviceType = e.UnregisteredServiceType;

                ServiceDescriptor descriptor = FindServiceDescriptor(services, serviceType);

                if (descriptor != null)
                {
                    Registration registration =
                        CreateCrossWireRegistration(
                            builder,
                            provider,
                            serviceType,
                            ToLifestyle(descriptor.Lifetime));

                    e.Register(registration);
                }
            };
        }

        private static Lifestyle DetermineLifestyle(Type serviceType, IServiceCollection services)
        {
            var descriptor = FindServiceDescriptor(services, serviceType);

            // In case the service type is an IEnumerable, a registration can't be found, but collections are
            // in Core always registered as Transient, so it's safe to fall back to the transient lifestyle.
            return ToLifestyle(descriptor?.Lifetime ?? ServiceLifetime.Transient);
        }

        private static Registration CreateCrossWireRegistration(
            SimpleInjectorAddOptions builder,
            IServiceProvider provider,
            Type serviceType,
            Lifestyle lifestyle)
        {
            var registration = lifestyle.CreateRegistration(
                serviceType,
                lifestyle == Lifestyle.Singleton
                    ? BuildSingletonInstanceCreator(serviceType, provider)
                    : BuildScopedInstanceCreator(serviceType, builder.ServiceProviderAccessor),
                builder.Container);

            if (lifestyle == Lifestyle.Transient && typeof(IDisposable).IsAssignableFrom(serviceType))
            {
                registration.SuppressDiagnosticWarning(
                    DiagnosticType.DisposableTransientComponent,
                    justification: "This is a cross-wired service. It will  be disposed by IServiceScope.");
            }

            return registration;
        }

        private static Func<object> BuildSingletonInstanceCreator(
            Type serviceType, IServiceProvider rootProvider)
        {
            return () => rootProvider.GetRequiredService(serviceType);
        }

        private static Func<object> BuildScopedInstanceCreator(
            Type serviceType, IServiceProviderAccessor accessor)
        {
            // The ServiceProviderAccessor allows access to a request-specific IServiceProvider. This
            // allows Scoped and Transient instances to be resolved from a scope instead of the root
            // container—resolving them from the root container will cause memory leaks. Specific
            // framework integration (such as Simple Injector's ASP.NET Core integration) can override
            // this accessor with one that allows retrieving the IServiceProvider from a web request.
            return () => accessor.Current.GetRequiredService(serviceType);
        }

        private static ServiceDescriptor FindServiceDescriptor(IServiceCollection services, Type serviceType)
        {
            // In case there are multiple descriptors for a given type, .NET Core will use the last
            // descriptor when one instance is resolved. We will have to get this last one as well.
            ServiceDescriptor descriptor = services.LastOrDefault(d => d.ServiceType == serviceType);

            if (descriptor == null && serviceType.GetTypeInfo().IsGenericType)
            {
                // In case the registration is made as open-generic type, the previous query will return
                // null, and we need to go find the last open generic registration for the service type.
                var serviceTypeDefinition = serviceType.GetTypeInfo().GetGenericTypeDefinition();
                descriptor = services.LastOrDefault(d => d.ServiceType == serviceTypeDefinition);
            }

            return descriptor;
        }

        private static Lifestyle ToLifestyle(ServiceLifetime lifetime)
        {
            switch (lifetime)
            {
                case ServiceLifetime.Singleton: return Lifestyle.Singleton;
                case ServiceLifetime.Scoped: return Lifestyle.Scoped;
                default: return Lifestyle.Transient;
            }
        }

        private static void AddOptions(Container container, SimpleInjectorAddOptions builder)
        {
            var current = container.ContainerScope.GetItem(SimpleInjectorAddOptionsKey);

            if (current is null)
            {
                container.ContainerScope.SetItem(SimpleInjectorAddOptionsKey, builder);
            }
            else
            {
                throw new InvalidOperationException(
                    $"The {nameof(AddSimpleInjector)} extension method can only be called once.");
            }
        }

        private static void TrySetDefaultScopedLifestyle(Container container)
        {
            if (container.Options.DefaultScopedLifestyle is null)
            {
                container.Options.DefaultScopedLifestyle = new AsyncScopedLifestyle();
            }
        }
    }
}