using FugaTES.Aplicacao.Configuracao;
using FugaTES.Aplicacao.CasosDeUso.Recebimento;
using FugaTES.Aplicacao.Contratos.Recebimento;
using FugaTES.Persistencia.Recebimento;
using FugaTES.Integracao.SAP.Http;
using FugaTES.Persistencia.MotorIntegracao;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace FugaTES.Callback.Http;

public static class ToledoCallbackComposition
{
    public const string DataSourceKey="TOLEDO_DEV_5433";
    public static void Configure(IServiceCollection services,IConfiguration configuration,string correlationProvider)
    {
        var flag=configuration["ToledoInbound:Enabled"];
        if(flag is not null&&!bool.TryParse(flag,out _))throw new InvalidOperationException("ToledoInbound:Enabled must be explicit true/false.");
        if(!bool.TryParse(flag,out var enabled)||!enabled)return;
        var cs=ValidateDedicatedBinding(configuration.GetConnectionString("Toledo"));
        services.AddKeyedSingleton<NpgsqlDataSource>(DataSourceKey,(_,_)=>NpgsqlDataSourceFactory.CriarAcm(cs));
        var sapAdmission=ToledoSapReturnComposition.Configure(services,configuration);
        services.TryAddScoped<IRegistroPesagemToledoStore>(sp=>new RegistroPesagemToledoPostgreSql(sp.GetRequiredKeyedService<NpgsqlDataSource>(DataSourceKey),sapAdmission));
        // Admission requires a proven supplier before any new receipt commit, independently of the send window.
        ToledoSapReturnComposition.ConfigureAuthentication(services,configuration);
        if(!Uri.TryCreate(configuration["Sap:BaseUrl"],UriKind.Absolute,out var sap)||sap.Scheme!="https"||sap.UserInfo.Length!=0||sap.Query.Length!=0||sap.Fragment.Length!=0||
           !FugaTES.Aplicacao.Configuracao.AmbientesSap.EhAprovado(sap.GetLeftPart(UriPartial.Authority),configuration["Sap:SapClient"]))
            throw new InvalidOperationException("TOLEDO_SAP_AUTHORIZED_DEV_ENDPOINT_REQUIRED");
        services.AddKeyedScoped<ToledoSapHttpTransport>("TOLEDO_SUPPLIER_READ",(_,_)=>new());
        services.TryAddScoped<FugaTES.Aplicacao.Servicos.Recebimento.IToledoSupplierLookup>(sp=>new ToledoSupplierSapLookup(new(
            sp.GetRequiredKeyedService<ToledoSapHttpTransport>("TOLEDO_SUPPLIER_READ").Client,
            new(){BaseUrl=sap,SapClient=configuration["Sap:SapClient"]!,Timeout=TimeSpan.FromSeconds(20)},sp.GetRequiredService<FugaTES.Integracao.SAP.Autenticacao.IAutenticacaoSap>(),new System.Net.CookieContainer())));
        services.TryAddScoped<RegistrarPesagemToledoCasoDeUso>();
        services.TryAddSingleton<IToledoCallbackAdmission>(new ProtectedToledoCallbackAdmission(ToledoCallbackAdmissionOptions.Parse(configuration)));
        // No ordinary NpgsqlDataSource registration, legacy provider change, host or worker.
    }
    /// <summary>
    /// O vinculo dedicado ao banco. <paramref name="ambientesAceitos"/> diz QUAIS bancos este
    /// vinculo pode alcancar; vazio significa so o DEV110, que e o comportamento de sempre.
    ///
    /// O par banco+papel e conferido JUNTO: papel no PostgreSQL e do CLUSTER e nao do banco,
    /// entao exigir "banco aprovado" e "papel aprovado" em separado deixaria passar o banco do
    /// QAS com o papel do DEV - e seria uma gravacao no ambiente errado com credencial valida.
    ///
    /// Em 06/11/2026 so o receptor da devolutiva SD passou a aceitar o QAS120. O receptor Toledo
    /// e o ACM continuam no DEV110, e e por isso que o parametro existe em vez de a funcao
    /// consultar a lista inteira.
    /// </summary>
    public static string ValidateDedicatedBinding(string? cs,params AmbienteBanco[] ambientesAceitos)
    {
        var aceitos=ambientesAceitos is {Length:>0}?ambientesAceitos:[AmbientesBanco.Dev110];
        try
        {
            var v=new NpgsqlConnectionStringBuilder(cs??"");
            var ambiente=AmbientesBanco.Por(v.Database,v.Username,aceitos);
            if(v.Host is not("127.0.0.1" or "localhost")||v.Port!=5433||v.SearchPath!="fugates"||
                ambiente is null||v.Username!=ambiente.PapelAcm||
                v.Enlist||!NpgsqlDataSourceFactory.HasExplicitAcmPassfile(v))throw new InvalidOperationException();
            return v.ConnectionString;
        }
        catch{throw new InvalidOperationException("TOLEDO_DEDICATED_5433_BINDING_REQUIRED");}
    }
}
