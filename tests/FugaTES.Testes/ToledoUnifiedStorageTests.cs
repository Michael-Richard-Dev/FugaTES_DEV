using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Nodes;
using FugaTES.Callback.Http;
using FugaTES.Aplicacao.CasosDeUso.Recebimento;
using FugaTES.Aplicacao.Modelos.Recebimento;
using FugaTES.Aplicacao.Modelos.ContratosSapToledo;
using FugaTES.Aplicacao.Servicos.ContratosSapToledo;
using FugaTES.Persistencia.Recebimento;
using Npgsql;
using Xunit;
namespace FugaTES.Testes;

public sealed class ToledoUnifiedStorageTests
{
 static PesagemToledoRecebida Document(bool two=false)
 {
  var node=JsonNode.Parse(ToledoCallbackTests.ValidJson)!;
  if(two){var items=node["to_Pesagem"]!.AsArray();items.Add(items[0]!.DeepClone());for(var i=0;i<2;i++){items[i]!["Indice"]=(i+1).ToString();items[i]!["NrIndiceTotal"]="2";items[i]!["PercentualDesdobro"]="50";}}
  using var json=JsonDocument.Parse(node.ToJsonString());return ToledoPesagemCallbackDto.Deserializar(json.RootElement).Mapear() with{OrigemAutenticada="OFFLINE_AUTHENTICATED_SOURCE"};
 }
 [Fact] public async Task Known_H4_is_linked_without_mapping_Indice_to_SAP_item_and_raw_document_is_kept()
 {
  var doc=Document();using var db=new MemoryDb();var id=db.AddH4(doc);var store=new RegistroPesagemToledoPostgreSql(db);
  var receipt=await new RegistrarPesagemToledoCasoDeUso(store).ExecutarAsync(doc,default);
  var item=Assert.Single(receipt.Itens);Assert.Equal("H4_MATCHED",item.Estado);Assert.Equal(id,item.IntegracaoId);Assert.Equal(1,item.Indice);Assert.Equal(1,item.VersaoH4);
  Assert.Equal(doc.DocumentoJson,db.Parents.Values.Single().Json);Assert.Equal("10",db.SnapshotItem);Assert.Equal(1,db.Commits);Assert.Equal(0,db.Rollbacks);Assert.Equal(1,db.Audits);
  Assert.DoesNotContain(db.Sql,sql=>sql.Contains("update fugates.integracao")||sql.Contains("tentativa_integracao")||sql.Contains("correlacao_guardian_sap"));
 }
 [Theory] [InlineData("absent")] [InlineData("ambiguous")] [InlineData("fingerprint")] [InlineData("different-partner")] [InlineData("description-is-not-identity")]
 public async Task Unknown_or_ambiguous_H4_is_quarantined_without_inventing_an_integration(string mode)
 {
  var doc=Document();using var db=new MemoryDb();if(mode!="absent")db.AddH4(doc);
  if(mode=="ambiguous")db.Candidates.Add((object[])db.Candidates[0].Clone());
  if(mode=="fingerprint")db.Candidates[0][3]=new string('F',64);
  if(mode=="different-partner")db.OverrideSnapshotPartner="DIFFERENT";
  if(mode=="description-is-not-identity")db.Candidates.Clear();
  var receipt=await new RegistrarPesagemToledoCasoDeUso(new RegistroPesagemToledoPostgreSql(db)).ExecutarAsync(doc,default);
  Assert.Null(Assert.Single(receipt.Itens).IntegracaoId);Assert.StartsWith("H4_",receipt.Itens.Single().Estado);Assert.Single(db.Parents);Assert.Single(db.Items);Assert.Equal(1,db.Commits);
 }
 [Fact] public async Task All_items_and_delivery_audit_commit_in_one_transaction_and_redelivery_does_not_duplicate_them()
 {
  var doc=Document(true);using var db=new MemoryDb();db.AddH4(doc);var use=new RegistrarPesagemToledoCasoDeUso(new RegistroPesagemToledoPostgreSql(db));
  var first=await use.ExecutarAsync(doc,default);var again=await use.ExecutarAsync(doc,default);
  Assert.Equal(first.ReciboId,again.ReciboId);Assert.False(first.Idempotente);Assert.True(again.Idempotente);
  Assert.Single(db.Parents);Assert.Equal(2,db.Items.Count);Assert.Equal(2,db.Audits);Assert.Equal(2,db.Commits);Assert.Equal(0,db.Rollbacks);
  Assert.Equal(2,again.Itens.Count);Assert.All(db.CommandTransactions,transaction=>Assert.NotNull(transaction));
 }
 [Fact] public async Task Divergent_redelivery_conflicts_and_does_not_overwrite_original_or_audit_success()
 {
  var doc=Document();using var db=new MemoryDb();var use=new RegistrarPesagemToledoCasoDeUso(new RegistroPesagemToledoPostgreSql(db));await use.ExecutarAsync(doc,default);
  var changed=doc with{DocumentoJson=doc.DocumentoJson.Replace("40000.0","40001.0")};
  await Assert.ThrowsAsync<PesagemToledoConflitoException>(()=>use.ExecutarAsync(changed,default));
  Assert.Equal(doc.DocumentoJson,db.Parents.Values.Single().Json);Assert.Equal(1,db.Commits);Assert.Equal(1,db.Rollbacks);Assert.Equal(1,db.Audits);
 }
 [Fact] public async Task Failure_on_second_item_rolls_back_parent_all_items_and_delivery_audit()
 {
  var doc=Document(true);using var db=new MemoryDb{FailItem=2};var use=new RegistrarPesagemToledoCasoDeUso(new RegistroPesagemToledoPostgreSql(db));
  await Assert.ThrowsAsync<IOException>(()=>use.ExecutarAsync(doc,default));Assert.Empty(db.Parents);Assert.Empty(db.Items);Assert.Equal(0,db.Audits);Assert.Equal(0,db.Commits);Assert.Equal(1,db.Rollbacks);
 }
 [Fact] public async Task Receipt_return_waits_for_commit_and_unknown_commit_has_no_positive_result_or_retry()
 {
  var doc=Document();using var db=new MemoryDb{HoldCommit=new(TaskCreationOptions.RunContinuationsAsynchronously)};
  var use=new RegistrarPesagemToledoCasoDeUso(new RegistroPesagemToledoPostgreSql(db));var pending=use.ExecutarAsync(doc,default);
  await db.CommitEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));Assert.False(pending.IsCompleted);db.HoldCommit.SetResult();Assert.NotEqual(Guid.Empty,(await pending).ReciboId);
  using var unknown=new MemoryDb{UnknownCommit=true};var attempt=new RegistrarPesagemToledoCasoDeUso(new RegistroPesagemToledoPostgreSql(unknown));
  await Assert.ThrowsAsync<IOException>(()=>attempt.ExecutarAsync(doc,default));Assert.Equal(1,unknown.OpenCount);Assert.Equal(1,unknown.Commits);
 }
 [Fact] public async Task H4_read_failure_rolls_back_instead_of_claiming_an_unknown_contract_was_queried()
 {
  var doc=Document();using var db=new MemoryDb{FailH4=true};
  await Assert.ThrowsAsync<IOException>(()=>new RegistrarPesagemToledoCasoDeUso(new RegistroPesagemToledoPostgreSql(db)).ExecutarAsync(doc,default));
  Assert.Empty(db.Parents);Assert.Equal(1,db.Rollbacks);Assert.Equal(0,db.Commits);
 }
 [Fact] public async Task Delivery_validation_is_durable_before_commit_and_original_JSON_stays_exact()
 {
  var doc=Document();using var db=new MemoryDb();var receipt=await new RegistrarPesagemToledoCasoDeUso(new RegistroPesagemToledoPostgreSql(db)).ExecutarAsync(doc,default);
  Assert.Equal(1,db.ProofAudits);Assert.Equal(doc.DocumentoJson,db.Parents.Values.Single().Json);
  var proof=JsonSerializer.Deserialize<FugaTES.Aplicacao.Servicos.Recebimento.ToledoDeliveryPartnerProof>(db.DeliveryProof!)!;
  Assert.Equal(RegistrarPesagemToledoCasoDeUso.Fingerprint(doc.DocumentoJson),proof.OriginalSha256);Assert.Equal("GUARDIAN_DEFAULT",proof.Items.Single().Source);
  await new RegistrarPesagemToledoCasoDeUso(new RegistroPesagemToledoPostgreSql(db)).ExecutarAsync(doc,default);Assert.Equal(1,db.ProofAudits);
 }
 [Fact] public async Task Validation_proof_storage_failure_rolls_back_receipt_instead_of_ACK_or_queue()
 {
  using var db=new MemoryDb{FailProof=true};await Assert.ThrowsAsync<IOException>(()=>new RegistrarPesagemToledoCasoDeUso(new RegistroPesagemToledoPostgreSql(db)).ExecutarAsync(Document(),default));
  Assert.Empty(db.Parents);Assert.Empty(db.Items);Assert.Equal(0,db.Commits);Assert.Equal(1,db.Rollbacks);Assert.Equal(0,db.Audits);
 }
 sealed record Parent(Guid Id,DateTime When,string Fingerprint,string Json);
 // Only a SQL-boundary model. These tests never claim to validate a physical PostgreSQL COMMIT.
 sealed class MemoryDb:DbDataSource
 {
  public override string ConnectionString=>"OFFLINE_NO_DATABASE";
  public Dictionary<string,Parent> Parents=[];public List<(Guid Id,int Index,string State,Guid? Integration,long? Version)> Items=[];
  public List<object[]> Candidates=[];public List<string> Sql=[];public List<DbTransaction?> CommandTransactions=[];
  public int Commits,Rollbacks,Audits,OpenCount,FailItem,ProofAudits;public bool FailProof;public string? DeliveryProof;public bool UnknownCommit,FailH4;public string? OverrideSnapshotPartner;public string? SnapshotItem;
  public TaskCompletionSource? HoldCommit;public TaskCompletionSource CommitEntered=new(TaskCreationOptions.RunContinuationsAsynchronously);
  protected override DbConnection CreateDbConnection()=>new Connection(this);
  public Guid AddH4(PesagemToledoRecebida doc)
  {
   var i=doc.Itens[0];var s=new ContratoSapToledoSnapshot(null,i.ContratoACM,"AC3P","SYNTHETIC OFFLINE DESCRIPTION",null,null,null,null,null,"T",1m,null,i.IdParceiro,doc.Material,"E",null,null,null,null,null,doc.Centro,"10",[]){Item="10"};
   var canonical=new ContratoSapToledoCanonicalizer().Canonicalizar(s);var id=Guid.NewGuid();SnapshotItem=s.Item;
   Candidates.Add([id,1L,canonical.FingerprintSha256,canonical.FingerprintSha256,JsonSerializer.Serialize(canonical.Snapshot),"OFFLINE_CORRELATION"]);return id;
  }
  static object? Value(DbCommand c,string name)=>c.Parameters[name].Value is DBNull?null:c.Parameters[name].Value;
  static string Key(DbCommand c)=>Value(c,"origem")+"|"+Value(c,"centro")+"|"+Value(c,"pesagem");
  void Record(DbCommand c){Sql.Add(c.CommandText);CommandTransactions.Add(c.Transaction);}
  public DbDataReader Read(DbCommand c)
  {
   Record(c);var sql=c.CommandText;
   if(sql.Contains("insert into fugates.toledo_pesagem_recebida"))
   {
    if(Parents.ContainsKey(Key(c)))return Table([typeof(Guid),typeof(DateTime)],[]);
    var p=new Parent((Guid)Value(c,"id")!,DateTime.UtcNow,(string)Value(c,"fingerprint")!,(string)Value(c,"documento")!);Parents[Key(c)]=p;
    return Table([typeof(Guid),typeof(DateTime)],[[p.Id,p.When]]);
   }
   if(sql.Contains("from fugates.toledo_pesagem_recebida"))
   {var p=Parents.GetValueOrDefault(Key(c));return Table([typeof(Guid),typeof(DateTime),typeof(string)],p is null?[]:[[p.Id,p.When,p.Fingerprint]]);}
   if(sql.Contains("from fugates.toledo_pesagem_item"))
    return Table([typeof(int),typeof(string),typeof(Guid),typeof(long)],Items.Where(i=>i.Id==(Guid)Value(c,"id")!).Select(i=>new object[]{i.Index,i.State,(object?)i.Integration??DBNull.Value,(object?)i.Version??DBNull.Value}).ToArray());
   if(sql.Contains("from fugates.contrato_sap_toledo_versao"))
   {
    if(FailH4)throw new IOException("OFFLINE_TEST_H4_READ_FAILURE");var rows=Candidates.Select(x=>(object[])x.Clone()).ToArray();
    if(OverrideSnapshotPartner is not null&&rows.Length>0){var n=JsonNode.Parse((string)rows[0][4])!;n["IdParceiro"]=OverrideSnapshotPartner;rows[0][4]=n.ToJsonString();}
    return Table([typeof(Guid),typeof(long),typeof(string),typeof(string),typeof(string),typeof(string)],rows);
   }
   throw new InvalidOperationException("UNEXPECTED_OFFLINE_READER");
  }
  public int Execute(DbCommand c)
  {
   Record(c);
   if(c.CommandText.StartsWith("set local"))return 0;
   if(c.CommandText.Contains("insert into fugates.toledo_pesagem_item"))
   {if(FailItem>0&&Items.Count+1==FailItem)throw new IOException("OFFLINE_ITEM_FAILURE");Items.Add(((Guid)Value(c,"id")!,(int)Value(c,"indice")!,(string)Value(c,"estado")!,(Guid?)Value(c,"integracao"),(long?)Value(c,"versao")));return 1;}
   if(c.CommandText.Contains("insert into fugates.toledo_pesagem_entrega")){Audits++;return 1;}
   if(c.CommandText.Contains("insert into fugates.auditoria_integracao")){if(FailProof)throw new IOException("OFFLINE_PROOF_WRITE_FAILURE");ProofAudits++;DeliveryProof=(string)Value(c,"proof")!;return 1;}
   throw new InvalidOperationException("UNEXPECTED_OFFLINE_MUTATION");
  }
  static DbDataReader Table(Type[] types,object[][] rows)
  {var t=new DataTable();for(var i=0;i<types.Length;i++){var col=t.Columns.Add("c"+i,types[i]);if(types[i]==typeof(DateTime))col.DateTimeMode=DataSetDateTime.Utc;}foreach(var row in rows)t.Rows.Add(row);return t.CreateDataReader();}
 }
 sealed class Connection(MemoryDb db):DbConnection
 {
  [AllowNull]public override string ConnectionString{get;set;}="";public override string Database=>"OFFLINE";public override string DataSource=>"OFFLINE";public override string ServerVersion=>"OFFLINE";public override ConnectionState State=>ConnectionState.Open;
  public override void Open()=>db.OpenCount++;public override void Close(){}public override void ChangeDatabase(string name)=>throw new NotSupportedException();
  protected override DbTransaction BeginDbTransaction(IsolationLevel isolation)=>new Transaction(db,this,isolation);
  protected override DbCommand CreateDbCommand()=>new Command(db,this);
 }
 sealed class Transaction(MemoryDb db,DbConnection connection,IsolationLevel isolation):DbTransaction
 {
  readonly Dictionary<string,Parent> parents=new(db.Parents);readonly List<(Guid,int,string,Guid?,long?)> items=new(db.Items);readonly int audits=db.Audits,proofs=db.ProofAudits;readonly string? proof=db.DeliveryProof;bool committed;
  protected override DbConnection DbConnection=>connection;public override IsolationLevel IsolationLevel=>isolation;
  public override void Commit(){db.Commits++;committed=true;if(db.UnknownCommit)throw new IOException("OFFLINE_UNKNOWN_COMMIT");}
  public override async Task CommitAsync(CancellationToken ct=default){db.CommitEntered.TrySetResult();if(db.HoldCommit is not null)await db.HoldCommit.Task.WaitAsync(ct);Commit();}
  public override void Rollback(){db.Rollbacks++;if(!committed){db.Parents=new(parents);db.Items=new(items);db.Audits=audits;db.ProofAudits=proofs;db.DeliveryProof=proof;}}
 }
 sealed class Command(MemoryDb db,DbConnection connection):DbCommand
 {
  readonly NpgsqlCommand holder=new();[AllowNull]public override string CommandText{get;set;}="";public override int CommandTimeout{get;set;}public override CommandType CommandType{get;set;}
  public override bool DesignTimeVisible{get;set;}public override UpdateRowSource UpdatedRowSource{get;set;}
  protected override DbConnection? DbConnection{get;set;}=connection;protected override DbTransaction? DbTransaction{get;set;}
  protected override DbParameterCollection DbParameterCollection=>holder.Parameters;protected override DbParameter CreateDbParameter()=>new NpgsqlParameter();
  public override void Cancel(){}public override void Prepare(){}public override object? ExecuteScalar()=>throw new NotSupportedException();public override int ExecuteNonQuery()=>db.Execute(this);
  protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior)=>db.Read(this);
  protected override void Dispose(bool disposing){holder.Dispose();base.Dispose(disposing);}
 }
}
