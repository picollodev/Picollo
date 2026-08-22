using System;
using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using Silhouette;

namespace Picollo.Profiler.IpResolution;

internal sealed class SimpleTypeProvider : ISignatureTypeProvider<string, SimpleTypeProvider.GenericContext>
{
    private readonly MetadataReader _reader;

    public SimpleTypeProvider(MetadataReader reader)
    {
        _reader = reader;
    }

    public sealed record GenericContext(string?[] TypeParameters, string?[] MethodParameters)
    {
        public static readonly GenericContext Empty = new([], []);
    }

    public string GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode switch
    {
        PrimitiveTypeCode.Boolean => "bool",
        PrimitiveTypeCode.Char => "char",
        PrimitiveTypeCode.SByte => "sbyte",
        PrimitiveTypeCode.Byte => "byte",
        PrimitiveTypeCode.Int16 => "short",
        PrimitiveTypeCode.UInt16 => "ushort",
        PrimitiveTypeCode.Int32 => "int",
        PrimitiveTypeCode.UInt32 => "uint",
        PrimitiveTypeCode.Int64 => "long",
        PrimitiveTypeCode.UInt64 => "ulong",
        PrimitiveTypeCode.Single => "float",
        PrimitiveTypeCode.Double => "double",
        PrimitiveTypeCode.String => "string",
        PrimitiveTypeCode.Object => "object",
        PrimitiveTypeCode.IntPtr => "nint",
        PrimitiveTypeCode.UIntPtr => "nuint",
        PrimitiveTypeCode.TypedReference => "typedref",
        PrimitiveTypeCode.Void => "void",
        _ => typeCode.ToString()
    };

