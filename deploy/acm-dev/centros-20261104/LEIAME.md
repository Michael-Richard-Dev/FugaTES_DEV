# ACM nos dois centros — 3011 e 3008 — 04/11/2026

Serviço `fugates-acm-motor-dev` (motor contínuo R170).

## O que estava errado

Os contratos `0000001207` e `0000001208`, do centro **3008**, chegaram do SAP, viraram integração e
ficaram `PENDENTE / NAO_INICIADO` **para sempre**. Não houve erro, recusa nem linha de log: o recorte
do motor comparava o centro do contrato com um único centro configurado (`3011`), e contrato de outro
centro simplesmente não era trabalho dele. Quem não é trabalho do motor não gera recusa — daí o
silêncio.

E não era questão de trocar o valor no `.env`: o `3011` estava **escrito no código**, de forma
independente, em cerca de vinte pontos do caminho do ACM. Configurar `Centro=3008` não liberaria o
3008 — invalidaria o recorte inteiro e o motor pararia de cadastrar qualquer contrato.

## O que mudou no código

Uma **lista única** de centros do ACM, em `FugaTES.Aplicacao/Configuracao/CentrosAcm.cs`:

```csharp
public static readonly string[] Permitidos = ["3011", "3008"];
```

Ela substituiu os literais `"3011"` em cada etapa: recorte de admissão (`AcmAutomaticoOptions`,
`AcmInboundWindowOptions`), resolução de emissor e material (`AcmR57NormalPreparation`,
`AcmGuardianMaterialResolver`), mapeador do Guardian (`MapeadorContratoAcmGuardian`, `AcmR57Mapping`),
cliente HTTP do Guardian (`GuardianSet26HttpClient`), preflight da devolutiva
(`AcmR106SapReturnPreflight`), identidade do callback (`ProcessarCallbackGuardianMvpCasoDeUso`,
`EvidenciaPesagemGuardianSnapshot`, `AcmGuardianTicketEvidenceAdapterR164A`), motor de pesagem
(`ProcessadorPesagemSapMotor`), gateway do SAP (`PesagemSapHttpGateway`) e verificação de identidade
da retentativa (`AcmManualRetryPostgreSql`).

Existe uma lista só por um motivo prático: esquecer um literal não impede a entrada, **move o
bloqueio para o meio do fluxo** — o pior lugar para ele acontecer.

O recorte contínuo passou a aceitar uma **lista** de centros (`AcmAutomatico__Centros`), e não um
só. Precisava ser lista porque `MotorIntegracaoOptions` recusa dois recortes ativos em paralelo:
atender 3011 e 3008 tinha de caber **dentro** de um recorte, não ao lado dele. A chave antiga
`AcmAutomatico__Centro` continua válida e continua restringindo — quem declara um centro não passa a
receber o outro de brinde.

### O campo adicional 1002

Já levava o centro do próprio contrato (`Snapshot.Centro`) e continua levando: contrato de 3011 vai
com `1002 = 3011`, contrato de 3008 vai com `1002 = 3008`. Os mapeadores sempre copiaram o centro do
snapshot. O que faltava era o contrato de 3008 **chegar** até eles.

Um ponto precisou de correção aqui: o cliente HTTP do Guardian validava `1002 == "3011"` em todo
pré-cadastro com transportadora preenchida no Guardian — não era uma regra do contrato histórico
1106, como parecia à primeira vista. Barraria 3008 na porta de saída.

### O que NÃO mudou, de propósito

- **Fail-closed dos identificadores.** Abrir o centro não abre os códigos. O motor resolve emissor
  e item de uma de duas formas, e o `.env` decide qual:

  1. **validação dinâmica** — `DynamicEmitterValidationEnabled` e `DynamicMaterialValidationEnabled`
     ligados, que é o caso na VM desde 31/10 e 01/11: o candidato é o código SAP sem os zeros à
     esquerda, e é **provado** com um GET exato no Guardian, sob a assinatura configurada. Não há
     tabela a preencher — é o Guardian que confirma ou nega. Para 3008 isso dá `20000236` e
     `7000001`, derivados dos contratos 1207/1208.
  2. **tabela de mapeamento por centro** — exige um par SAP → Guardian para cada identificador.

  Nas duas, identificador não confirmado bloqueia o contrato **antes** do envio e ele fica visível
  no FugaTES. O script roda sem argumentos quando a validação dinâmica está ligada nos dois lados, e
  **se recusa a rodar** sem os pares quando não está — porque aí o contrato passaria o recorte só
  para travar mais adiante.
- **O par tipo + pesagem.** Continua fechado: compra `AC3P/E` e venda `AC3S/S`, nos dois centros.
- **O caminho piloto / retentativa manual** (`AcmPilotCentro`, `AcmManualRetryCommandHost`) segue
  fixo em 3011. É ferramenta operacional ligada a um contrato único e não faz parte do cadastro
  automático; se um dia precisar alcançar 3008, é mudança separada.

