using System;
using System.Collections.Generic;
using System.Reflection;
using Robust.Shared.IoC;
using Robust.Shared.Log;
using Robust.Shared.Reflection;
using XamlX.TypeSystem;

namespace Robust.Client.UserInterface.XAML.JIT
{
    public interface IXamlJitManager
    {
        void Initialize();
        void PopulateJit(Type t, object o);
    }

    public sealed class XamlJitManager: IXamlJitManager
    {
        [Dependency] IReflectionManager _reflectionManager = default!;

        XamlJitCompiler? _xamlJitCompiler = null;
        List<Assembly> _heldAssemblies = []; // TODO: Weaker way to reference these
        Dictionary<Type, MethodInfo> _implementations = new();

        public void Initialize()
        {
            AddImplementationsFromNewAssemblies();
            _reflectionManager.OnAssemblyAdded += (_, _) =>
            {
                AddImplementationsFromNewAssemblies();
            };
        }

        internal void AddImplementationsFromNewAssemblies()
        {
            foreach (var a in _reflectionManager.Assemblies)
            {
                if (!_heldAssemblies.Contains(a))
                {
                    _heldAssemblies.Add(a);
                    AddImplementationsFromNewAssembly(a);

                    // reinitialize
                    _xamlJitCompiler = null;
                }
            }
        }

        internal XamlJitCompiler Compiler
        {
            get
            {
                if (_xamlJitCompiler == null)
                {
                    // initialize as late as possible
                    return _xamlJitCompiler = new XamlJitCompiler();
                }

                return _xamlJitCompiler;
            }
        }

        internal void AddImplementationsFromNewAssembly(Assembly a)
        {
            foreach (var (type, xaml) in Compiler.DiscoverXamlTypes(a))
            {
                SetImplementation(type, xaml);
            }
        }

        public void SetImplementation(Type t, IFileSource xaml)
        {
            _implementations[t] = Compiler.Compile(t, xaml);
        }

        public void PopulateJit(Type t, object o)
        {
            Logger.Info($"PopulateJit called: {t}, {o}");

            if (!_implementations.TryGetValue(t, out var implementation))
            {
                throw new InvalidOperationException("TODO: This should never happen");
            }

            implementation.Invoke(null, [(IServiceProvider?) null, o]);
        }
    }

}
