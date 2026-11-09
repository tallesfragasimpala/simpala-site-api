# Publicação no IIS

1. Instalar IIS e Hosting Bundle ASP.NET Core .NET 10 atualizado.
2. Site/aplicação separado do SimDesk, Application Pool dedicado, No Managed Code,
   um worker; configurar Start Mode AlwaysRunning e preload se desejar aquecimento.
3. HTTPS válido, DNS e binding do domínio definido pela infraestrutura.
4. Copiar artifacts/publish para a pasta da aplicação. Não copiar fontes ou App_Data.
5. Configurar as variáveis listadas no README no ambiente da aplicação/servidor.
   Credenciais protegidas e fora do pacote; não compartilhar arquivos contendo segredo.
6. Dados fora da pasta publicada; ACL de escrita somente para a identidade da API.
   Load User Profile habilitado para DPAPI; planejar troca de identidade e backup.
7. AllowedHosts e origens HTTPS exatas. Limite IIS de corpo: 16 KiB.
8. Configurar os sites para o domínio da API, com allowSending:true.
9. Validar GET /health, carregamento do desafio, POST autorizado de cada empresa,
   recebimento do e-mail e criação do chamado com documento completo no SimDesk.

Produção não roda scripts Start-Local nem Python. IIS gerencia o processo .NET.
Não registrar corpos, credenciais, tokens ou dados pessoais. Monitorar erros de
autenticação (HTTP/código Entra sanitizados), disponibilidade e tentativas Unknown.
Não aceitar X-Forwarded-For arbitrário. Validar o IP e Request.IsHttps na hospedagem;
proxies adicionais exigem configuração explícita de confiança.

Manter banco e chaves entre publicações; não sobrescrever com dados de teste.
Backup consistente antes de atualizar; restauração exige reconciliar envios posteriores
ao backup para não duplicar chamados. Migrações antigas preservam modos HML/Simulation
somente como histórico, sem reenvio automático; não são ambientes operacionais novos.

Concluir antispam, escopo das caixas, retenção e homologação antes de habilitar
Contact__EnablePublicAccess. O local valida o mesmo código em Production, mas não
substitui a validação de HTTPS, permissões e comunicação na infraestrutura final.