## Testes

19 asserções novas em `tests/FugaTES.Testes/AcmCentrosTests.cs`, incluindo o caminho **feliz** dos
dois centros — não só a recusa. O caso 1207/1208 falhou em silêncio justamente porque um teste de
"centro errado é recusado" não pega esse tipo de defeito.

Cinco testes que fixavam a regra antiga foram atualizados: eles usavam `3008` como exemplo de
"centro não aprovado", o que deixou de ser verdade. O centro de fora agora é `3020`, e cada teste
mantém exatamente a intenção original.

Suíte completa: nada quebrou. As 13 falhas restantes são anteriores a esta mudança — 8 em
`R165U.Automatic.Tests` exigem a pasta `evidence/`, que não existe neste checkout; 2 em
`R165R.Provider.Tests` exigem a variável `R165R_PACKAGE_ROOT`; 3 em `FugaTES.Testes` apontam para o
trabalho de MM e do painel.

## A unit tem drop-ins, e o arquivo principal não manda

Isto custou duas tentativas e vale registrar, porque qualquer publicação futura neste serviço cai na
mesma armadilha:

```
/etc/systemd/system/fugates-acm-motor-dev.service          → ignorado
  .service.d/90-R171_DYNAMIC_EMITTER_R2_3280DADA2807.conf  → perde
  .service.d/zzzzzzzzzz-DYNAMIC_MATERIAL_BF2D5358B21F.conf → VENCE
```

O `atualizar-motor-r170.sh` edita o **arquivo principal** da unit e anuncia "motor ATIVO em
`<release>`". O anúncio é verdadeiro sobre o arquivo que ele editou e falso sobre a realidade: o
último drop-in redefine `WorkingDirectory` e `ExecStart`, e é ele quem decide o binário. Dez "z" no
nome é o que uma corrida de precedência por nome produz.

Há uma segunda consequência, menos visível: `EnvironmentFile` **acumula**, e o último vence por
chave. Os drop-ins acrescentam dois arquivos de env **depois** do `r57.env`. Escrever o recorte no
`r57.env` deixaria a escrita à mercê deles — neste momento funcionaria, porque nenhum dos dois
declara o centro, mas por sorte, não por desenho.

Por isso o `publicar-motor-acm.sh`:

- **descobre** o binding canônico (o drop-in de maior precedência que define `ExecStart`) em vez de
  fixar o nome, que é histórico e mente;
- troca **apenas o caminho do release** dentro dele, preservando o resto do arquivo;
- escreve o recorte num `centros.env` ligado como **último** `EnvironmentFile` desse drop-in, para
  vencer por chave. A chave antiga `AcmAutomatico__Centro` pode ficar onde está: no código,
  `Centros` tem precedência sobre `Centro`;
- **não cria drop-in novo** — entrar na corrida de nomes é o que gerou esta unit;
- confere o **efeito**, não o arquivo: o `ExecStart` que o systemd resolveu e as marcas no binário
  que está de fato em execução. Falhando qualquer uma, restaura o binding e volta o motor.

## Como publicar

No Windows, nesta pasta:

```powershell
.\Enviar-MotorAcm.ps1
```

Na VM:

```bash
rm -rf ~/r172 && mkdir ~/r172 && tar -xzf ~/fugates-motor-acm.tgz -C ~/r172
```

```bash
sudo bash ~/publicar-motor-acm.sh conferir
```

```bash
sudo bash ~/publicar-motor-acm.sh instalar
```

```bash
sudo bash ~/publicar-motor-acm.sh reverter
```

O `conferir` não altera nada: mostra o binding canônico, a ordem dos `EnvironmentFile`, onde o
recorte está declarado hoje, as marcas do binário em execução e as do pacote, e confere que o
pacote é superconjunto do release em uso.

Esse último ponto também custou uma tentativa: o release em uso foi publicado **sem** RID e tem
`FugaTES.HostIntegracao.exe` e `runtimes/`. Publicar com `-r linux-x64` gera o apphost nativo e
inlina os assets, e o guarda de "nada do release em uso pode faltar" barrou — corretamente. O pacote
agora é publicado sem RID.

## Uma ressalva sobre 1207 e 1208

Esses dois foram cadastrados **à mão**, pelo Postman. Cadastro feito fora do FugaTES não deixa
vínculo durável aqui — e é esse vínculo que a devolutiva da pesagem exige para fechar o ciclo de
volta ao SAP. Com o binário novo, uma pesagem de contrato 3008 passa a seguir o caminho do ACM, que
pede o vínculo; sem ele, a pesagem fica visível e bloqueada, não enviada.

Ou seja: 1207 e 1208 estão no Guardian e servem para o pessoal operar, mas **não** servem para provar
o ciclo completo. Quem prova é o próximo contrato de 3008 que o SAP entregar e o motor cadastrar
sozinho.
