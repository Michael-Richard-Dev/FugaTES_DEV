"""Authorized metadata/contract-lineage read. No files, schema or business writes."""
import ctypes as C
import ctypes.util
import json
import pwd
import re
import socket
import sys

assert socket.gethostname() == 'vm-fugatesbio', 'HOST_IDENTITY_REJECTED'
assert pwd.getpwuid(__import__('os').geteuid()).pw_name == 'postgres', 'ADMIN_PEER_REQUIRED'
pq = C.CDLL(ctypes.util.find_library('pq') or 'libpq.so.5')
for name,args,result in [
 ('PQconnectdb',[C.c_char_p],C.c_void_p),('PQstatus',[C.c_void_p],C.c_int),
 ('PQexec',[C.c_void_p,C.c_char_p],C.c_void_p),('PQresultStatus',[C.c_void_p],C.c_int),
 ('PQntuples',[C.c_void_p],C.c_int),('PQgetvalue',[C.c_void_p,C.c_int,C.c_int],C.c_char_p),
 ('PQresultErrorField',[C.c_void_p,C.c_int],C.c_char_p),('PQclear',[C.c_void_p],None),('PQfinish',[C.c_void_p],None)]:
    fn=getattr(pq,name);fn.argtypes=args;fn.restype=result

QUERIES = {
 'schemas': """select coalesce(jsonb_agg(jsonb_build_object('name',nspname,'owner',pg_get_userbyid(nspowner),'acl',nspacl) order by nspname),'[]') from pg_namespace where nspname !~ '^pg_' and nspname<>'information_schema'""",
 'relations': """select coalesce(jsonb_agg(jsonb_build_object('oid',c.oid,'schema',n.nspname,'name',c.relname,'kind',c.relkind,'owner',pg_get_userbyid(c.relowner),'acl',c.relacl,'rls',c.relrowsecurity,'forceRls',c.relforcerowsecurity) order by n.nspname,c.relname),'[]') from pg_class c join pg_namespace n on n.oid=c.relnamespace where n.nspname !~ '^pg_' and n.nspname<>'information_schema' and c.relkind in ('r','p','v','m','S','f')""",
 'columns': """select coalesce(jsonb_agg(jsonb_build_object('schema',n.nspname,'table',c.relname,'ordinal',a.attnum,'name',a.attname,'type',format_type(a.atttypid,a.atttypmod),'notNull',a.attnotnull,'identity',a.attidentity,'generated',a.attgenerated,'default',case when a.attname ~* '(password|secret|token|api_key)' then '[REDACTED]' else pg_get_expr(d.adbin,d.adrelid) end,'acl',a.attacl) order by n.nspname,c.relname,a.attnum),'[]') from pg_attribute a join pg_class c on c.oid=a.attrelid join pg_namespace n on n.oid=c.relnamespace left join pg_attrdef d on d.adrelid=c.oid and d.adnum=a.attnum where n.nspname !~ '^pg_' and n.nspname<>'information_schema' and c.relkind in ('r','p','v','m','f') and a.attnum>0 and not a.attisdropped""",
 'constraints': """select coalesce(jsonb_agg(jsonb_build_object('schema',n.nspname,'table',c.relname,'name',k.conname,'type',k.contype,'definition',pg_get_constraintdef(k.oid,true),'validated',k.convalidated,'enforced',k.conenforced,'deferrable',k.condeferrable,'deferred',k.condeferred,'noInherit',k.connoinherit,'parent',k.conparentid) order by n.nspname,c.relname,k.conname),'[]') from pg_constraint k join pg_class c on c.oid=k.conrelid join pg_namespace n on n.oid=c.relnamespace where n.nspname !~ '^pg_' and n.nspname<>'information_schema'""",
 'indexes': """select coalesce(jsonb_agg(jsonb_build_object('schema',n.nspname,'table',c.relname,'name',i.relname,'definition',pg_get_indexdef(x.indexrelid),'unique',x.indisunique,'primary',x.indisprimary,'valid',x.indisvalid,'ready',x.indisready,'predicate',pg_get_expr(x.indpred,x.indrelid)) order by n.nspname,c.relname,i.relname),'[]') from pg_index x join pg_class c on c.oid=x.indrelid join pg_class i on i.oid=x.indexrelid join pg_namespace n on n.oid=c.relnamespace where n.nspname !~ '^pg_' and n.nspname<>'information_schema'""",
 'triggers': """select coalesce(jsonb_agg(jsonb_build_object('schema',n.nspname,'table',c.relname,'name',t.tgname,'enabled',t.tgenabled,'internal',t.tgisinternal,'functionOid',t.tgfoid,'definition',case when not t.tgisinternal then pg_get_triggerdef(t.oid,true) end) order by n.nspname,c.relname,t.tgname),'[]') from pg_trigger t join pg_class c on c.oid=t.tgrelid join pg_namespace n on n.oid=c.relnamespace where n.nspname !~ '^pg_' and n.nspname<>'information_schema'""",
 'functions': """select coalesce(jsonb_agg(jsonb_build_object('oid',p.oid,'schema',n.nspname,'name',p.proname,'arguments',pg_get_function_identity_arguments(p.oid),'returns',pg_get_function_result(p.oid),'kind',p.prokind,'owner',pg_get_userbyid(p.proowner),'acl',p.proacl,'securityDefiner',p.prosecdef,'volatility',p.provolatile,'definitionMd5',case when p.prokind in ('f','p') then md5(pg_get_functiondef(p.oid)) end) order by n.nspname,p.proname,p.oid),'[]') from pg_proc p join pg_namespace n on n.oid=p.pronamespace where n.nspname !~ '^pg_' and n.nspname<>'information_schema'""",
 'sequences': """select coalesce(jsonb_agg(jsonb_build_object('schema',n.nspname,'name',c.relname,'owner',pg_get_userbyid(c.relowner),'acl',c.relacl,'type',format_type(s.seqtypid,null),'start',s.seqstart,'increment',s.seqincrement,'min',s.seqmin,'max',s.seqmax,'cache',s.seqcache,'cycle',s.seqcycle) order by n.nspname,c.relname),'[]') from pg_sequence s join pg_class c on c.oid=s.seqrelid join pg_namespace n on n.oid=c.relnamespace where n.nspname !~ '^pg_' and n.nspname<>'information_schema'""",
 'roles': """select coalesce(jsonb_agg(jsonb_build_object('name',rolname,'login',rolcanlogin,'superuser',rolsuper,'createDb',rolcreatedb,'createRole',rolcreaterole,'replication',rolreplication,'bypassRls',rolbypassrls,'inherit',rolinherit) order by rolname),'[]') from pg_roles where rolname like 'fugates%' or rolname='postgres'""",
 'memberships': """select coalesce(jsonb_agg(jsonb_build_object('role',r.rolname,'member',m.rolname,'grantor',g.rolname,'admin',a.admin_option,'inherit',a.inherit_option,'set',a.set_option) order by r.rolname,m.rolname),'[]') from pg_auth_members a join pg_roles r on r.oid=a.roleid join pg_roles m on m.oid=a.member join pg_roles g on g.oid=a.grantor where r.rolname like 'fugates%' or m.rolname like 'fugates%'""",
 'defaultPrivileges': """select coalesce(jsonb_agg(jsonb_build_object('owner',pg_get_userbyid(d.defaclrole),'schema',n.nspname,'objectType',d.defaclobjtype,'acl',d.defaclacl) order by d.defaclrole,d.defaclnamespace,d.defaclobjtype),'[]') from pg_default_acl d left join pg_namespace n on n.oid=d.defaclnamespace where pg_get_userbyid(d.defaclrole) like 'fugates%' or n.nspname !~ '^pg_'""",
 'effectiveTablePrivileges': """select coalesce(jsonb_agg(jsonb_build_object('role',r.rolname,'schema',n.nspname,'table',c.relname,'select',has_table_privilege(r.oid,c.oid,'SELECT'),'insert',has_table_privilege(r.oid,c.oid,'INSERT'),'update',has_table_privilege(r.oid,c.oid,'UPDATE'),'delete',has_table_privilege(r.oid,c.oid,'DELETE'),'truncate',has_table_privilege(r.oid,c.oid,'TRUNCATE'),'references',has_table_privilege(r.oid,c.oid,'REFERENCES'),'trigger',has_table_privilege(r.oid,c.oid,'TRIGGER')) order by r.rolname,n.nspname,c.relname),'[]') from pg_roles r cross join pg_class c join pg_namespace n on n.oid=c.relnamespace where r.rolname like 'fugates%' and n.nspname !~ '^pg_' and n.nspname<>'information_schema' and c.relkind in ('r','p','v','m')""",
 'activeSessions': """select coalesce(jsonb_agg(jsonb_build_object('role',usename,'application',application_name,'state',state,'transactionStart',xact_start,'waitType',wait_event_type) order by pid),'[]') from pg_stat_activity where datname=current_database() and pid<>pg_backend_pid()"""
}

