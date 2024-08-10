using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using Pidgin;
using Robust.Shared.Log;
using Robust.Shared.Reflection;
using Robust.Shared.Utility;
using XamlX;
using XamlX.Ast;
using XamlX.Emit;
using XamlX.IL;
using XamlX.Parsers;
using XamlX.Transform;
using XamlX.TypeSystem;

namespace Robust.Client.UserInterface.XAML.JIT;

internal sealed class XamlJitCompiler
{
    // system reflection emit stuff
    private readonly ModuleBuilder _moduleBuilder;
    private int _typeI;

    // xaml stuff
    private readonly XamlLanguageTypeMappings _xamlLanguage;
    private readonly SreTypeSystem _typeSystem;

    public const string ContextNameScopeFieldName = "RobustNameScope";

    internal XamlJitCompiler()
    {
        var assemblyNameString = "JitsAreForKids";
        var assemblyName = new AssemblyName(assemblyNameString);
        var assemblyBuilder = AssemblyBuilder.DefineDynamicAssembly(
            assemblyName, AssemblyBuilderAccess.RunAndCollect);
        _moduleBuilder = assemblyBuilder.DefineDynamicModule(assemblyNameString);
        _typeI = 0;

        _typeSystem = new SreTypeSystem();
        Logger.Info($"type system assemblies");
        foreach (var a in _typeSystem.Assemblies)
        {
            Logger.Info($"type system assembly: {a} {a.Name}");
        }

        _xamlLanguage = new XamlLanguageTypeMappings(_typeSystem)
        {
            XmlnsAttributes =
            {
                _typeSystem.GetType("Avalonia.Metadata.XmlnsDefinitionAttribute"),

            },
            ContentAttributes =
            {
                _typeSystem.GetType("Avalonia.Metadata.ContentAttribute")
            },
            UsableDuringInitializationAttributes =
            {
                _typeSystem.GetType("Robust.Client.UserInterface.XAML.UsableDuringInitializationAttribute")
            },
            DeferredContentPropertyAttributes =
            {
                _typeSystem.GetType("Robust.Client.UserInterface.XAML.DeferredContentAttribute")
            },
            RootObjectProvider = _typeSystem.GetType("Robust.Client.UserInterface.XAML.ITestRootObjectProvider"),
            UriContextProvider = _typeSystem.GetType("Robust.Client.UserInterface.XAML.ITestUriContext"),
            ProvideValueTarget = _typeSystem.GetType("Robust.Client.UserInterface.XAML.ITestProvideValueTarget"),
        };
    }

    public IEnumerable<(Type, IFileSource)> DiscoverXamlTypes(Assembly assembly)
    {
        // TODO: Use an attribute or something instead
        foreach (var t in assembly.GetTypes())
        {
            var xamlJitEmbeddedResourceAttribute = t.GetCustomAttribute<XamlJitEmbeddedResourceAttribute>();
            if (xamlJitEmbeddedResourceAttribute == null)
            {
                continue;
            }

            yield return (t, MakeFileSource(t, xamlJitEmbeddedResourceAttribute.Uri, xamlJitEmbeddedResourceAttribute.ResourceName));
        }
    }

    private IFileSource MakeFileSource(Type type, string uri, string resourceName)
    {
        Logger.Info($"scanning {type} {uri} {resourceName}");
        foreach (var possibleAssembly in (Assembly[]) [type.Assembly, typeof(XamlJitCompiler).Assembly])
        {
            // TODO: Faster way to do this
            var stream = possibleAssembly.GetManifestResourceStream(resourceName);

            if (stream == null)
            {
                continue;
            }

            using var mstream = new MemoryStream();
            stream.CopyTo(mstream);

            return new XamlJitInternalFileSource(uri, mstream.ToArray());
        }

        throw new ArgumentException(
            $"tried to make file source with missing resources: {resourceName}");
    }

    private static void EmitNameScopeField(XamlLanguageTypeMappings xamlLanguage, SreTypeSystem typeSystem, IXamlTypeBuilder<IXamlILEmitter> typeBuilder, IXamlILEmitter constructor)
    {
        var nameScopeType = typeSystem.FindType("Robust.Client.UserInterface.XAML.NameScope");
        var field = typeBuilder.DefineField(nameScopeType,
            ContextNameScopeFieldName, true, false);
        constructor
            .Ldarg_0()
            .Newobj(nameScopeType.GetConstructor())
            .Stfld(field);
    }

