using FugaTES.Persistencia.Transportadoras;
namespace FugaTES.Testes;
public class CarrierMigrationHotfixTests
{
    static string Root {get {var d=new DirectoryInfo(Environment.CurrentDirectory);while(d is not null){if(File.Exists(Path.Combine(d.FullName,"FugaTES.slnx")))return d.FullName;d=d.Parent;}throw new InvalidOperationException("REPO_ROOT_MISSING");}}
    static string Sql(string file)=>File.ReadAllText(Path.Combine(Root,"docs","Incrementos","007",file+"_007_CARRIER_SYNC_R3.sql"));
    [Fact] public void Checkpoint_blocks_every_non_synced_state_with_null_safe_comparison()
    {Assert.Contains("pending_attempt_id is not null",CarrierSyncPostgreSqlStore.CheckpointSql,StringComparison.OrdinalIgnoreCase);Assert.Contains("status is distinct from 'SYNCED'",CarrierSyncPostgreSqlStore.CheckpointSql,StringComparison.OrdinalIgnoreCase);Assert.DoesNotContain("status in(",CarrierSyncPostgreSqlStore.CheckpointSql,StringComparison.OrdinalIgnoreCase);}
    [Fact] public void Checkpoint_duplicate_confirmation_does_not_physically_advance_again()
    {Assert.Contains("is distinct from row(@watermark,@upper,@page,@visited)",CarrierSyncPostgreSqlStore.CheckpointSql,StringComparison.OrdinalIgnoreCase);}
    [Fact] public void Grants_are_explicit_minimal_and_role_bound()
    {var sql=Sql("MIGRATION");Assert.Contains("GRANT SELECT, INSERT, UPDATE ON TABLE",sql);Assert.Contains("GRANT USAGE ON SCHEMA fugates",sql);Assert.Contains("attidentity",sql);var executable=System.Text.RegularExpressions.Regex.Replace(sql,@"--[^\r\n]*","");Assert.DoesNotContain("GRANT USAGE ON SEQUENCE",executable,StringComparison.OrdinalIgnoreCase);Assert.Contains("fugates.carrier_app_role",sql);Assert.Contains("pg_roles",sql);Assert.DoesNotContain("GRANT ALL",sql,StringComparison.OrdinalIgnoreCase);Assert.DoesNotContain("GRANT DELETE",sql,StringComparison.OrdinalIgnoreCase);Assert.DoesNotContain("GRANT CREATE",sql,StringComparison.OrdinalIgnoreCase);}
    [Theory][InlineData("MIGRATION")][InlineData("ROLLBACK")]
    public void Plpgsql_blocks_terminate_before_dollar_delimiter(string file)
    {Assert.DoesNotMatch(@"(?im)^\s*END\s+\$carrier_",Sql(file));Assert.Matches(@"(?im)^\s*END;\s*\$carrier_",Sql(file));}
    [Fact] public void Public_is_revoked_for_tables_and_identity_sequence()
    {var sql=Sql("MIGRATION");Assert.Contains("FROM PUBLIC",sql);Assert.Contains("REVOKE ALL ON SEQUENCE fugates.carrier_sync_step_id_seq FROM PUBLIC",sql);}
    [Fact] public void Rollback_guards_execute_before_first_drop()
    {var sql=Sql("ROLLBACK");Assert.True(sql.IndexOf("CARRIER_ROLLBACK_OPERATIONAL_DATA",StringComparison.Ordinal)<sql.IndexOf("DROP TABLE",StringComparison.Ordinal));Assert.Contains("CARRIER_ROLLBACK_WORKER_NOT_DISABLED",sql);Assert.Contains("pg_try_advisory_xact_lock",sql);Assert.Contains("ACCESS EXCLUSIVE",sql);Assert.DoesNotContain("CASCADE",sql,StringComparison.OrdinalIgnoreCase);}
    [Theory][InlineData("cycle")][InlineData("state")][InlineData("attempt")][InlineData("step")][InlineData("request")]
    public void Rollback_checks_each_operational_table(string table)=>Assert.Contains("EXISTS (SELECT 1 FROM fugates.carrier_sync_"+table+")",Sql("ROLLBACK"));
    [Fact] public void Rollback_requires_original_seed_not_only_empty_history()
    {var sql=Sql("ROLLBACK");foreach(var field in new[]{"watermark","upper_bound","page_cursor","heartbeat_utc","next_cycle_utc"})Assert.Contains(field+" IS NOT NULL",sql);Assert.Contains("visited_pages IS DISTINCT FROM ARRAY[]::text[]",sql);Assert.Contains("enabled IS DISTINCT FROM false",sql);Assert.Contains("is_called",sql);}
    [Fact] public void Validation_checks_catalogs_effective_grants_and_duplicates_readonly()
    {var sql=Sql("VALIDATION");foreach(var term in new[]{"BEGIN TRANSACTION READ ONLY","information_schema.columns","pg_constraint","pg_index","has_table_privilege","has_schema_privilege","has_sequence_privilege","aclexplode","HAVING count(*) > 1"})Assert.Contains(term,sql);var clean=System.Text.RegularExpressions.Regex.Replace(sql,@"--[^\r\n]*|'(?:''|[^'])*'","");Assert.DoesNotMatch(@"(?im)^\s*(INSERT|UPDATE|DELETE|TRUNCATE|CREATE|ALTER|DROP|GRANT|REVOKE)\b",clean);}
}