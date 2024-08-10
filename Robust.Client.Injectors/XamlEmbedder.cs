using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Build.Framework;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.Rocks;
using Pidgin;
using XamlX;
using XamlX.Ast;
using XamlX.Emit;
using XamlX.IL;
using XamlX.Parsers;
using XamlX.Transform;
using XamlX.TypeSystem;

namespace Robust.Build.Tasks
{
    /// <summary>
    /// Based on https://github.com/AvaloniaUI/Avalonia/blob/c85fa2b9977d251a31886c2534613b4730fbaeaf/src/Avalonia.Build.Tasks/XamlCompilerTaskExecutor.cs
    /// Adjusted for our UI-Framework
    /// </summary>
    public partial class XamlEmbedder
    {
        public static (bool success, bool writtentofile) Compile(IBuildEngine engine, string input, string[] references,
            string projectDirectory, string output, string strongNameKey)
        {
            var typeSystem = new CecilTypeSystem(references
                .Where(r => !r.ToLowerInvariant().EndsWith("robust.build.tasks.dll"))
                .Concat(new[] { input }), input);

            var asm = typeSystem.TargetAssemblyDefinition;

            if (asm.MainModule.GetType("CompiledRobustXaml", "XamlIlContext") != null)
            {
                // If this type exists, the assembly has already been processed by us.
                // Do not run again, it would corrupt the file.
                // This *shouldn't* be possible due to Inputs/Outputs dependencies in the build system,
                // but better safe than sorry eh?
                engine.LogWarningEvent(new BuildWarningEventArgs("XAMLIL", "", "", 0, 0, 0, 0, "Ran twice on same assembly file; ignoring.", "", ""));
                return (true, false);
            }

            var compileRes = CompileCore(engine, typeSystem);
            if (compileRes == null)
                return (true, false);
            if (compileRes == false)
                return (false, false);

            var writerParameters = new WriterParameters { WriteSymbols = asm.MainModule.HasSymbols };
            if (!string.IsNullOrWhiteSpace(strongNameKey))
                writerParameters.StrongNameKeyBlob = File.ReadAllBytes(strongNameKey);

            asm.Write(output, writerParameters);

            return (true, true);

        }

        static bool? CompileCore(IBuildEngine engine, CecilTypeSystem typeSystem)
        {
            var asm = typeSystem.TargetAssemblyDefinition;
            var embrsc = new EmbeddedResources(asm);

            if (embrsc.Resources.Count(CheckXamlName) == 0)
                // Nothing to do
                return null;

            var contextDef = new TypeDefinition("CompiledRobustXaml", "XamlIlContext",
                TypeAttributes.Class, asm.MainModule.TypeSystem.Object);
            asm.MainModule.Types.Add(contextDef);

            // these are classes and methods our generated code depends on
            var iocManager = typeSystem.GetTypeReference(typeSystem.FindType("Robust.Shared.IoC.IoCManager")).Resolve();

            var xamlJitHookup = typeSystem.GetTypeReference(typeSystem.FindType("Robust.Client.UserInterface.XAML.JIT.XamlJitHookup")).Resolve();
            var resolveXamlJitManagerMethod =
                asm.MainModule.ImportReference(
                    iocManager.Methods.First(m => m.Name == "Resolve").MakeGenericMethod(xamlJitHookup)
                );
            var populateJitMethod = asm.MainModule.ImportReference(xamlJitHookup.Methods.First(m => m.Name == "PopulateJit"));

            var systemType = typeSystem.GetTypeReference(typeSystem.FindType("System.Type"));
            var getTypeFromHandleMethod = asm.MainModule.ImportReference(
                systemType.Resolve().Methods.First(m => m.Name == "GetTypeFromHandle")
            );

            var stringType = typeSystem.GetTypeReference(typeSystem.FindType(typeof(string).FullName));

            var xamlJitEmbeddedResourceAttribute = typeSystem.GetTypeReference(
                typeSystem.FindType("Robust.Client.UserInterface.XAML.JIT.XamlJitEmbeddedResourceAttribute")
            ).Resolve();
            var xamlJitEmbeddedResourceAttributeConstructor = asm.MainModule.ImportReference(
                xamlJitEmbeddedResourceAttribute
                    .GetConstructors()
                    .First(
                        c => c.Parameters.Count == 2 &&
                             c.Parameters[0].ParameterType.FullName == "System.String" &&
                             c.Parameters[1].ParameterType.FullName == "System.String"
                    )
            );

            bool CompileGroup(IResourceGroup group)
            {

                var typeDef = new TypeDefinition("CompiledRobustXaml", "!" + group.Name, TypeAttributes.Class,
                    asm.MainModule.TypeSystem.Object);

                //typeDef.CustomAttributes.Add(new CustomAttribute(ed));
                asm.MainModule.Types.Add(typeDef);
                var builder = typeSystem.CreateTypeBuilder(typeDef);

                foreach (var res in group.Resources.Where(CheckXamlName))
                {
                    try
                    {
                        engine.LogMessage($"XAMLIL: {res.Name} -> {res.Uri}", MessageImportance.Low);

                        var xaml = new StreamReader(new MemoryStream(res.FileContents)).ReadToEnd();
                        var parsed = XDocumentXamlParser.Parse(xaml);

                        var initialRoot = (XamlAstObjectNode) parsed.Root;

                        var classDirective = initialRoot.Children.OfType<XamlAstXmlDirective>()
                            .FirstOrDefault(d => d.Namespace == XamlNamespaces.Xaml2006 && d.Name == "Class");
                        string classname;
                        if (classDirective != null && classDirective.Values[0] is XamlAstTextNode tn)
                        {
                            classname = tn.Text;
                        }
                        else
                        {
                            classname = res.Name.Replace(".xaml","");
                        }

                        var classType = typeSystem.TargetAssembly.FindType(classname);
                        if (classType == null)
                            throw new Exception($"Unable to find type '{classname}'");

                        var classTypeDefinition = typeSystem.GetTypeReference(classType).Resolve();

                        const string TrampolineName = "!XamlIlPopulateTrampoline";

                        var trampoline = new MethodDefinition(TrampolineName,
                            MethodAttributes.Static | MethodAttributes.Private, asm.MainModule.TypeSystem.Void);
                        trampoline.Parameters.Add(new ParameterDefinition(classTypeDefinition));
                        classTypeDefinition.Methods.Add(trampoline);

                        trampoline.Body.Instructions.Add(Instruction.Create(OpCodes.Call, resolveXamlJitManagerMethod));
                        trampoline.Body.Instructions.Add(Instruction.Create(OpCodes.Ldtoken, classTypeDefinition));
                        trampoline.Body.Instructions.Add(Instruction.Create(OpCodes.Call, getTypeFromHandleMethod));
                        trampoline.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_0));
                        trampoline.Body.Instructions.Add(Instruction.Create(OpCodes.Call, populateJitMethod));
                        trampoline.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));

