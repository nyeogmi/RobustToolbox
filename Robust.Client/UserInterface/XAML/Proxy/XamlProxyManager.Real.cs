﻿#if TOOLS
using System;
using System.Collections.Generic;
using System.Reflection;
using Robust.Shared.IoC;
using Robust.Shared.Log;
using Robust.Shared.Reflection;
using RobustXaml;

namespace Robust.Client.UserInterface.XAML.Proxy {
    /// <summary>
    /// The real implementation of IXamlProxyManager.
    ///
    /// (See that interface for details.)
    /// </summary>
    public sealed class XamlProxyManager: IXamlProxyManager
    {
        ISawmill _sawmill = null!;
        [Dependency] IReflectionManager _reflectionManager = null!;
        [Dependency] ILogManager _logManager = null!;

        XamlImplementationStorage _xamlImplementationStorage = null!;

        List<Assembly> _knownAssemblies = [];
        XamlJitCompiler? _xamlJitCompiler;

        /// <summary>
        /// Initialize this, subscribing to assembly changes.
        /// </summary>
        public void Initialize()
        {
            _sawmill = _logManager.GetSawmill("xamlhotreload");
            _xamlImplementationStorage = new XamlImplementationStorage(_sawmill, Compile);

            AddAssemblies();
            _reflectionManager.OnAssemblyAdded += (_, _) => { AddAssemblies(); };
        }

        /// <summary>
        /// Return true if setting the implementation of fileName would not be a no-op.
        /// </summary>
        /// <param name="fileName">the file name</param>
        /// <returns>true or false</returns>
        public bool CanSetImplementation(string fileName)
        {
            return _xamlImplementationStorage.CanSetImplementation(fileName);
        }

        /// <summary>
        /// Replace the implementation of fileName, failing silently if the new content
        /// does not compile. (but still logging)
        /// </summary>
        /// <param name="fileName">the file name</param>
        /// <param name="fileContent">the new content</param>
        public void SetImplementation(string fileName, string fileContent)
        {
            _xamlImplementationStorage.SetImplementation(fileName, fileContent, false);
        }

        /// <summary>
        /// Add all the types from all known assemblies, then force-JIT everything again.
        /// </summary>
        private void AddAssemblies()
        {
            foreach (var a in _reflectionManager.Assemblies)
            {
                if (!_knownAssemblies.Contains(a))
                {
                    _knownAssemblies.Add(a);
                    _xamlImplementationStorage.Add(a);

                    _xamlJitCompiler = null;
                }
            }

            // Always use the JITed versions on debug builds
            _xamlImplementationStorage.ForceReloadAll();
        }

        /// <summary>
        /// Populate `o` using the JIT compiler, if possible.
        /// </summary>
        /// <param name="t">the static type of `o`</param>
        /// <param name="o">a `t` instance or subclass</param>
        /// <returns>true if there was a JITed implementation</returns>
        public bool Populate(Type t, object o)
        {
            return _xamlImplementationStorage.Populate(t, o);
        }

        /// <summary>
        /// Wrap XamlJitCompiler.Compile.
        ///
        /// Lazily initialize XamlJitCompiler -- initializing it is expensive, and initializing it on every
        /// individual assembly load is quadratic.
        /// </summary>
        /// <param name="t">the type that cares about this Xaml</param>
        /// <param name="uri">the Uri of this xaml (from the type's metadata)</param>
        /// <param name="fileName">the filename of this xaml (from the type's metadata)</param>
        /// <param name="content">the new content of the xaml file</param>
        /// <returns></returns>
        /// <exception cref="InvalidOperationException">if XamlJitCompiler returns something other than success or failure</exception>
        MethodInfo? Compile(Type t, Uri uri, string fileName, string content)
        {
            XamlJitCompiler xjit;
            lock(this)
            {
                xjit = _xamlJitCompiler ??= new XamlJitCompiler();
            }

            var result = xjit.Compile(t, uri, fileName, content);

            if (result is XamlJitCompilerResult.Error e)
            {
                _sawmill.Info($"hot reloading failed: {t.FullName}; {fileName}; {e.Raw.Message} {e.Hint ?? ""}");
                return null;
            }

            if (result is XamlJitCompilerResult.Success s)
            {
                return s.MethodInfo;
            }

            throw new InvalidOperationException($"totally unexpected result from compiler operation: {result}");
        }
    }
}
#endif
