// Copyright (c) 2026 SquareZero Inc. â€” Licensed under Apache 2.0. See LICENSE in the repo root.
using System;
using System.Collections.Generic;

namespace Bibim.Core
{
    /// <summary>
    /// Lightweight DI container — no external dependencies.
    /// Avoids Microsoft.Extensions.DependencyInjection assembly loading issues in Revit host.
    /// </summary>
    public static class ServiceContainer
    {
        private static readonly object _lock = new object();
        private static readonly Dictionary<Type, object> _services = new Dictionary<Type, object>();
        private static bool _initialized;

        public static void Initialize()
        {
            lock (_lock)
            {
                if (_initialized) return;
                _initialized = true;
            }
        }

        public static void Register<T>(T instance) where T : class
        {
            lock (_lock)
            {
                _services[typeof(T)] = instance;
            }
        }

        public static T GetService<T>() where T : class
        {
            lock (_lock)
            {
                if (_services.TryGetValue(typeof(T), out var svc))
                    return (T)svc;
                return null;
            }
        }

        public static T GetRequiredService<T>() where T : class
        {
            var svc = GetService<T>();
            if (svc == null)
                throw new InvalidOperationException($"Service {typeof(T).Name} not registered.");
            return svc;
        }

        public static void Reset()
        {
            lock (_lock)
            {
                foreach (var svc in _services.Values)
                {
                    if (svc is IDisposable d) d.Dispose();
                }
                _services.Clear();
                _initialized = false;
            }
        }

        public static bool IsInitialized => _initialized;
    }
}