    public MethodInfo Compile(Type type, IFileSource fileSource)
    {
        Logger.Info($"compiling: {type.FullName}");
        var transformerConfiguration = new TransformerConfiguration(
            _typeSystem,
            _typeSystem.GetAssembly(type.Assembly),
            _xamlLanguage,
            XamlXmlnsMappings.Resolve(_typeSystem, _xamlLanguage),
            CustomValueConverter
        );

        var emitConfig = new XamlLanguageEmitMappings<IXamlILEmitter, XamlILNodeEmitResult>
        {
            ContextTypeBuilderCallback = (b,c) =>
                EmitNameScopeField(_xamlLanguage, _typeSystem, b, c)
        };

        var ilCompiler = new RobustXamlILCompiler(transformerConfiguration, emitConfig, true);

        var xaml = new StreamReader(new MemoryStream(fileSource.FileContents)).ReadToEnd();
        var parsed = XDocumentXamlParser.Parse(xaml);

        var bigBox = _moduleBuilder.DefineType($"XamlBigBox{_typeI++}");
        var littleBox = _moduleBuilder.DefineType($"XamlLittleBox{_typeI++}");
        var xamlTypeBuilder = _typeSystem.CreateTypeBuilder(bigBox);
        var contextClass =
            XamlILContextDefinition.GenerateContextClass(xamlTypeBuilder, _typeSystem, _xamlLanguage, emitConfig);
        var populateBuilder = _typeSystem.CreateTypeBuilder(littleBox);

        ilCompiler.Transform(parsed);
        ilCompiler.Compile(
            parsed,
            contextClass,
            ilCompiler.DefinePopulateMethod(populateBuilder, parsed, "!Populate", true),
            null,
            null,
            (closureName, closureBaseType) => xamlTypeBuilder.DefineSubType(closureBaseType, closureName, false),
            fileSource.FilePath,
            fileSource
        );

        bigBox.CreateType();
        Type implementingType = littleBox.CreateType();
        var implementation = implementingType.GetMethod("!Populate");
        if (implementation == null)
        {
            throw new Exception("populate method should have existed");
        }

        return implementation;
    }

    private static bool CustomValueConverter(
        AstTransformationContext context,
        IXamlAstValueNode node,
        IXamlType type,
        out IXamlAstValueNode? result)
    {
        if (!(node is XamlAstTextNode textNode))
        {
            result = null;
            return false;
        }

        var text = textNode.Text;
        var types = context.GetRobustTypes();

        if (type.Equals(types.Vector2))
        {
            var foo = MathParsing.Single2.Parse(text);

            if (!foo.Success)
                throw new XamlLoadException($"Unable to parse \"{text}\" as a Vector2", node);

            var (x, y) = foo.Value;

            result = new RXamlSingleVecLikeConstAstNode(
                node,
                types.Vector2, types.Vector2ConstructorFull,
                types.Single, new[] {x, y});
            return true;
        }

        if (type.Equals(types.Thickness))
        {
            var foo = MathParsing.Thickness.Parse(text);

            if (!foo.Success)
                throw new XamlLoadException($"Unable to parse \"{text}\" as a Thickness", node);

            var val = foo.Value;
            float[] full;
            if (val.Length == 1)
            {
                var u = val[0];
                full = new[] {u, u, u, u};
            }
            else if (val.Length == 2)
            {
                var h = val[0];
                var v = val[1];
                full = new[] {h, v, h, v};
            }
            else // 4
            {
                full = val;
            }

            result = new RXamlSingleVecLikeConstAstNode(
                node,
                types.Thickness, types.ThicknessConstructorFull,
                types.Single, full);
            return true;
        }

        if (type.Equals(types.Thickness))
        {
            var foo = MathParsing.Thickness.Parse(text);

            if (!foo.Success)
                throw new XamlLoadException($"Unable to parse \"{text}\" as a Thickness", node);

            var val = foo.Value;
            float[] full;
            if (val.Length == 1)
            {
                var u = val[0];
                full = new[] {u, u, u, u};
            }
            else if (val.Length == 2)
            {
                var h = val[0];
                var v = val[1];
                full = new[] {h, v, h, v};
            }
            else // 4
            {
                full = val;
            }

            result = new RXamlSingleVecLikeConstAstNode(
                node,
                types.Thickness, types.ThicknessConstructorFull,
                types.Single, full);
            return true;
        }

        if (type.Equals(types.Color))
        {
            // TODO: Interpret these colors at XAML compile time instead of at runtime.
            result = new RXamlColorAstNode(node, types, text);
            return true;
        }

        result = null;
        return false;
    }

}

internal class XamlJitInternalFileSource(string filePath, byte[] fileContents) : IFileSource
{
    public string FilePath { get; } = filePath;
    public byte[] FileContents { get; } = fileContents;
}
