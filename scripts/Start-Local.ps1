$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot '..\src\Simpala.Site.Api'
$dataPath = [System.IO.Path]::GetFullPath((Join-Path $project 'App_Data\Local'))
$variables = @{
    'EmailSettings__EnableSending' = 'true'
    'Contact__EnablePublicAccess' = 'false'
    'Contact__DataDirectory' = $dataPath
}
$original = @{}
foreach ($name in @($variables.Keys) + @('SupportMail__ClientSecret')) {
    $original[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
}
$secret = Read-Host 'Client secret (entrada oculta; somente nesta sessao)' -AsSecureString
$pointer = [IntPtr]::Zero
try {
    if ($secret.Length -eq 0) { throw 'Informe o valor do client secret.' }
    $pointer = [System.Runtime.InteropServices.Marshal]::SecureStringToBSTR($secret)
    [Environment]::SetEnvironmentVariable('SupportMail__ClientSecret',
        [System.Runtime.InteropServices.Marshal]::PtrToStringBSTR($pointer), 'Process')
    foreach ($name in $variables.Keys) {
        [Environment]::SetEnvironmentVariable($name, $variables[$name], 'Process')
    }
    # Token-only preflight: reject invalid credentials before starting the public form endpoint.
    $settings = Get-Content (Join-Path $project 'appsettings.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    $tenant = [Environment]::GetEnvironmentVariable('SupportMail__TenantId', 'Process')
    $client = [Environment]::GetEnvironmentVariable('SupportMail__ClientId', 'Process')
    if (-not $tenant) { $tenant = $settings.SupportMail.TenantId }
    if (-not $client) { $client = $settings.SupportMail.ClientId }
    $tokenForm = @{
        grant_type = 'client_credentials'
        scope = 'https://graph.microsoft.com/.default'
        client_id = ([guid]$client).ToString()
        client_secret = [Environment]::GetEnvironmentVariable('SupportMail__ClientSecret', 'Process')
    }
    $authResult = $null
    try {
        $authResult = Invoke-RestMethod -Method Post -Uri ('https://login.microsoftonline.com/' + ([guid]$tenant).ToString() + '/oauth2/v2.0/token') -ContentType 'application/x-www-form-urlencoded' -Body $tokenForm -TimeoutSec 30
        if (-not $authResult.access_token) { throw 'AUTH_RESPONSE_INVALID' }
        Write-Host 'Autenticacao Microsoft validada. Nenhum e-mail enviado.' -ForegroundColor Green
    }
    catch {
        $authCode = 'indisponivel'
        try {
            $details = $_.ErrorDetails.Message | ConvertFrom-Json
            if ($details.error_codes.Count -gt 0 -and ([string]$details.error_codes[0]) -match '^[0-9]+$') {
                $authCode = [string]$details.error_codes[0]
            }
        } catch { }
        Write-Host ('Falha na autenticacao Microsoft. Codigo Entra: ' + $authCode + '. Confira o valor e a validade do segredo. Nenhum e-mail enviado.') -ForegroundColor Red
        throw 'A API nao foi iniciada: autenticacao nao validada.'
    }
    finally {
        $tokenForm.Clear()
        $authResult = $null
    }
    Write-Host 'API iniciada em Production para teste local. Enviar o formulario envia um e-mail real.' -ForegroundColor Yellow
    Write-Host 'Apenas iniciar o servidor nao envia e-mails. Encerre com Ctrl+C.'
    & dotnet run --project $project --no-launch-profile -- --environment Production --urls https://localhost:7099 --Contact:AllowedOrigins:0=http://localhost:8000 --Contact:AllowedOrigins:1=http://127.0.0.1:8000 --Contact:AllowedOrigins:2=http://localhost:5500 --Contact:AllowedOrigins:3=http://127.0.0.1:5500
    if ($LASTEXITCODE -ne 0) { throw 'A API nao concluiu a execucao normalmente. Confira a mensagem acima.' }
}
finally {
    foreach ($name in $original.Keys) {
        [Environment]::SetEnvironmentVariable($name, $original[$name], 'Process')
    }
    if ($pointer -ne [IntPtr]::Zero) { [System.Runtime.InteropServices.Marshal]::ZeroFreeBSTR($pointer) }
    $secret.Dispose()
}
