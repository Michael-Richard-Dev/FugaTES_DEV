using System.Reflection;
using System.Reflection.Emit;

namespace FugaTES.Testes;

internal static class ArchitectureBoundaries
{
    internal static IEnumerable<Type> ContractTypes(Type owner, bool includeLocals = false)
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        var executable = owner.GetMethods(flags).Cast<MethodBase>().Concat(owner.GetConstructors(flags));
        var members = owner.GetFields(flags).Select(field => field.FieldType)
            .Concat(owner.GetInterfaces())
            .Concat(owner.BaseType is { } parent ? [parent] : Array.Empty<Type>())
            .Concat(owner.GetProperties(flags).Select(property => property.PropertyType))
            .Concat(executable.SelectMany(method => method.GetParameters().Select(parameter => parameter.ParameterType)))
            .Concat(owner.GetMethods(flags).Select(method => method.ReturnType));
        if (includeLocals)
            members = members.Concat(executable.SelectMany(method => method.GetMethodBody()?.LocalVariables.Select(local => local.LocalType) ?? []))
                .Concat(executable.SelectMany(ReferencedTypes));
        return members.SelectMany(Expand).Distinct();
    }

    private static readonly IReadOnlyDictionary<short, OpCode> Opcodes = typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(field => field.FieldType == typeof(OpCode)).Select(field => (OpCode)field.GetValue(null)!)
        .ToDictionary(opcode => opcode.Value);

    private static IEnumerable<Type> ReferencedTypes(MethodBase method)
    {
        var il = method.GetMethodBody()?.GetILAsByteArray();
        if (il is null) yield break;
        for (var offset = 0; offset < il.Length;)
        {
            short code = il[offset++];
            if (code == 0xfe) code = unchecked((short)(0xfe00 | il[offset++]));
            var opcode = Opcodes[code];
            if (opcode.OperandType is OperandType.InlineMethod or OperandType.InlineField or OperandType.InlineType or OperandType.InlineTok)
            {
                var member = method.Module.ResolveMember(BitConverter.ToInt32(il, offset),
                    method.DeclaringType?.GetGenericArguments(), method is MethodInfo info ? info.GetGenericArguments() : Type.EmptyTypes);
                if (member is Type type) yield return type;
                else if (member?.DeclaringType is { } declaring) yield return declaring;
                if (member is MethodInfo referencedMethod)
                {
                    foreach (var argument in referencedMethod.GetGenericArguments()) yield return argument;
                    yield return referencedMethod.ReturnType;
                }
                if (member is MethodBase referencedCall)
                    foreach (var parameter in referencedCall.GetParameters()) yield return parameter.ParameterType;
                if (member is FieldInfo referencedField) yield return referencedField.FieldType;
            }
            offset += opcode.OperandType switch
            {
                OperandType.InlineNone => 0,
                OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                OperandType.InlineVar => 2,
                OperandType.InlineI8 or OperandType.InlineR => 8,
                OperandType.InlineSwitch => 4 + 4 * BitConverter.ToInt32(il, offset),
                _ => 4
            };
        }
    }

    private static IEnumerable<Type> Expand(Type type)
    {
        yield return type;
        if (type.HasElementType)
            foreach (var item in Expand(type.GetElementType()!)) yield return item;
        foreach (var argument in type.GetGenericArguments())
            foreach (var item in Expand(argument)) yield return item;
    }

    internal static void NoToledoWireDtos(IEnumerable<Type> owners)
    {
        foreach (var owner in owners)
            Assert.DoesNotContain(ContractTypes(owner, includeLocals: true), type =>
                type.Namespace?.StartsWith("FugaTES.Integracao.Toledo.Dtos", StringComparison.Ordinal) == true);
    }

    internal static void NoHttpTransport(Type owner)
    {
        Assert.DoesNotContain(ContractTypes(owner, includeLocals: true), type =>
            type.Namespace?.StartsWith("System.Net.Http", StringComparison.Ordinal) == true);
        foreach (var nested in owner.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
            NoHttpTransport(nested);
    }
}
