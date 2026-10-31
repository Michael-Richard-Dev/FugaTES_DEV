"""Authorized target-only backup + reviewed additive DDL. Payloads injected by local launcher."""
import base64,ctypes as C,hashlib,json,os,subprocess,sys
from pathlib import Path
from datetime import datetime,timezone

helper={'__name__':'unified_inventory_library'}
exec(compile(base64.b64decode(CATALOG_B64),'capture_catalog.py','exec'),helper)
pq,command,capture=helper['pq'],helper['command'],helper['capture']
pq.PQcmdStatus.argtypes=[C.c_void_p];pq.PQcmdStatus.restype=C.c_char_p
pq.PQtransactionStatus.argtypes=[C.c_void_p];pq.PQtransactionStatus.restype=C.c_int
migration=base64.b64decode(MIGRATION_B64);postcheck=base64.b64decode(POSTCHECK_B64)
assert hashlib.sha256(migration).hexdigest().upper()==MIGRATION_SHA
assert hashlib.sha256(postcheck).hexdigest().upper()==POSTCHECK_SHA
assert REVIEW_APPROVAL=='STATIC_REVIEW_ACL_R2_PASS'
tables=['auditoria_integracao','contrato_sap_toledo_identidade','contrato_sap_toledo_versao',
        'correlacao_guardian_evento','correlacao_guardian_sap','integracao','pausa_processamento',
        'projecao_resultado_fluxo_mvp','tentativa_integracao']
result={'gate':'FUGATES-UNIFIED-POSTGRESQL-DEV','migration':'NOT_EXECUTED','commit':'NOT_APPLICABLE',
        'backup':'NOT_EXECUTED','sourceHmlWrites':0,'businessRowsCopied':0,'newLoginRoles':0,
        'migrationSha256':MIGRATION_SHA,'postcheckSha256':POSTCHECK_SHA}
guard=None;ddl=None
def connect():
    c=pq.PQconnectdb(b"host=/var/run/postgresql port=5433 dbname=fugates_dev user=postgres application_name=FugaTES.UnifiedDev.Schema options='-c default_transaction_read_only=on -c statement_timeout=60000 -c lock_timeout=3000'")
    if pq.PQstatus(c)!=0:pq.PQfinish(c);raise RuntimeError('TARGET_PEER_AUTHENTICATION_FAILED')
    identity=command(c,"select jsonb_build_object('cluster',current_setting('cluster_name'),'port',current_setting('port')::int,'db',current_database(),'user',current_user,'sessionUser',session_user,'version',current_setting('server_version_num')::int,'recovery',pg_is_in_recovery())",True)
    assert identity==dict(cluster='18/fugatesacmdev',port=5433,db='fugates_dev',user='postgres',sessionUser='postgres',version=identity['version'],recovery=False)
    assert 180000<=identity['version']<190000
    result['targetIdentity']=identity
    return c
def digest(c):
    values={}
    for table in tables:
        values[table]=command(c,"select jsonb_build_object('count',count(*),'md5',md5(coalesce(string_agg(md5(to_jsonb(t)::text),'' order by to_jsonb(t)::text),''))) from fugates."+table+' t',True)
    return values
def structural(catalog):
    keys=['schemas','relations','columns','constraints','indexes','triggers','functions','sequences','roles','memberships','defaultPrivileges','effectiveTablePrivileges']
    return hashlib.sha256(json.dumps({k:catalog[k] for k in keys},sort_keys=True,separators=(',',':')).encode()).hexdigest().upper()
