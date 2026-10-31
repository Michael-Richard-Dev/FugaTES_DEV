using FugaTES.Callback.Http;
using FugaTES.Aplicacao.Servicos.Recebimento;
using FugaTES.Integracao.SAP.Autenticacao;
using Microsoft.Extensions.Configuration;
namespace FugaTES.Testes;
public sealed class ToledoSapContinuousTests
{
 internal static Dictionary<string,string?> Profile()=>new(){["ToledoSapReturn:Enabled"]="true",["ToledoSapReturn:Environment"]="DEV",["ToledoSapReturn:ExecutionAuthorized"]="true",["ToledoSapReturn:AutomaticProcessingEnabled"]="true",["ToledoSapReturn:OriginalDocumentApproved"]="true",["ToledoSapReturn:NotBeforeUtc"]="2026-10-31T11:29:11Z",["ToledoSapReturn:ContinuousEnabled"]="true",["ToledoSapReturn:ApprovedSourceIdentity"]="OFFLINE_PRIVATE_SOURCE",["ToledoSapReturn:AuthorizationReference"]="OFFLINE_CONTINUOUS_APPROVAL",["Sap:BaseUrl"]="https://vhfufds4ci.sap.fugacouros.com.br:44300",["Sap:SapClient"]="110"};
 [Fact] public void Explicit_continuous_mode_accepts_new_receipts_after365days_without_future_expiry_date()
 {
  var services=new ServiceCollection();services.AddSingleton<IAutenticacaoSap,Auth>();
  var window=ToledoSapReturnComposition.Configure(services,new ConfigurationBuilder().AddInMemoryCollection(Profile()).Build());
  Assert.NotNull(window);Assert.Equal(default,window.FimUtc);Assert.True(window.Admite(DateTimeOffset.Parse("2027-11-01T11:00:00Z"),"OFFLINE_PRIVATE_SOURCE"));
  Assert.False(window.Admite(DateTimeOffset.Parse("2026-10-31T11:29:10Z"),"OFFLINE_PRIVATE_SOURCE"));
 }
 [Theory][InlineData(null)][InlineData("false")][InlineData("maybe")]
 public void Missing_or_invalid_continuous_authorization_ne