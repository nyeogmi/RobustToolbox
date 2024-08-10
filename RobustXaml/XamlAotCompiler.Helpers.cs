﻿using System.Collections.Generic;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Collections.Generic;
using XamlX.TypeSystem;

namespace RobustXaml
{
    /// <summary>
    /// Helpers taken from:
    /// - https://github.com/AvaloniaUI/Avalonia/blob/c85fa2b9977d251a31886c2534613b4730fbaeaf/src/Avalonia.Build.Tasks/XamlCompilerTaskExecutor.cs
    /// - https://github.com/AvaloniaUI/Avalonia/blob/c85fa2b9977d251a31886c2534613b4730fbaeaf/src/Avalonia.Build.Tasks/XamlCompilerTaskExecutor.Helpers.cs
    /// </summary>
    public partial class XamlAotCompiler
    {
        static bool CheckXamlName(IResource r) => r.Name.ToLowerInvariant().EndsWith(".xaml")
                                                  || r.Name.ToLowerInvariant().EndsWith(".paml")
                                                  || r.Name.ToLowerInvariant().EndsWith(".axaml");
        interface IResource : IFileSource
        {
            string Uri { get; }
            string Name { get; }
            void Remove();

        }

        interface IResourceGroup
        {
            string Name { get; }
            IEnumerable<IResource> Resources { get; }
        }

        class EmbeddedResources : IResourceGroup
        {
            private readonly AssemblyDefinition _asm;
            public string Name => "EmbeddedResource";

            public IEnumerable<IResource> Resources => _asm.MainModule.Resources.OfType<EmbeddedResource>()
                .Select(r => new WrappedResource(_asm, r)).ToList();

            public EmbeddedResources(AssemblyDefinition asm)
            {
                _asm = asm;
            }
            class WrappedResource : IResource
            {
                private readonly AssemblyDefinition _asm;
                private readonly EmbeddedResource _res;

                public WrappedResource(AssemblyDefinition asm, EmbeddedResource res)
                {
                    _asm = asm;
                    _res = res;
                }

                public string Uri => $"resm:{Name}?assembly={_asm.Name.Name}";
                public string Name => _res.Name;
                public string FilePath => Name;
                public byte[] FileContents => _res.GetResourceData();

                public void Remove() => _asm.MainModule.Resources.Remove(_res);
            }
        }
    }
}
