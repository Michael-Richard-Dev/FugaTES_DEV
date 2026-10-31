using System.Text.Json;
using System.Reflection;
using System.Reflection.Emit;

namespace FugaTES.Testes;

public sealed class ArchitectureBoundaryRegressionTests
{
    [Fact]
    public void Wire_dto_boundary_rejects_real_typed_leaks_including_generic_wrappers()
    {
        var wire = typeof(FugaTES.Integracao.Toledo.Fakes.ContratoToledoGatewayFake).Assembly
            .GetType("FugaTES.Integracao.Toledo.Dtos.Contrato.ContratoToledoRequestDto", throwOnError: true)!;
        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => ArchitectureBoundaries.NoToledoWireDtos([typeof(Task<>).MakeGenericType(wire)]));
        ArchitectureBoundaries.NoToledoWireDtos([typeof(JsonSnapshot)]);
    }

    [Fact]
    public void Wire_dto_boundary_rejects_discarded_generic_call_without_typed_field_or_local()
    {
        var wire = typeof(FugaTES.Integracao.Toledo.Fakes.ContratoToledoGatewayFake).Assembly
            .GetType("FugaTES.Integracao.Toledo.Dtos.Contrato.ContratoToledoRequestDto", throwOnError: true)!;
        var module = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("SyntheticWireBoundary"), AssemblyBuilderAccess.Run)
            .DefineDynamicModule("SyntheticWireBoundary");
        var owner = module.DefineType("DiscardedWireCall", TypeAttributes.Public);
        var method = owner.DefineMethod("NotInvoked", MethodAttributes.Public | MethodAttributes.Static, typeof(void), Type.EmptyTypes);
        var deserialize = typeof(JsonSerializer).GetMethods().Single(candidate => candidate.Name == "Deserialize" && candidate.IsGenericMethodDefinition &&
            candidate.GetParameters().Select(parameter => parameter.ParameterType).SequenceEqual(new[] { typeof(string), typeof(JsonSerializerOptions) }));
        var il = method.GetILGenerator();
        il.Emit(OpCodes.Ldstr, "{}");
        il.Emit(OpCodes.Ldnull);
        il.Emit(OpCodes.Call, deserialize.MakeGenericMethod(wire));
        il.Emit(OpCodes.Pop);
        il.Emit(OpCodes.Ret);
        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => ArchitectureBoundaries.NoToledoWireDtos([owner.CreateType()!]));
    }

    [Fact]
    public void Http_boundary_rejects_direct_inline_dispatch_without_invoking_it()
    {
        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => ArchitectureBoundaries.NoHttpTransport(typeof(DirectNetwork)));
        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => ArchitectureBoundaries.NoHttpTransport(typeof(ConstructorNetwork)));
        ArchitectureBoundaries.NoHttpTransport(typeof(JsonSnapshot));
    }

    private sealed class JsonSnapshot
    {
        public JsonElement Data { get; set; }
    }

    private sealed class DirectNetwork
    {
        public Task<HttpResponseMessage> NotInvoked() => new HttpClient().GetAsync("https://example.invalid");
    }

    private sealed class ConstructorNetwork
    {
        public ConstructorNetwork() => _ = new HttpClient().GetAsync("https://example.invalid");
    }
}
