"""Legitimate local private-API read consumer; exports metadata, never credentials or contract payloads."""
import json,urllib.request,urllib.error
from pathlib import Path
key=Path('/home/michael/.fugates-r165al-physical-R2/run/READ_API_KEY').read_text().strip()
assert len(key)==64
client=urllib.request.build_opener(urllib.request.ProxyHandler({}))
result={'databaseWrites':0,'businessPosts':0,'payloadExported':False,'reads':{}}
for resource in ['identity','contracts','monitor','masterdata','protocol?selector=e502b0a0-57c5-49f3-976c-9e1fe7e355e3','detail?selector=e502b0a0-57c5-49f3-976c-9e1fe7e355e3']:
 try:
  request=urllib.request.Request('http://127.0.0.1:18762/dev/read/'+resource,headers={'X-R165AL-Read-Key':key},method='GET')
  with client.open(request,timeout=12) as response:
   data=json.load(response)
  if resource=='identity':safe=data
  elif resource=='contracts':
   rows=data['data'];safe={'identity':data['identity'],'count':len(rows),'rows':[{'contract':r['numeroContrato'],'item':r.get('item'),'integrationId':r['integracaoId'],'version':r['versao'],'origin':r['fonteDados'],'jsonValid':isinstance(json.loads(r['payloadContratoJson']),dict)} for r in rows]}
  elif resource=='monitor':safe={'identity':data['identity'],'count':len(data['data']['integracoes'])}
  elif resource=='masterdata':safe={'identity':data['identity'],'schema':data['data']['schema'],'total':data['data']['total']}
  elif resource.startswith('protocol'):safe={'identity':data['identity'],'knownSynthetic':data['data']['knownSynthetic'],'contract':data['data']['acm']['contrato'],'protocol':data['data']['acm']['protocolo']}
  else:safe={'identity':data['identity'],'readSucceeded':True}
  result['reads'][resource]={'status':200,'result':safe}
 except urllib.error.HTTPError as error:
  body=json.loads(error.read());result['reads'][resource]={'status':error.code,'code':body.get('code','HTTP_READ_FAILED')}
 except Exception:result['reads'][resource]={'status':'BLOCKED','code':'READ_METADATA_VALIDATION_FAILED'}
print(json.dumps(result))