                        var foundXamlLoader = false;
                        // Find RobustXamlLoader.Load(this) and replace it with !XamlIlPopulateTrampoline(this)
                        foreach (var method in classTypeDefinition.Methods
                            .Where(m => !m.Attributes.HasFlag(MethodAttributes.Static)))
                        {
                            var i = method.Body.Instructions;
                            for (var c = 1; c < i.Count; c++)
                            {
                                if (i[c].OpCode == OpCodes.Call)
                                {
                                    var op = i[c].Operand as MethodReference;

                                    if (op != null
                                        && op.Name == TrampolineName)
                                    {
                                        foundXamlLoader = true;
                                        break;
                                    }

                                    if (op != null
                                        && op.Name == "Load"
                                        && op.Parameters.Count == 1
                                        && op.Parameters[0].ParameterType.FullName == "System.Object"
                                        && op.DeclaringType.FullName == "Robust.Client.UserInterface.XAML.RobustXamlLoader")
                                    {
                                        if (MatchThisCall(i, c - 1))
                                        {
                                            i[c].Operand = trampoline;
                                            foundXamlLoader = true;
                                        }
                                    }
                                }
                            }
                        }

                        var xamlJitAttribute = new CustomAttribute(xamlJitEmbeddedResourceAttributeConstructor);
                        xamlJitAttribute.ConstructorArguments.Add(new CustomAttributeArgument(stringType, res.FilePath));
                        xamlJitAttribute.ConstructorArguments.Add(new CustomAttributeArgument(stringType, res.Uri));
                        classTypeDefinition.CustomAttributes.Add(xamlJitAttribute);

                        if (!foundXamlLoader)
                        {
                            var ctors = classTypeDefinition.GetConstructors()
                                .Where(c => !c.IsStatic).ToList();
                            // We can inject xaml loader into default constructor
                            if (ctors.Count == 1 && ctors[0].Body.Instructions.Count(o=>o.OpCode != OpCodes.Nop) == 3)
                            {
                                var i = ctors[0].Body.Instructions;
                                var retIdx = i.IndexOf(i.Last(x => x.OpCode == OpCodes.Ret));
                                i.Insert(retIdx, Instruction.Create(OpCodes.Call, trampoline));
                                i.Insert(retIdx, Instruction.Create(OpCodes.Ldarg_0));
                            }
                            else
                            {
                                throw new InvalidProgramException(
                                    $"No call to RobustXamlLoader.Load(this) call found anywhere in the type {classType.FullName} and type seems to have custom constructors.");
                            }
                        }
                    }
                    catch (Exception e)
                    {
                        engine.LogErrorEvent(new BuildErrorEventArgs("XAMLIL", "", res.FilePath, 0, 0, 0, 0,
                            $"{res.FilePath}: {e.Message}", "", "CompileRobustXaml"));
                    }
                }
                return true;
            }

            if (embrsc.Resources.Count(CheckXamlName) != 0)
            {
                if (!CompileGroup(embrsc))
                    return false;
            }

            return true;
        }

    }

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
}
