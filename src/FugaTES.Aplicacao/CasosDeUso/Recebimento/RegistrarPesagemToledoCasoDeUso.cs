using FugaTES.Aplicacao.Servicos.Recebimento;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FugaTES.Aplicacao.Contratos.Recebimento;
using FugaTES.Aplicacao.Modelos.Recebimento;
namespace FugaTES.Aplicacao.CasosDeUso.Recebimento;

public sealed class RegistrarPesagemToledoCasoDeUso(IRegistroPesagemToledoStore store,IToledoSupplierLookup? suppliers=null)
{
    public async Task<ReciboPesagemToledo> ExecutarAsync(PesagemToledoRecebida documento,CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(documento);
        ct.ThrowIfCancellationRequested();
        if(documento.Origem!="TOLEDO" || string.IsNullOrWhiteSpace(documento.Centro) || string.IsNullOrWhiteSpace(documento.IdPesagem)
            || documento.DataPesagemLocal.Kind!=DateTimeKind.Unspecified)throw new ArgumentException("Invalid Toledo receipt identity.");
        if(string.IsNullOrWhiteSpace(documento.OrigemAutenticada)||documento.OrigemAutenticada.Length>160||documento.OrigemAutenticada.Any(char.IsControl))
            throw new ArgumentException("Authenticated Toledo origin required.");
        var fingerprint=Fingerprint(documento.DocumentoJson);
        // A persisted identical receipt keeps its frozen proof and never triggers another SAP lookup.
        if(!ToledoDeliveryPartners.RequiresLookup(documento.DocumentoJson)||!await store.JaRecebidoAsync(documento,fingerprint,ct))
            documento=documento with{DeliveryPartners=await ToledoDeliveryPartners.ValidateAsync(documento.DocumentoJson,suppliers,ct)};
        var receipt=await store.RegistrarAsync(documento,fingerprint,ct);
        if(receipt.ReciboId==Guid.Empty || receipt.RecebidoEm==default)throw new PesagemToledoStorageException();
        return receipt;
    }
    public static string Fingerprint(string json)
    {
        using var document=JsonDocument.Parse(json,new(){MaxDepth=32});
        using var stream=new MemoryStream();
        using(var writer=new Utf8JsonWriter(stream)){Write(document.RootElement,writer);}
        return Convert.ToHexString(SHA256.HashData(stream.ToArray()));
    }
    private static void Write(JsonElement node,Utf8JsonWriter writer)
    {
        if(node.ValueKind==JsonValueKind.Object)
        {
            writer.WriteStartObject();
            foreach(var p in node.EnumerateObject().OrderBy(x=>x.Name,StringComparer.Ordinal)){writer.WritePropertyName(p.Name);Write(p.Value,writer);}
            writer.WriteEndObject();
        }
        else if(node.ValueKind==JsonValueKind.Array){writer.WriteStartArray();foreach(var x in node.EnumerateArray())Write(x,writer);writer.WriteEndArray();}
        else node.WriteTo(writer);
    }
}
