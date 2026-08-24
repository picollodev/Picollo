using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Text;
using Picollo.Profiling.Messages;
using Silhouette;
using ParameterModifier = Picollo.Profiling.Messages.ParameterModifier;

namespace Picollo.Profiler.IpResolution;

internal sealed class SignatureTypeProvider : ISignatureTypeProvider<TypeMetadata, SignatureTypeProvider.GenericContext>
{
    private readonly MetadataReader _reader;

    public SignatureTypeProvider(MetadataReader reader)
    {
        _reader = reader;
    }

    internal sealed class GenericContext
    {
        public static readonly GenericContext Empty = new([], []);

        public string?[] TypeParameters { get; }
        public string?[] MethodParameters { get; }

        public GenericContext(string?[] typeParameters, string?[] methodParameters)
        {
            TypeParameters = typeParameters;
            MethodParameters = methodParameters;
        }
    }

    public TypeMetadata GetPrimitiveType(PrimitiveTypeCode typeCode)
        => new("", typeCode switch
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
        });

    public TypeMetadata GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind)
    {
        var definition = reader.GetTypeDefinition(handle);
        var name = reader.GetString(definition.Name);
        var declaringType = definition.GetDeclaringType();

        if (!declaringType.IsNil)
        {
            var declaringMetadata = GetTypeFromDefinition(reader, declaringType, rawTypeKind);
            return new TypeMetadata(declaringMetadata.Namespace, $"{declaringMetadata.Name}.{name}");
        }

        return new TypeMetadata(reader.GetString(definition.Namespace), name);
    }

    public TypeMetadata GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind)
    {
        var reference = reader.GetTypeReference(handle);
        var name = reader.GetString(reference.Name);

        if (reference.ResolutionScope.Kind == HandleKind.TypeReference)
        {
            var declaringMetadata = GetTypeFromReference(reader, (TypeReferenceHandle)reference.ResolutionScope, rawTypeKind);
            return new TypeMetadata(declaringMetadata.Namespace, $"{declaringMetadata.Name}.{name}");
        }

        return new TypeMetadata(reader.GetString(reference.Namespace), name);
    }

    public TypeMetadata GetSZArrayType(TypeMetadata elementType)
        => new(elementType.Namespace, elementType.Name + "[]");

    public TypeMetadata GetArrayType(TypeMetadata elementType, ArrayShape shape)
        => new(elementType.Namespace, elementType.Name + "[" + new string(',', Math.Max(0, shape.Rank - 1)) + "]");

    public TypeMetadata GetPointerType(TypeMetadata elementType)
        => new(elementType.Namespace, elementType.Name + "*");

    public TypeMetadata GetByReferenceType(TypeMetadata elementType)
        => new(elementType.Namespace, elementType.Name + "&");

    public TypeMetadata GetGenericTypeParameter(GenericContext genericContext, int index)
        => new("", ((uint)index < (uint)genericContext.TypeParameters.Length ? genericContext.TypeParameters[index] : null) ?? "!" + index);

    public TypeMetadata GetGenericMethodParameter(GenericContext genericContext, int index)
        => new("", ((uint)index < (uint)genericContext.MethodParameters.Length ? genericContext.MethodParameters[index] : null) ?? "!!" + index);

    public TypeMetadata GetGenericInstantiation(TypeMetadata genericType, ImmutableArray<TypeMetadata> typeArguments)
        => new(genericType.Namespace, AddGenericArguments(genericType.Name, typeArguments));

    public TypeMetadata GetModifiedType(TypeMetadata modifier, TypeMetadata unmodifiedType, bool isRequired)
        => unmodifiedType;

    public TypeMetadata GetPinnedType(TypeMetadata elementType)
        => elementType;

    public TypeMetadata GetFunctionPointerType(MethodSignature<TypeMetadata> signature)
    {
        var builder = new StringBuilder("delegate*<");

        for (var i = 0; i < signature.ParameterTypes.Length; i++)
        {
            if (i != 0)
                builder.Append(", ");

            builder.Append(signature.ParameterTypes[i].FullName);
        }

        if (signature.ParameterTypes.Length != 0)
            builder.Append(", ");

        builder.Append(signature.ReturnType.FullName);
        builder.Append('>');
        return new TypeMetadata("", builder.ToString());
    }

    public TypeMetadata GetTypeFromSpecification(
        MetadataReader reader,
        GenericContext genericContext,
        TypeSpecificationHandle handle,
        byte rawTypeKind)
        => reader.GetTypeSpecification(handle).DecodeSignature(this, genericContext);

    public unsafe MethodSignature<TypeMetadata> DecodeMethodSignature(
        NativePointer<byte> signature,
        GenericContext? genericContext = null)
    {
        var blobReader = new BlobReader((byte*)signature.Ptr, signature.Length);
        var decoder = new SignatureDecoder<TypeMetadata, GenericContext>(
            this,
            _reader,
            genericContext ?? GenericContext.Empty);

        return decoder.DecodeMethodSignature(ref blobReader);
    }

    public GenericContext CreateGenericContext(MethodDefinitionHandle methodHandle)
    {
        var method = _reader.GetMethodDefinition(methodHandle);
        var declaringType = _reader.GetTypeDefinition(method.GetDeclaringType());

        return new GenericContext(
            ReadGenericParameterNames(_reader, declaringType.GetGenericParameters(), "!"),
            ReadGenericParameterNames(_reader, method.GetGenericParameters(), "!!"));
    }

    public MethodMetadata GetMethodMetadata(MethodDefinitionHandle methodHandle, NativePointer<byte> signature)
    {
        var method = _reader.GetMethodDefinition(methodHandle);
        var genericContext = CreateGenericContext(methodHandle);
        var decodedSignature = DecodeMethodSignature(signature, genericContext);
        var parameterAttributes = new ParameterAttributes[decodedSignature.ParameterTypes.Length];

        foreach (var parameterHandle in method.GetParameters())
        {
            var parameter = _reader.GetParameter(parameterHandle);
            var index = parameter.SequenceNumber - 1;
            if ((uint)index < (uint)parameterAttributes.Length)
                parameterAttributes[index] = parameter.Attributes;
        }

        var parameters = ImmutableArray.CreateBuilder<ParameterMetadata>(decodedSignature.ParameterTypes.Length);
        for (var i = 0; i < decodedSignature.ParameterTypes.Length; i++)
        {
            var type = decodedSignature.ParameterTypes[i];
            var modifier = ParameterModifier.None;

            if (type.Name.EndsWith("&", StringComparison.Ordinal))
            {
                type = new TypeMetadata(type.Namespace, type.Name[..^1]);
                modifier = (parameterAttributes[i] & ParameterAttributes.Out) != 0
                    ? ParameterModifier.Out
                    : (parameterAttributes[i] & ParameterAttributes.In) != 0
                        ? ParameterModifier.In
                        : ParameterModifier.Ref;
            }

            parameters.Add(new ParameterMetadata(type, modifier));
        }

        return new MethodMetadata(
            GetDeclaringTypeMetadata(method.GetDeclaringType(), genericContext.TypeParameters),
            AddGenericParameters(_reader.GetString(method.Name), genericContext.MethodParameters),
            decodedSignature.ReturnType,
            parameters.MoveToImmutable());
    }

    private static string?[] ReadGenericParameterNames(
        MetadataReader reader,
        GenericParameterHandleCollection handles,
        string fallbackPrefix)
    {
        var names = new string?[handles.Count];

        foreach (var handle in handles)
        {
            var parameter = reader.GetGenericParameter(handle);
            var index = parameter.Index;

            if ((uint)index >= (uint)names.Length)
                Array.Resize(ref names, index + 1);

            var name = reader.GetString(parameter.Name);
            names[index] = string.IsNullOrEmpty(name) ? fallbackPrefix + index : name;
        }

        for (var i = 0; i < names.Length; i++)
            names[i] ??= fallbackPrefix + i;

        return names;
    }

    private TypeMetadata GetDeclaringTypeMetadata(TypeDefinitionHandle typeHandle, string?[] genericParameters)
    {
        var handles = new List<TypeDefinitionHandle>();
        for (var current = typeHandle; !current.IsNil;)
        {
            handles.Add(current);
            current = _reader.GetTypeDefinition(current).GetDeclaringType();
        }

        handles.Reverse();

        var outermostType = _reader.GetTypeDefinition(handles[0]);
        var builder = new StringBuilder();
        var genericParameterIndex = 0;

        for (var i = 0; i < handles.Count; i++)
        {
            if (i != 0)
                builder.Append('.');

            var type = _reader.GetTypeDefinition(handles[i]);
            var metadataName = _reader.GetString(type.Name);
            var arity = GetGenericArity(metadataName, out var name);
            builder.Append(name);

            if (arity == 0)
                continue;

            builder.Append('<');
            for (var j = 0; j < arity; j++)
            {
                if (j != 0)
                    builder.Append(", ");

                builder.Append(genericParameters[genericParameterIndex] ?? "!" + genericParameterIndex);
                genericParameterIndex++;
            }

            builder.Append('>');
        }

        return new TypeMetadata(_reader.GetString(outermostType.Namespace), builder.ToString());
    }

    private static string AddGenericArguments(string metadataName, ImmutableArray<TypeMetadata> typeArguments)
    {
        var builder = new StringBuilder();
        var argumentIndex = 0;
        var segments = metadataName.Split('.');

        for (var i = 0; i < segments.Length; i++)
        {
            if (i != 0)
                builder.Append('.');

            var arity = GetGenericArity(segments[i], out var name);
            builder.Append(name);

            if (arity == 0)
                continue;

            builder.Append('<');
            for (var j = 0; j < arity; j++)
            {
                if (j != 0)
                    builder.Append(", ");

                builder.Append(typeArguments[argumentIndex].FullName);
                argumentIndex++;
            }

            builder.Append('>');
        }

        return builder.ToString();
    }

    private static int GetGenericArity(string metadataName, out string name)
    {
        var tick = metadataName.LastIndexOf('`');
        if (tick < 0 || !int.TryParse(metadataName.AsSpan(tick + 1), out var arity))
        {
            name = metadataName;
            return 0;
        }

        name = metadataName[..tick];
        return arity;
    }

    private static string AddGenericParameters(string name, string?[] genericParameters)
    {
        if (genericParameters.Length == 0)
            return name;

        GetGenericArity(name, out name);
        return $"{name}<{string.Join(", ", genericParameters)}>";
    }
}
