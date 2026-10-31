namespace FugaTES.Aplicacao.Modelos.Recebimento;

// Receipt identity is independent from IntegrationId, Guardian protocol and ticket.
public sealed record PesagemToledoRecebida(string Origem,string Centro,string IdPesagem,
    DateTime DataPesagemLocal,string DocumentoJson)
{
    public FugaTES.Aplicacao.Servicos.Recebimento.ToledoDeliveryPartnerProof? DeliveryPartners {get;init;}

    public string Material {get;init;}="";
    public string OrigemAutenticada {get;init;}="";
    public IReadOnlyList<ItemPesagemToledoRecebido> Itens {get;init;}=[];
}
public sealed record ItemPesagemToledoRecebido(int Indice,int NrIndiceTotal,string ContratoACM,string IdParceiro,string DocumentoJson);
public sealed record CorrelacaoItemPesagemToledo(int Indice,string Estado,Guid? IntegracaoId=null,long? VersaoH4=null);

public sealed record ReciboPesagemToledo(Guid ReciboId,DateTimeOffset RecebidoEm,bool Idempotente)
{
    public IReadOnlyList<CorrelacaoItemPesagemToledo> Itens {get;init;}=[];
    public Guid? SapIntegracaoId {get;init;}
}
public sealed class PesagemToledoConflitoException : Exception;
public sealed class PesagemToledoStorageException : Exception;