def command(connection,sql,tuples=False):
    result=pq.PQexec(connection,sql.encode('utf-8'))
    try:
        if pq.PQresultStatus(result)!=(2 if tuples else 1):
            state=pq.PQresultErrorField(result,ord('C'))
            raise RuntimeError('SQLSTATE_'+(state.decode() if state else 'UNKNOWN'))
        if tuples:return json.loads(pq.PQgetvalue(result,0,0).decode('utf-8'))
    finally:pq.PQclear(result)

def capture(port,db,cluster):
    connection=pq.PQconnectdb(("host=/var/run/postgresql port=%s dbname=%s user=postgres application_name=FugaTES.UnifiedDev.Inventory options='-c default_transaction_read_only=on -c statement_timeout=10000 -c lock_timeout=1000'"%(port,db)).encode())
    result={'port':port,'database':db,'transaction':'NOT_OPENED','rollback':'NOT_APPLICABLE'}
    opened=False
    try:
        if pq.PQstatus(connection)!=0:raise RuntimeError('PEER_CONNECTION_FAILED')
        command(connection,'BEGIN READ ONLY');opened=True
        identity=command(connection,"select jsonb_build_object('database',current_database(),'sessionUser',session_user,'effectiveUser',current_user,'cluster',current_setting('cluster_name'),'port',current_setting('port')::int,'serverVersion',current_setting('server_version_num')::int,'readOnly',current_setting('transaction_read_only'),'recovery',pg_is_in_recovery())",True)
        result['identity']=identity
        assert identity['database']==db and identity['sessionUser']=='postgres' and identity['effectiveUser']=='postgres'
        assert identity['cluster']==cluster and identity['port']==port and 180000<=identity['serverVersion']<190000
        assert identity['readOnly']=='on' and not identity['recovery']
        result['transaction']='READ_ONLY'
        for section,query in QUERIES.items():
            result[section]=command(connection,query,True)
        h4=any(r['schema']=='fugates' and r['name']=='contrato_sap_toledo_versao' for r in result['relations'])
        if h4:
            columns={c['name'] for c in result['columns'] if c['schema']=='fugates' and c['table']=='contrato_sap_toledo_versao'}
            assert {'numero_contrato','versao','integracao_id'}.issubset(columns)
            result['contractCounts']=command(connection,"select jsonb_build_object('h4Versions',count(*),'distinctContracts',count(distinct numero_contrato),'knownSyntheticVersions',count(*) filter(where numero_contrato='0000986801'),'otherVersionsOriginNotYetAttested',count(*) filter(where numero_contrato<>'0000986801')) from fugates.contrato_sap_toledo_versao",True)
            result['contractIdentities']=command(connection,"select coalesce(jsonb_agg(to_jsonb(v)),'[]') from (select numero_contrato,versao,integracao_id from fugates.contrato_sap_toledo_versao order by numero_contrato,versao limit 50) v",True)
        else:result['contractCounts']={'h4Versions':None,'reason':'H4_TABLE_ABSENT'}
        result['result']='PASS'
    except Exception as error:
        result['result']='BLOCKED';result['error']=str(error) if str(error).startswith(('SQLSTATE_','PEER_')) else 'IDENTITY_OR_CONTRACT_REJECTED'
    finally:
        if opened:
            try:command(connection,'ROLLBACK');result['rollback']='PASS'
            except Exception:result['rollback']='UNVERIFIED'
        pq.PQfinish(connection)
    return result

if __name__=='__main__':
    results=[]
    for port,db,cluster in [(5433,'fugates_dev','18/fugatesacmdev'),(5434,'fugates_masterdata_hml','18/fugatesmdhml'),(5432,'fugates_dev','18/main')]:
        result=capture(port,db,cluster);results.append(result)
        if result['result']!='PASS':break
    print('UNIFIED_DEV_JSON_BEGIN')
    print(json.dumps({'host':socket.gethostname(),'catalogs':results,'databaseWrites':0,'businessPayloadsExported':0},ensure_ascii=True))
    print('UNIFIED_DEV_JSON_END')
    sys.exit(0 if len(results)==3 and all(r['result']=='PASS' for r in results) else 2)