try:
    sources=[capture(5434,'fugates_masterdata_hml','18/fugatesmdhml'),capture(5432,'fugates_dev','18/main')]
    assert all(c['result']=='PASS' and c['rollback']=='PASS' for c in sources)
    result['sourceStructureBefore']={str(c['port']):structural(c) for c in sources}
    guard=connect()
    command(guard,'BEGIN READ WRITE')
    command(guard,"SET LOCAL lock_timeout='3s'; SET LOCAL statement_timeout='60s'; SET LOCAL TIME ZONE 'UTC'")
    # SHARE permits readers and pg_dump but excludes every application DML writer on existing ACM data.
    command(guard,'LOCK TABLE '+','.join('fugates.'+t for t in tables)+' IN SHARE MODE')
    result['writerExclusion']='TRANSACTIONAL_SHARE_LOCKS_ACQUIRED'
    before=digest(guard)
    directory=Path('/var/lib/postgresql/fugates-unified-dev-backups')
    directory.mkdir(mode=0o700,exist_ok=True)
    assert directory.is_dir() and not directory.is_symlink()
    assert directory.stat().st_uid==os.geteuid() and directory.stat().st_mode&0o077==0
    backup=directory/('fugates_dev_5433_'+datetime.now(timezone.utc).strftime('%Y%m%dT%H%M%SZ')+'.dump')
    assert not backup.exists();os.umask(0o077)
    env=dict(os.environ);env.pop('PGPASSWORD',None);env['PGPASSFILE']='/dev/null';env['PGOPTIONS']='-c default_transaction_read_only=on -c lock_timeout=3000 -c statement_timeout=60000'
    tool='/usr/lib/postgresql/18/bin/'
    dump=subprocess.run([tool+'pg_dump','--no-password','-h','/var/run/postgresql','-p','5433','-U','postgres','-d','fugates_dev','-Fc','-f',str(backup)],env=env,stdout=subprocess.DEVNULL,stderr=subprocess.PIPE)
    if dump.returncode:raise RuntimeError('TARGET_BACKUP_FAILED')
    os.chmod(backup,0o600)
    toc=subprocess.run([tool+'pg_restore','--list',str(backup)],stdout=subprocess.PIPE,stderr=subprocess.PIPE)
    full=subprocess.run([tool+'pg_restore','--file=/dev/null',str(backup)],stdout=subprocess.DEVNULL,stderr=subprocess.PIPE)
    assert toc.returncode==0 and full.returncode==0 and b'TABLE DATA fugates integracao' in toc.stdout
    result.update(backup='PASS',backupPath=str(backup),backupSha256=hashlib.sha256(backup.read_bytes()).hexdigest().upper(),
        backupToc='PASS',backupFullArchiveRead='PASS',backupPermissions='0600_POSTGRES_PRIVATE',restoreExecution='NOT_EXECUTED_NO_OPERATIONAL_RESTORE',
        historicRestoreReference='R161J_20261022_RESTORE_PASS_NOT_RECREATED')
    assert digest(guard)==before
    ddl=connect();result['migration']='IN_PROGRESS'
    reply=pq.PQexec(ddl,migration)
    try:
        if pq.PQresultStatus(reply)!=1 or pq.PQcmdStatus(reply)!=b'COMMIT':
            state=pq.PQresultErrorField(reply,ord('C'))
            result['migration']='INDETERMINATE' if pq.PQstatus(ddl)!=0 else 'FAILED'
            result['commit']='INDETERMINATE' if result['migration']=='INDETERMINATE' else 'NOT_COMMITTED'
            raise RuntimeError('MIGRATION_SQLSTATE_'+(state.decode() if state else 'UNKNOWN'))
    finally:pq.PQclear(reply)
    result.update(migration='PASS',commit='CONFIRMED')
    command(ddl,postcheck.decode())
    result['postcheck']='PASS'
    assert digest(guard)==before
    result['acmRowsPreserved']='PASS';result['acmTableCounts']={k:v['count'] for k,v in before.items()}
    command(guard,'ROLLBACK');pq.PQfinish(guard);guard=None
    after=[capture(5434,'fugates_masterdata_hml','18/fugatesmdhml'),capture(5432,'fugates_dev','18/main')]
    assert all(c['result']=='PASS' and c['rollback']=='PASS' for c in after)
    assert result['sourceStructureBefore']=={str(c['port']):structural(c) for c in after}
    result['sourceHmlStructurePreserved']='PASS'
    result['targetCatalogAfter']=capture(5433,'fugates_dev','18/fugatesacmdev')
    assert result['targetCatalogAfter']['result']=='PASS'
    result['result']='PASS'
except Exception as error:
    result['result']='BLOCKED'
    result['error']=str(error) if str(error).startswith(('SQLSTATE_','MIGRATION_SQLSTATE_','TARGET_')) else 'PRECONDITION_OR_POSTCHECK_REJECTED'
finally:
    for c in [ddl,guard]:
        if c:
            try:
                if pq.PQstatus(c)==0 and pq.PQtransactionStatus(c)!=0:command(c,'ROLLBACK')
            except Exception:result['finalRollback']='UNVERIFIED'
            pq.PQfinish(c)
print('UNIFIED_DEV_JSON_BEGIN');print(json.dumps(result,ensure_ascii=True));print('UNIFIED_DEV_JSON_END')
sys.exit(0 if result.get('result')=='PASS' else 2)
