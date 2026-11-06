using FugaTES.HostIntegracao.Mm;
using Microsoft.Extensions.Configuration;
namespace FugaTES.Testes;
public sealed class MmProcessEnvironmentTests
{
 static MmLinuxOptions Options(string origin,string client,string database,string role)=>new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>
 { ["StateDirectory"]=database=="fugates_qas"?"/var/lib/fugates-mm-qas":"/var/lib/fugates-mm",["SapOrigin"]=origin,["SapClient"]=client,["Database"]=database,["Role"]=role }).Build().Get<MmLinuxOptions>()!;
 [Theory]
 [InlineData("https://vhfufds4ci.sap.fugacouros.com.br:44300","110","fugates_dev","fugates_mm_app")]
 [InlineData("https://vhfufqs4ci.sap.fugacouros.com.br:44300","120","fugates_qas","fugates_qas_mm_app")]
 public void Process_accepts_only_its_matching_SAP_and_MM_database(string origin,string client,string database,string role)=>Options(origin,client,database,role).Validate();
 [Theory]
 [InlineData("https://vhfufds4ci.sap.fugacouros.com.br:44300","120","fugates_dev","fugates_mm_app")]
 [InlineData("https://vhfufqs4ci.sap.fugacouros.com.br:44300","110","fugates_dev","fugates_mm_app")]
 [InlineData("https://vhfufqs4ci.sap.fugacouros.com.br:44300","120","fugates_dev","fugates_mm_app")]
 [InlineData("https://vhfufds4ci.sap.fugacouros.com.br:44300","110","fugates_qas","fugates_qas_mm_app")]
 [InlineData("https://vhfufds4ci.sap.fugacouros.com.br:44300","110","fugates_qas","fugates_mm_app")]
 [InlineData("https://vhfufqs4ci.sap.fugacouros.com.br:44300","120","fugates_qas","fugates_mm_app")]
 public void Process_rejects_crossed_pairs_even_while_inert(string origin,string client,string database,string role)=>Assert.Throws<InvalidOperationException>(()=>Options(origin,client,database,role).Validate());
}
