"""One reviewed column-read ACL transaction in DEV only; no business data selected or changed."""
import base64,ctypes as C,hashlib,json,os,subprocess,sys
from pathlib import Path
helper={'__name__':'unified_inventory_library'}
exec(compile(base64.b64decode(CATALOG_B64),'capture_catalog.py','exec'),helper)
pq,command=helper['pq'],helper['command']
pq.PQcmdStatus.argtypes=[C.c_void_p];pq.PQcmdStatus.restype=C.c_char_p
sql=base64.b64decode(SQL_B64)
assert hashlib.sha256(sql).hexdigest().upper()=='D4FE697EDCAFD7430034E7D138083D151866B0FCEA0D3DF9CFD436E05B3E9C66'
assert REVIEW_APPROVAL=='READ_ONLY_COLUMN_ACL_STATIC_PASS'
backup=Path('/var/lib/postgresql/fugates-unified-dev-backups/fugates_dev_5433_20261027T221705Z.dump')
assert backup.is_file() and not backup.is_symlink() and backup.stat().st_mode&0o077==0
assert hashlib.sha256(backup.read_bytes()).hexdigest().upper()=='90555CC3A1DF0411ECC81F384EBABAFDBA3B28456BD73AA099BF1CC146DF5609'
assert subprocess.run(['/usr/lib/postgresql/18/bin/pg_restore','--list',str(backup)],stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL).returncode==0
c=pq.PQconnectdb(b"host=/var/run/postgresql port=5433 dbname=fugates_dev user=postgres application_name=FugaTES.UnifiedDev.ReadAcl options='-c default_transaction_read_only=on -c statement_timeout=10000 -c lock_timeout=3000'")
result={'result':'BLOCKED','commit':'NOT_APPLICABLE','businessRowsWritten':0,'hmlChanges':0,'sqlSha256':hashlib.sha256(sql).hexdigest().upper(),'backup':'PASS','review':'PASS'}
acl_sql="""select coalesce(jsonb_agg(v order by v::text),'[]') from (
 select jsonb_build_array(c.relname,a.attname,coalesce(x.grantee,0),coalesce(x.grantor,0),x.privilege_type,x.is_grantable) v
 from pg_class c join pg_attribute a on a.attrelid=c.oid and a.attnum>0 and not a.attisdropped
 cross join lateral aclexplode(a.attacl) x
 where c.oid in ('fugates.integracao'::regclass,'fugates.tentativa_integracao'::regclass,'fugates.auditoria_integracao'::regclass)
) t"""
try:
 assert pq.PQstatus(c)==0,'DEV_PEER_AUTHENTICATION_FAILED'
 identity=command(c,"select jsonb_build_object('cluster',current_setting('cluster_name'),'port',current_setting('port')::int,'database',current_database(),'role',current_user)",True)
 assert identity==dict(cluster='18/fugatesacmdev',port=5433,database='fugates_dev',role='postgres')
 before=command(c,acl_sql,True)
 table_before=command(c,"select jsonb_agg(jsonb_build_array(oid,relacl,relowner) order by oid) from pg_class where oid in ('fugates.integracao'::regclass,'fugates.tentativa_integracao'::regclass,'fugates.auditoria_integracao'::regclass)",True)
 reply=pq.PQexec(c,sql)
 try:
  if pq.PQresultStatus(reply)!=1 or pq.PQcmdStatus(reply)!=b'COMMIT':
   result['commit']='INDETERMINATE' if pq.PQstatus(c)!=0 else 'NOT_CONFIRMED'
   raise RuntimeError('ACL_TRANSACTION_FAILED_NO_RETRY')
 finally:pq.PQclear(reply)
 result['commit']='CONFIRMED'
 command(c,'BEGIN READ ONLY')
 after=command(c,acl_sql,True)
 table_after=command(c,"select jsonb_agg(jsonb_build_array(oid,relacl,relowner) order by oid) from pg_class where oid in ('fugates.integracao'::regclass,'fugates.tentativa_integracao'::regclass,'fugates.auditoria_integracao'::regclass)",True)
 assert table_before==table_after,'TABLE_ACL_OR_OWNER_CHANGED'
 old={json.dumps(v) for v in before};new={json.dumps(v) for v in after}
 assert not old-new
 added=[json.loads(v) for v in new-old]
 assert len(added)==12 and all(v[4]=='SELECT' and v[5] is False for v in added)
 command(c,'ROLLBACK')
 result.update(result='PASS',columnSelectAdded=len(added),tableAclsPreserved=True,rollback='PASS',identity=identity)
except Exception as error:
 result['error']=str(error) if str(error).startswith(('SQLSTATE_','ACL_','TABLE_','DEV_')) else 'ACL_POSTCONDITION_FAILED_NO_RETRY'
 try:command(c,'ROLLBACK')
 except Exception:pass
finally:pq.PQfinish(c)
print('UNIFIED_DEV_JSON_BEGIN');print(json.dumps(result));print('UNIFIED_DEV_JSON_END')
sys.exit(0 if result['result']=='PASS' else 2)
