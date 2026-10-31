
import json, hashlib, struct, pathlib
root=pathlib.Path(r"C:\Visual Studio 2026\Projetos\Fuga\FugaTES\FugaTES_DEV")
ev=root/".artifacts/manual-readonly-r16/evidence"
baseline=json.loads((ev/"baseline.json").read_text(encoding="utf-8-sig"))
stage=pathlib.Path(baseline["StagingPath"])
def digest(p): return hashlib.sha256(p.read_bytes()).hexdigest().upper()
def write(p,obj): p.write_text(json.dumps(obj,ensure_ascii=False,indent=2),encoding="utf-8")
def verify(items,key):
    return [{"Path":x["Path"],"Before":x[key],"After":digest(pathlib.Path(x["Path"])) if pathlib.Path(x["Path"]).is_file() else "MISSING","Match":pathlib.Path(x["Path"]).is_file() and digest(pathlib.Path(x["Path"]))==x[key]} for x in items]
protected=verify(baseline["Protected"],"After")
evidence=verify(baseline["R15Evidence"],"Hash")
sources=verify(baseline["SourceFiles"],"Hash")
if not all(x["Match"] for x in protected+evidence+sources): raise RuntimeError("PRESERVATION_MISMATCH")
exit_code=int((ev/"publish-exit-code.txt").read_text().strip())
if exit_code != 0: raise RuntimeError("PUBLISH_FAILED")
exe=stage/"FugaTES.WinForms.exe"
data=exe.read_bytes()
pe=struct.unpack_from("<I",data,0x3c)[0]
if data[:2]!=b"MZ" or data[pe:pe+4]!=b"PE\0\0" or struct.unpack_from("<H",data,pe+4)[0]!=0x8664: raise RuntimeError("WINDOWS_X64_PE_INVALID")
rt=json.loads((stage/"FugaTES.WinForms.runtimeconfig.json").read_text())
frameworks={x["name"] for x in rt["runtimeOptions"].get("includedFrameworks",[])}
if not {"Microsoft.NETCore.App","Microsoft.WindowsDesktop.App"}.issubset(frameworks): raise RuntimeError("SELF_CONTAINED_NOT_PROVEN")
for f in ["coreclr.dll","hostfxr.dll","hostpolicy.dll","System.Windows.Forms.dll","FugaTES.HostIntegracao.dll","FugaTES.Persistencia.dll"]:
    if not (stage/f).is_file(): raise RuntimeError("PUBLISH_DEPENDENCY_MISSING")
checks={
"FugaTES.WinForms.dll":["CarrierManualReadOnlyComposition","ConsultarManualReadOnlyAsync","ConsultarReadOnlyAsync"],
"FugaTES.Aplicacao.dll":["ConsultarTransportadoraManualReadOnlyCasoDeUso","ResultadoCarrierManual"],
"FugaTES.Integracao.SAP.dll":["CarrierManualSapReadOnlyAdapter","CarrierManualODataDateParser"],
"FugaTES.Integracao.Guardian.dll":["GuardianCarrierManualReadOnlyAdapter","ResolveApprovedCollectionUpdateRoute"],
"FugaTES.HostIntegracao.dll":["CarrierProtocolBinding"]}
markers=[]
for filename,names in checks.items():
    buf=(stage/filename).read_bytes()
    for name in names:
        found=name.encode()+b"\0" in buf
        markers.append({"Assembly":filename,"CompiledMetadataSymbol":name,"Present":found})
        if not found: raise RuntimeError("COMPILED_SYMBOL_MISSING")
    if filename in ["FugaTES.Integracao.Guardian.dll","FugaTES.HostIntegracao.dll"]:
        found="/api/v0/Guardian/Transportadora".encode("utf-16le") in buf
        markers.append({"Assembly":filename,"CompiledUserString":"R13_APPROVED_COLLECTION_ROUTE","Present":found})
        if not found: raise RuntimeError("R13_ROUTE_LITERAL_MISSING")
json_files=list(stage.glob("*.json"))
expected={"FugaTES.WinForms.deps.json","FugaTES.WinForms.runtimeconfig.json","FugaTES.HostIntegracao.deps.json","FugaTES.HostIntegracao.runtimeconfig.json","appsettings.Development.json"}
if {x.name for x in json_files} - expected: raise RuntimeError("UNEXPECTED_PUBLISHED_CONFIGURATION")
for f in json_files:
    j=json.loads(f.read_text(encoding="utf-8-sig"))
    if f.name=="appsettings.Development.json" and set(j)!={"Logging"}: raise RuntimeError("NON_LOGGING_APPSETTINGS_PRESENT")
for f in stage.rglob("*"):
    if f.is_file() and (f.suffix.lower() in [".pem",".key",".pfx",".p12",".env"] or f.name in ["FugaTES.runtime.json","FugaTES.carrier.json","id_rsa","id_ed25519"]):
        raise RuntimeError("PRIVATE_PROFILE_OR_SECRET_FILE_PRESENT")
