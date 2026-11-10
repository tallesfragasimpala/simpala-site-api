# SIMPALA — API Fale Conosco

ASP.NET Core / .NET 10. Mesma aplicação e ambiente Production no computador e no IIS.
Os sites continuam estáticos e separados. Fluxo: formulário → API → Microsoft Graph
→ caixa de Fale Conosco → SimDesk. Não há serviço Windows adicional para esta API.

## Rodar localmente

Na pasta da API:

```powershell
powershell -NoProfile -File scripts/Start-Local.ps1
```

O script solicita a chave da Criptool com entrada oculta quando há segredo criptografado
configurado (caso contrário, solicita o ClientSecret), valida autenticação Microsoft sem
enviar e-mails e inicia a API em https://localhost:7099, ambiente Production.
Enviar o formulário envia e-mail real. O acesso continua restrito ao computador local.
Credenciais não são gravadas. Variáveis do processo são restauradas ao encerrar.
Requisito: SDK .NET 10 e certificado HTTPS de desenvolvimento confiável.

Em outro terminal, disponibilizar os sites (ajustar o caminho se necessário):

```powershell
python -m http.server 8000 --bind 127.0.0.1 --directory "C:\Users\talles.fraga\projects\Sites"
```

Abrir http://127.0.0.1:8000/simpala-financeira/ ou /simpala-consorcios/.

Não existe seleção HML/PRD para o envio da API. Se o SimDesk estiver em TestMode,
o usuário digita [TESTE HML] no início da Mensagem; nenhuma tag é adicionada pela API.
Os dados antigos de App_Data/HML são preservados e não são enviados ou migrados
automaticamente. A execução local usa App_Data/Local, separada do diretório definitivo
que será configurado no servidor. Recibos locais antigos não são reutilizados pelo site.

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

## Criptografia da credencial

SupportMail:EncryptedClientSecret contém o resultado Base64 da Criptool;
SupportMail:EncryptionIV contém seu IV de 16 bytes UTF-8. A chave de 32 bytes UTF-8
fica somente na variável de ambiente SupportMail__EncryptionKey, fora do JSON e do código.
O script local solicita a chave por sessão; não a grava. A variável de ambiente não é
criptografada automaticamente. Não configurar ClientSecret e EncryptedClientSecret juntos.
Formato compatível: AES-256-CBC, PKCS7, sem derivação da chave. Esse formato legado
não autentica o conteúdo criptografado. O segredo é descriptografado em memória.
