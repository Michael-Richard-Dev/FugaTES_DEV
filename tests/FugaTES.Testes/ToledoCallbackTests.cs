using System.Text;
using FugaTES.Callback.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace FugaTES.Testes;

public sealed class ToledoCallbackTests
{
    public static string ValidJson => """
        {"SistemaBalanca":"Toledo","DataPesagem":"10/27/2026 12:00:22 PM","TipoPesagem":"2","IdPesagem":"0020318",
         "UsuarioPesagem":"SYNTHETIC","UsuarioClassificacao":"","IdPlanejamento":"","Centro":"3011","IdAgendamento":"",
         "IdTransportadora":"14300001","PesoBruto":"40000.0","PesoTara":"20000.0","Material":"7000000",
         "PesoAntesDesconto":"20000.0","PesoAposDesconto":"20000.0","NomeMotorista":"","CpfMotorista":"","Placa":"","Observacao":"SYNTHETIC OFFLINE ONLY",
         "to_QualidPes":[{"Tipo":"1","ValorAferido":"12.5"},{"Tipo":"","ValorAferido":""},{"Tipo":"3","ValorAferido":""},{"Tipo":"","ValorAferido":"0"},{"Tipo":"5","ValorAferido":"1"},{"Tipo":"6","ValorAferido":"2"},{"Tipo":"7","ValorAferido":"3"},{"Tipo":"8","ValorAferido":"4"}],
         "to_Pesagem":[{"Indice":"1","NrIndiceTotal":"1","ContratoACM":"0000001155","IdParceiro":"0020000051","ParceiroNome":"SYNTHETIC",
             "ParceiroCpfCnpj":"","IdParceiroEntrega":"","ParceiroEntregaNome":"","ObservacaoPesagem":"","PercentualDesdobro":"100",
             "PesoAntesDesconto":"20000.0","PesoAposDesconto":"20000.0","ChaveAcessoNFe":""}]}
        """;

    [Fact]
    public async Task Valid_Toledo_reaches_receipt_boundary_instead_of_invalid_callback()
    {
        var response=await Post(ValidJson,new ServiceCollection().BuildServiceProvider());
        Assert.Equal(503,response.Status);
        Assert.Contains("TOLEDO_STORAGE_NOT_CONFIGURED",response.Body);
    }
    internal static async Task<(int Status,string Body)> Post(string json,IServiceProvider services)
    {
        var context=new DefaultHttpContext{RequestServices=services};
        context.Request.Method="POST";context.Request.ContentType="application/json";
        context.Request.Body=new MemoryStream(Encoding.UTF8.GetBytes(json));context.Response.Body=new MemoryStream();
        await GuardianCallbackEndpoint.HandleEventoAsync(context);
        context.Response.Body.Position=0;return(context.Response.StatusCode,await new StreamReader(context.Response.Body).ReadToEndAsync());
    }
}