payload=list(stage.iterdir())
manifest=[]
for f in sorted(payload,key=lambda p:p.name):
    if f.is_file() and f.suffix.lower() in [".dll",".exe"]:
        manifest.append({"File":f.name,"SHA256":digest(f),"Bytes":f.stat().st_size})
focal=json.loads((root/".artifacts/manual-readonly-r15/evidence/source-manifest.json").read_text(encoding="utf-8-sig"))
for rel in ["src/FugaTES.Integracao.Guardian/GuardianCarrierSyncAdapter.cs","src/FugaTES.HostIntegracao/CarrierProtocolBinding.cs"]:
    f=root/rel
    focal.append({"Path":str(f),"SHA256":digest(f),"Bytes":f.stat().st_size})
result={
"Gate":"FUGATES-MASTERDATA-MANUAL-READONLY-STAGING-R16","ReleaseIdentity":baseline["ReleaseIdentity"],"StagingPath":str(stage),
"PublishExitCode":exit_code,"ReleaseBuild":"PASS","WindowsExecutable":"PE_AMD64","SelfContained":"PASS",
"R15CodeIncluded":True,"R13PatchIncluded":True,"CompiledInclusionEvidence":markers,
"AssemblyCount":len(manifest),"PrivateProfilesCopied":False,"ConfigurationInventory":[f.name for f in json_files],
"SourceChange":0,"SourceFilesUnchanged":len(sources),"R15EvidenceFilesUnchanged":len(evidence),"ProtectedFilesUnchanged":len(protected),
"StagingRuntimeReady":False,"WinFormsStarted":0,"WorkersStarted":0,"RealHttpCalls":0,"DatabaseConnections":0}
write(ev/"integrity-validation.json",result)
write(ev/"preservation.json",{"Protected":protected,"R15Evidence":evidence,"Source":sources})
write(ev/"assembly-manifest.json",manifest)
write(ev/"source-manifest-r13-r15.json",focal)
write(stage/"ASSEMBLY_MANIFEST.json",manifest)
write(stage/"SOURCE_MANIFEST_R13_R15.json",focal)
write(stage/"INTEGRITY_VALIDATION.json",result)
pending="""# Preflight pendente — R16
Binários publicados; STAGING_RUNTIME_READY=NAO. Não iniciar antes do preflight autorizado.
Nenhum perfil privado, credencial, chave SSH ou connection string foi copiado/criado.
FugaTES.runtime.json: identidade HML, role PostgreSQL, target Credential Manager e configuração SSH devem ser comprovados separadamente.
FugaTES.carrier.json: identidade HML SAP/Guardian, referências de segredos, autorização de leitura BP/SID/prazo, contrato GET, código Guardian e idAssinatura precisam ser comprovados.
ProfileVerified, SignatureVerified e DesktopProfileApproved não foram alterados nem transportados.
Para a consulta manual, o perfil futuro precisa manter explicitamente FALSE:
- FUGATES_INTEGRATION_WORKER_ENABLED
- INC005_WORKER_ENABLED
- Carrier:IntegrationWorkerEnabled
- Carrier:DesktopGuardianWritesEnabled
- Carrier:Authorization:IncrementalReleased
A ausência de perfil deixa o startup/consulta bloqueados; nenhum worker foi habilitado.
Não configurar delta fictício. O manual não depende do contrato delta.
Comparação completa ainda não comprovada: igualdade parcial permanece INDETERMINATE.
Executável do projeto: FugaTES.WinForms.exe. Mantido o nome real do apphost; não é o desktop oficial.
Não foi executado QA visual, HTTP, PostgreSQL ou worker. Não há nova instalação.
"""
(stage/"PREFLIGHT_PENDING.md").write_text(pending,encoding="utf-8")
(stage/"RELEASE_R16.md").write_text("# "+baseline["ReleaseIdentity"]+"\nPublicação Release win-x64 self-contained do projeto WinForms real.\nR15 e R13.1 incluídos; runtime pendente; nenhum aplicativo iniciado.\nConsultar ASSEMBLY_MANIFEST.json, SOURCE_MANIFEST_R13_R15.json, INTEGRITY_VALIDATION.json e PREFLIGHT_PENDING.md.\n",encoding="utf-8")
package=[{"File":f.name,"SHA256":digest(f),"Bytes":f.stat().st_size} for f in sorted(stage.iterdir(),key=lambda p:p.name) if f.is_file()]
write(stage/"PACKAGE_MANIFEST.json",package)
write(ev/"package-manifest.json",package)
print(json.dumps({k:result[k] for k in ["ReleaseIdentity","StagingPath","PublishExitCode","ReleaseBuild","WindowsExecutable","SelfContained","R15CodeIncluded","R13PatchIncluded","AssemblyCount","SourceFilesUnchanged","R15EvidenceFilesUnchanged","ProtectedFilesUnchanged","StagingRuntimeReady"]},ensure_ascii=False))
