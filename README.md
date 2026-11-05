# SIMPALA — Fale Conosco API

API independente dos sites e do SimDesk, em ASP.NET Core / .NET 10.

## Estado desta entrega

**Somente simulação local. Nenhum e-mail é transmitido.**

Implementados: controllers, validação no servidor, CPF/CNPJ numérico e alfanumérico,
protocolo persistente, idempotência transacional, desafio assinado de uso único,
limitação por IP, geração do texto e testes HTTP reais contra Kestrel.

O banco guarda apenas empresa, chave de idempotência, hash do conteúdo, protocolo,
data e estado. Nome, CPF/CNPJ, e-mail, telefone, mensagem e corpo gerado não são
persistidos. O hash continua sendo informação pseudonimizada e deve ter acesso restrito.

Não implementados nesta etapa: transporte Graph, fila de envio, transições de entrega,
reconciliação com Exchange, integração dos sites e ativação pública. Os estados
`Pending`, `Sending`, `Sent`, `Failed` e `Unknown` estão definidos, mas as simulações
permanecem `Pending` e são marcadas `Simulation=1`. Não devem ser enviadas futuramente.

Configurar `EmailSettings:EnableSending=true` causa falha de inicialização.
Os endpoints de contato só respondem a conexões de loopback em Development/HML.
Em Production não coletam solicitações. O `/health` informa explicitamente modo de simulação.

## Executar

Requisito: SDK .NET 10.

```powershell
dotnet restore Simpala.Site.slnx
dotnet build Simpala.Site.slnx
dotnet run --project src/Simpala.Site.Api --launch-profile http
```

API local: `http://localhost:5099`. HTTP é permitido somente em loopback/Development.
Para HTTPS local, configure o certificado de desenvolvimento confiável e use
`--launch-profile https` em `https://localhost:7099`.

Não há Swagger nem interface pública de consulta aos dados. Os sites existentes
não foram alterados nem conectados a esta API.

## Testar

```powershell
dotnet run --project tests/Simpala.Site.Api.Tests
```

O executor usa apenas a biblioteca padrão, inicia instâncias isoladas da API e
bancos temporários. Retorna código de falha se qualquer verificação falhar.
Não exige credenciais, certificado do Entra nem serviços de e-mail.

Para gerar uma prévia manual:

```powershell
powershell -File scripts/Test-Local.ps1
```

## Contrato local

1. `GET /api/contact/challenge` retorna `token`, `question`, `expiresAt`.
2. `POST /api/contact/financeira` ou `/api/contact/consorcio` recebe JSON e
   o cabeçalho `Idempotency-Key` com um UUID no formato `xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx`.

```json
{
  "name": "Cliente Exemplo",
  "email": "cliente@exemplo.com",
  "document": "529.982.247-25",
  "phone": "(51) 99999-9999",
  "message": "Preciso de atendimento.",
  "contactPreference": "",
  "challengeToken": "TOKEN_DO_DESAFIO",
  "challengeAnswer": "RESPOSTA",
  "website": ""
}
```

Nome: 2–150 caracteres; e-mail: até 254; documento: até 18; telefone: até 20;
mensagem: até 1.000. O campo `website` é uma armadilha e deve ficar vazio.
Propriedades desconhecidas são rejeitadas, inclusive endereços arbitrários de destino.
`contactPreference` é opcional e ignorada; nenhum aceite é inferido.

Resposta `200`: protocolo, `state: Pending`, `simulation: true`, aviso explícito e
`preview` com remetente, destinatário, assunto e corpo. Não significa entrega nem chamado criado.
Validação/desafio inválido: `400`; conteúdo diferente na mesma chave: `409`;
limite de corpo: `413`; limite por IP: `429`; serviço público indisponível: `503` em HTTPS.

Mesma empresa/chave e mesmos dados normalizados reutilizam protocolo e data,
inclusive após reiniciar. Mesma chave com conteúdo diferente retorna conflito.
Uma nova chave requer um novo desafio. Repetições idempotentes não precisam de
um desafio ainda válido, mas devem manter os campos obrigatórios do contrato.

São permitidas 10 chamadas por IP/minuto, incluindo a obtenção de desafios.
O desafio de soma é uma proteção inicial, **não uma proteção suficiente contra bots
sofisticados**. A estratégia de produção será definida antes da exposição pública.

## E-mail e pendências do parser

- Caixas mapeadas no servidor; remetente e destinatário iguais.
- Corpo em texto puro; prévia declara UTF-8 e usa CRLF.
- Data persistida em UTC e formatada em America/Sao_Paulo.
- Rótulo `CPF:` preservado também para CNPJ.
- `Aceito receber contato por:` e `Política de Privacidade:` ficam vazios.
- Mensagem é convertida para uma linha, para não permitir criação de campos falsos do parser.
- Ambiente HML acrescenta `[TESTE HML]` ao início do campo Mensagem.

O assunto, os rótulos com valores vazios, os prefixos de protocolo, as quebras de
linha e o CNPJ alfanumérico ainda exigem homologação com o SimDesk.
Nenhuma representação recebida no Exchange é presumida correta sem conferência.

## Configuração e dados

`appsettings.{Development,HML,Production}.json` mantêm envio desabilitado.
`Contact:DataDirectory` aceita um caminho absoluto para o banco e as chaves de proteção;
o padrão é `App_Data`, fora de qualquer pasta pública. Nunca publique essa pasta como conteúdo estático.
No Windows, as chaves dos desafios são protegidas com DPAPI para a identidade do processo.
Restrinja o diretório por ACL; em outros sistemas, proteja as chaves no armazenamento do servidor.

`Contact:RetentionDays` tem padrão de **30 dias somente para demonstração local**,
com limpeza na inicialização e a cada seis horas. O responsável da empresa deve
aprovar a política de retenção e backups antes de produção. Após expirar a retenção,
a mesma chave poderá gerar outro protocolo: a janela deve cobrir as retentativas permitidas.

CORS não autoriza nenhuma origem por padrão. Se necessário, configure
`Contact:AllowedOrigins` com origens HTTPS exatas. CORS não substitui proteção antispam.
Não confie em `X-Forwarded-For` arbitrário; na implantação, configure somente proxies conhecidos.

## Próxima etapa

Reutilizar o aplicativo existente do SimDesk no Entra, conferir sua autenticacao e
`Application Mail.Send` via RBAC restrito às duas caixas. Depois implementar o transporte,
persistência segura do conteúdo de envio, concorrência de despacho e reconciliação
para `Sending`/`Unknown`. Um `202` do Graph será registrado como aceitação, não confirmação de entrega.

Nenhum envio real, inclusive HML, está autorizado nesta entrega.
Consulte [homologação](docs/homologacao.md) e [IIS](docs/iis.md).

## Teste de autenticacao no Entra (sem envio)

TenantId e ClientId estao configurados em `SupportMail` no appsettings.json.
O script abaixo solicita o valor do client secret de forma oculta e o utiliza
somente nesta execucao. Nao grava o segredo, nao imprime o token e nao envia e-mails.

```powershell
powershell -NoProfile -File scripts/Test-GraphAuthentication.ps1
```

Resultado esperado: `Autenticacao no Entra: OK`. Esse resultado confirma apenas
obtencao do token; as permissoes e o escopo das caixas exigem verificacao separada.
A autenticacao deste script ainda nao esta ligada ao transporte da API.