    public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind)
    {
        var def = reader.GetTypeDefinition(handle);
        var name = reader.GetString(def.Name);

        var declaring = def.GetDeclaringType();
        if (!declaring.IsNil)
            return GetTypeFromDefinition(reader, declaring, rawTypeKind) + "." + name;

        var ns = reader.GetString(def.Namespace);
        return string.IsNullOrEmpty(ns) ? name : ns + "." + name;
    }

    public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind)
    {
        var tr = reader.GetTypeReference(handle);
        var name = reader.GetString(tr.Name);

        if (tr.ResolutionScope.Kind == HandleKind.TypeReference)
        {
            var parent = (TypeReferenceHandle)tr.ResolutionScope;
            return GetTypeFromReference(reader, parent, rawTypeKind) + "." + name;
        }

        var ns = reader.GetString(tr.Namespace);
        return string.IsNullOrEmpty(ns) ? name : ns + "." + name;
    }

    public string GetSZArrayType(string elementType) => elementType + "[]";

    public string GetArrayType(string elementType, ArrayShape shape)
        => elementType + "[" + new string(',', Math.Max(0, shape.Rank - 1)) + "]";

    public string GetPointerType(string elementType)
        => elementType + "*";

    public string GetByReferenceType(string elementType)
        => elementType + "&";

    public string GetGenericTypeParameter(GenericContext genericContext, int index)
        => ((uint)index < (uint)genericContext.TypeParameters.Length ? genericContext.TypeParameters[index] : null) ?? "!" + index;

    public string GetGenericMethodParameter(GenericContext genericContext, int index)
        => ((uint)index < (uint)genericContext.MethodParameters.Length ? genericContext.MethodParameters[index] : null) ?? "!!" + index;

    public string GetGenericInstantiation(
        string genericType,
        ImmutableArray<string> typeArguments)
        => $"{StripGenericArity(genericType)}<{string.Join(", ", typeArguments)}>";

    public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired)
        => unmodifiedType;

    public string GetPinnedType(string elementType)
        => elementType;

    public string GetFunctionPointerType(MethodSignature<string> signature)
    {
        var parts = signature.ParameterTypes.Add(signature.ReturnType);
        return $"delegate*<{string.Join(", ", parts)}>";
    }

    public string GetTypeFromSpecification(
        MetadataReader reader,
        GenericContext genericContext,
        TypeSpecificationHandle handle,
        byte rawTypeKind)
    {
        var spec = reader.GetTypeSpecification(handle);
        var blobReader = reader.GetBlobReader(spec.Signature);

        var decoder = new SignatureDecoder<string, GenericContext>(
            this,
            reader,
            genericContext);

        return decoder.DecodeType(ref blobReader);
    }

    public unsafe MethodSignature<string> DecodeMethodSig(NativePointer<byte> signature, GenericContext? genericContext = null)
    {
        var blobReader = new BlobReader((byte*)signature.Ptr, signature.Length);
        var decoder = new SignatureDecoder<string, GenericContext>(this, _reader, genericContext ?? GenericContext.Empty);
        return decoder.DecodeMethodSignature(ref blobReader);
    }

    public GenericContext CreateGenericContext(FunctionInfo functionInfo)
    {
        var handle = MetadataTokens.EntityHandle(functionInfo.Token.Value);

        if (handle.Kind != HandleKind.MethodDefinition)
            throw new NotSupportedException($"Expected MethodDef token, got {handle.Kind}");

        var method = _reader.GetMethodDefinition((MethodDefinitionHandle)handle);
        var type = _reader.GetTypeDefinition(method.GetDeclaringType());

        return new GenericContext(ReadGenericParameterNames(_reader, type.GetGenericParameters(), "!"),
            ReadGenericParameterNames(_reader, method.GetGenericParameters(), "!!"));
    }

    public string GetTypeDisplayName(string typeName, GenericContext genericContext)
        => AddGenericParameters(typeName, genericContext.TypeParameters);

    public string GetMethodDisplayName(string methodName, FunctionInfo functionInfo, MethodSignature<string> signature,
        GenericContext genericContext)
    {
        var methodHandle = (MethodDefinitionHandle)MetadataTokens.EntityHandle(functionInfo.Token.Value);
        var method = _reader.GetMethodDefinition(methodHandle);
        var parameterNames = new string?[signature.ParameterTypes.Length];
        var parameterAttributes = new ParameterAttributes[signature.ParameterTypes.Length];

        foreach (var parameterHandle in method.GetParameters())
        {
            var parameter = _reader.GetParameter(parameterHandle);
            var index = parameter.SequenceNumber - 1;
            if ((uint)index >= (uint)parameterNames.Length)
                continue;

            parameterNames[index] = _reader.GetString(parameter.Name);
            parameterAttributes[index] = parameter.Attributes;
        }

        var includeParameterNames = false;
        var parameters = new string[signature.ParameterTypes.Length];
        for (var i = 0; i < parameters.Length; i++)
        {
            var parameterType = signature.ParameterTypes[i];
            var modifier = "";
            if (parameterType.EndsWith("&", StringComparison.Ordinal))
            {
                parameterType = parameterType[..^1];
                modifier = (parameterAttributes[i] & ParameterAttributes.Out) != 0 ? "out " : "ref ";
            }

            var parameterName = includeParameterNames && !string.IsNullOrWhiteSpace(parameterNames[i])
                ? " " + parameterNames[i]
                : "";
            parameters[i] = modifier + parameterType + parameterName;
        }

        return $"{AddGenericParameters(methodName, genericContext.MethodParameters)}({string.Join(", ", parameters)})";
    }

    private static string?[] ReadGenericParameterNames(MetadataReader reader, GenericParameterHandleCollection handles,
        string fallbackPrefix)
    {
        var names = new string?[handles.Count];

        foreach (var handle in handles)
        {
            var gp = reader.GetGenericParameter(handle);
            var index = gp.Index;

            if ((uint)index >= (uint)names.Length)
                Array.Resize(ref names, index + 1);

            var name = reader.GetString(gp.Name);
            names[index] = string.IsNullOrEmpty(name)
                ? fallbackPrefix + index
                : name;
        }

        for (var i = 0; i < names.Length; i++)
            names[i] ??= fallbackPrefix + i;

        return names;
    }

    private static string StripGenericArity(string name)
    {
        var tick = name.IndexOf('`');
        return tick < 0 ? name : name[..tick];
    }

    private static string AddGenericParameters(string name, string?[] genericParameters)
    {
        if (genericParameters.Length == 0)
            return name;

        return $"{StripGenericArity(name)}<{string.Join(", ", genericParameters)}>";
    }

    public bool HasAttribute(FunctionInfo functionInfo, string fullAttributeName)
    {
        var handle = MetadataTokens.EntityHandle(functionInfo.Token.Value);

        if (handle.Kind != HandleKind.MethodDefinition)
            throw new NotSupportedException($"Expected MethodDef token, got {handle.Kind}");

        MethodDefinitionHandle methodHandle = (MethodDefinitionHandle)handle;
        // DumpMethod(_reader, methodHandle);
        MethodDefinition method = _reader.GetMethodDefinition(methodHandle);

        foreach (var attrHandle in method.GetCustomAttributes())
        {
            var attr = _reader.GetCustomAttribute(attrHandle);
            string? attributeTypeName = GetAttributeTypeName(attr.Constructor);
            if (attributeTypeName == fullAttributeName)
                return true;
        }

        return false;
    }

    private string? GetAttributeTypeName(EntityHandle ctor)
    {
        EntityHandle typeHandle;

        switch (ctor.Kind)
        {
            case HandleKind.MemberReference:
                var mr = _reader.GetMemberReference((MemberReferenceHandle)ctor);
                typeHandle = mr.Parent;
                break;

            case HandleKind.MethodDefinition:
                var md = _reader.GetMethodDefinition((MethodDefinitionHandle)ctor);
                typeHandle = md.GetDeclaringType();
                break;

            default:
                return null;
        }

        return typeHandle.Kind switch
        {
            HandleKind.TypeReference => TypeRefName((TypeReferenceHandle)typeHandle),
            HandleKind.TypeDefinition => TypeDefName((TypeDefinitionHandle)typeHandle),
            _ => null
        };

        string TypeRefName(TypeReferenceHandle h)
        {
            var t = _reader.GetTypeReference(h);
            var ns = _reader.GetString(t.Namespace);
            var name = _reader.GetString(t.Name);
            return string.IsNullOrEmpty(ns) ? name : ns + "." + name;
        }

        string TypeDefName(TypeDefinitionHandle h)
        {
            var t = _reader.GetTypeDefinition(h);
            var ns = _reader.GetString(t.Namespace);
            var name = _reader.GetString(t.Name);
            return string.IsNullOrEmpty(ns) ? name : ns + "." + name;
        }
    }

    // private void DumpMethod0(MetadataReader reader, MethodDefinitionHandle h)
    // {
    //     var m = reader.GetMethodDefinition(h);
    //     var name = reader.GetString(m.Name);
    //
    //     Console.WriteLine($"Token: 0x{MetadataTokens.GetToken(h):X8}");
    //     Console.WriteLine($"Name: {name}");
    //     Console.WriteLine($"Attributes: 0x{(int)m.Attributes:X8} {m.Attributes}");
    //     Console.WriteLine($"ImplAttributes: 0x{(int)m.ImplAttributes:X8} {m.ImplAttributes}");
    //     Console.WriteLine($"IsPinvokeImpl: {(m.Attributes & MethodAttributes.PinvokeImpl) != 0}");
    //
    //     if ((m.Attributes & MethodAttributes.PinvokeImpl) != 0)
    //     {
    //         var import = m.GetImport();
    //         Console.WriteLine($"Import name: {reader.GetString(import.Name)}");
    //
    //         var mod = reader.GetModuleReference(import.Module);
    //         Console.WriteLine($"Import module: {reader.GetString(mod.Name)}");
    //         Console.WriteLine($"Import attrs: {import.Attributes}");
    //     }
    //
    //     foreach (var attrHandle in m.GetCustomAttributes())
    //     {
    //         var attr = reader.GetCustomAttribute(attrHandle);
    //         Console.WriteLine($"Custom attr ctor kind: {attr.Constructor.Kind}");
    //     }
    // }
    //
    // static void DumpMethod(MetadataReader reader, MethodDefinitionHandle methodHandle)
    // {
    //     var method = reader.GetMethodDefinition(methodHandle);
    //     var name = reader.GetString(method.Name);
    //
    //     Console.WriteLine($"---- {name} ----");
    //     Console.WriteLine($"Token:           0x{MetadataTokens.GetToken(methodHandle):X8}");
    //     Console.WriteLine($"Attributes raw:  0x{(int)method.Attributes:X8}");
    //     Console.WriteLine($"Attributes:      {method.Attributes}");
    //     Console.WriteLine($"Impl raw:        0x{(int)method.ImplAttributes:X8}");
    //     Console.WriteLine($"ImplAttributes:  {method.ImplAttributes}");
    //     Console.WriteLine($"IsPinvokeImpl:   {(method.Attributes & MethodAttributes.PinvokeImpl) != 0}");
    //     Console.WriteLine($"IsInternalCall:  {(method.ImplAttributes & MethodImplAttributes.InternalCall) != 0}");
    //     Console.WriteLine($"RVA:             0x{method.RelativeVirtualAddress:X8}");
    //     Console.WriteLine($"Signature blob:  {Convert.ToHexString(reader.GetBlobBytes(method.Signature))}");
    //
    //     try
    //     {
    //         var import = method.GetImport();
    //         Console.WriteLine($"Import name:     {reader.GetString(import.Name)}");
    //         Console.WriteLine($"Import attrs:    {import.Attributes}");
    //
    //         if (!import.Module.IsNil)
    //         {
    //             var mod = reader.GetModuleReference(import.Module);
    //             Console.WriteLine($"Import module:   {reader.GetString(mod.Name)}");
    //         }
    //     }
    //     catch (Exception ex)
    //     {
    //         Console.WriteLine($"Import:          <none> ({ex.GetType().Name}: {ex.Message})");
    //     }
    //
    //     foreach (var ph in method.GetParameters())
    //     {
    //         var p = reader.GetParameter(ph);
    //         Console.WriteLine(
    //             $"Param #{p.SequenceNumber}: {reader.GetString(p.Name)} " +
    //             $"attrs=0x{(int)p.Attributes:X4} {p.Attributes}");
    //     }
    //
    //     foreach (var ah in method.GetCustomAttributes())
    //     {
    //         var attr = reader.GetCustomAttribute(ah);
    //         Console.WriteLine($"Attr token:      0x{MetadataTokens.GetToken(ah):X8}");
    //         Console.WriteLine($"Attr ctor kind:  {attr.Constructor.Kind}");
    //         Console.WriteLine($"Attr ctor token: 0x{MetadataTokens.GetToken(attr.Constructor):X8}");
    //         Console.WriteLine($"Attr blob:       {Convert.ToHexString(reader.GetBlobBytes(attr.Value))}");
    //     }
    //
    //     Console.WriteLine();
    // }
}
