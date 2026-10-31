using System.Text.Json.Nodes;
using FugaTES.Integracao.SAP.Http;
// Offline verification of user-provided evidence. Never logs body, names, tax IDs or credentials.
if(args.Length!=2)throw new InvalidOperationException("Two local evidence paths required");
var root=JsonNode.Parse(File.ReadAllText(args[0]))!.AsObject();
// These are the operator's explicit Postman edits, NOT defaults for any other receipt.
root["IdPesagem"]="20318";root["DataPesagem"]="/Date(1772874589888)/";
root["to_QualidPes"]=JsonNode.Parse("""[{"Tipo":"IMPUREZA","ValorAferido":"4.0"},{"Tipo":"UMIDADE","ValorAferido":"9.6"}]""");
var prepared=ToledoSapODataPayload.Prepare(root.ToJsonString(),new("America/Sao_Paulo"));
var proof=ToledoSapAtomReceipt.Validate(File.ReadAllText(args[1]),prepared,new("https://vhfufds4ci.sap.fugacouros.com.br:44300/sap/opu/odata/sap/ZUI_ACM_RECPESAGEM/ReceberPesagem?sap-client=110"));
if(proof is null)throw new InvalidOperationException("SUPPLIED_ATOM_RESPONSE_DID_NOT_MATCH");
Console.WriteLine("SUPPLIED_201_ATOM_CONTRACT=PASS; ORIGINAL_UNCHANGED; NETWORK_CALLS=0; BUSINESS_DATA_OUTPUT=0");
