# SIMPALA — API Fale Conosco

ASP.NET Core / .NET 10. Mesma aplicação e ambiente Production no computador e no IIS.
Os sites continuam estáticos e separados. Fluxo: formulário → API → Microsoft Graph
→ caixa de Fale Conosco → SimDesk. Não há serviço Windows adicional para esta API.

## Rodar localmente

Na pasta da API:

```powershell
powershell -NoProfile -File scripts/Start-Local.ps1
```

O script solicita o segredo com entrada oculta, valida autenticação Microsoft sem
enviar e-mails e inicia a API em https://localhost:7099, ambiente Production.
Enviar o formulário envia e-mail real. O acesso continua restrito ao computador local.
Credenciais não são gravadas. Variáveis do processo são restauradas ao encerrar.
Requisito: SDK .NET 10 e certificado HTTPS de desenvolvimento confiável.

Em outro terminal, disponibilizar os sites (ajustar o caminho se necessário):

```powershell
python -m http.server 8000 --bind 127.0.0.1 --directory "C:\Users\talles.fraga\projects\Sites"
```

Abrir http://127.0.0.1:8000/simpala-financeira/ ou /simpala-consorcios/.
Os domínios públicos dos sites permanecem sem endereço de API até a configuração com a infra.

Não existe seleção HML/PRD para o envio da API. Se o SimDesk estiver em TestMode,
o usuário digita [TESTE HML] no início da Mensagem; nenhuma tag é adicionada pela API.
Os dados antigos de App_Data/HML são preservados e não são enviados ou migrados
automaticamente. A execução local usa App_Data/Local, separada do diretório definitivo
que será configurado no servidor. Recibos locais antigos não são reutilizados pelo site.

## Configuração para IIS

```text
ASPNETCORE_ENVIRONMENT=Production
EmailSettings__EnableSending=true
Contact__EnablePublicAccess=true
Contact__DataDirectory=D:\Simpala\Dados\SiteApi
Contact__AllowedOrigins__0=https://DOMINIO-REAL-FINANCEIRA
Contact__AllowedOrigins__1=https://DOMINIO-REAL-CONSORCIOS
AllowedHosts=DOMINIO-REAL-DA-API
SupportMail__TenantId=...
SupportMail__ClientId=...
SupportMail__ClientSecret=...
```

O appsettings.json é a única base: envio e acesso externo vêm desabilitados para
evitar ativação acidental. No IIS, as configurações são provisionadas no servidor.
O script local habilita envio, limita CORS às origens locais da porta 8000 e mantém
acesso público bloqueado. Autenticar confirma credenciais, não permissão de envio.

Nos contact-config.js dos sites, configurar a URL HTTPS definitiva e allowSending:true.
Nunca colocar credenciais Microsoft nos sites. [Instruções de infraestrutura](docs/iis.md).

## Publicar

```powershell
dotnet publish src/Simpala.Site.Api -c Release -o artifacts/publish
```

Entregar somente artifacts/publish à infraestrutura. Não entregar código-fonte,
tests, scripts, App_Data ou credenciais. O SDK Web gera web.config para IIS.
A publicação usa o mesmo código testado localmente. Mudam endereços, credenciais,
diretório persistente e liberação de acesso externo.

## Testes

```powershell
dotnet run --project tests/Simpala.Site.Api.Tests -c Release
```

Os testes permanecem como ferramenta de desenvolvimento, fora do pacote da API.
Usam dados fictícios, bancos temporários e HTTP Microsoft interceptado, sem enviar e-mails.
Simulação existe somente para desenvolvimento/testes; não é outro fluxo operacional.

## Contrato e proteção contra duplicidade

- GET /health: processo disponível e modo; não prova entrega ou permissão Microsoft.
- GET /api/contact/challenge: pergunta, token protegido e validade.
- POST /api/contact/financeira ou /api/contact/consorcio: JSON e Idempotency-Key UUID.
- GET /api/contact/{company}/status: UUID no cabeçalho, somente estado e recibo.
- X-Contact-Mode impede que um cliente de simulação acione envio real.

Campos: name (2–150), email (254), document (18), phone (20), message (1000),
challengeToken, challengeAnswer, website (honeypot vazio). ContactPreference opcional
é ignorada. Propriedades desconhecidas são rejeitadas. CPF numérico e CNPJ numérico
ou alfanumérico são validados. Corpo limitado a 16 KiB; HTTPS obrigatório.

O estado é persistido antes de enviar: Pending → Sending → Accepted / Failed / Unknown.
Somente um processamento pode iniciar o envio. Alterar um Pending ainda não enviado
consome novo desafio e invalida o claim do conteúdo antigo. O navegador recupera
o resultado automaticamente, sem reenviar em segundo plano. Após envio confirmado,
uma nova ação explícita cria outra solicitação, mesmo com conteúdo igual.
Unknown nunca é reenviado automaticamente; exige conferência por protocolo no Exchange/SimDesk.
Graph 202 indica aceitação, sem provar entrega ou criação do chamado.

Assunto: Entre em contato. Texto puro UTF-8/CRLF; data America/Sao_Paulo.
Mesmo remetente e destinatário, fixos no servidor por empresa. Documento em CPF:
inclusive para CNPJ. Preferência/política não aparecem no e-mail. Registro fica no
corpo para rastreamento, sem aparecer nas mensagens do site. Mensagem normalizada
para uma linha impede injeção de campos no parser.

SQLite guarda recibos, hashes e estados, sem nome, documento, e-mail, telefone,
mensagem, segredo ou token. As chaves dos desafios usam DPAPI no Windows.
Retenção padrão: 30 dias; limpeza a cada seis horas. Sending/Unknown exigem resolução
administrativa e ficam fora da exclusão automática. A janela de idempotência depende
da retenção. Banco/chaves precisam de ACL e backup. A mesma instância SQLite deve
ser local ao servidor, sem compartilhamento ou múltiplos workers.

Antes de expor ao público: homologar o chamado, autorização Microsoft restrita às
duas caixas, antispam, retenção/backups e IP/HTTPS no IIS. Soma e honeypot são apenas
proteções básicas. Nenhum deploy, envio ou mudança no SimDesk é feito pela publicação.


## Entrega para infraestrutura

Scripts de configuração e instruções detalhadas em deploy/LEIAME-INFRA.md.
Gerar pacote com: powershell -NoProfile -File scripts/Build-Delivery.ps1.
Os domínios oficiais foram confirmados; a origem da API e pastas ainda dependem da infra.
O pacote gerado mantém o frontend público sem envio até Configure-Websites.ps1 ser executado.
Configure-Server.ps1 permite informar o segredo no servidor, criptografado por Data Protection
com DPAPI LocalMachine e ACL. A API aceita SupportMail:ClientSecretFile (caminho absoluto),
alternativo ao ClientSecret; não configurar as duas fontes simultaneamente.
Localmente Start-Local.ps1 continua solicitando o segredo em memória, sem alterar o fluxo.
